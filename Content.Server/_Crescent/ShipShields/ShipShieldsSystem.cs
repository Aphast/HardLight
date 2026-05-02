// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 Redrover1760
// SPDX-FileCopyrightText: 2025 ark1368
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Events;
using Robust.Server.GameStates;
using Content.Server.Power.Components;
using Robust.Shared.Physics;
using Content.Shared._Mono.SpaceArtillery;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged;
using Robust.Shared.Prototypes;
using Content.Shared.Weapons.Ranged.Events;
using SpaceArtilleryComponent = Content.Server._Mono.SpaceArtillery.Components.SpaceArtilleryComponent;


namespace Content.Server._Crescent.ShipShields;

public sealed partial class ShipShieldsSystem : EntitySystem
{
    private const string ShipShieldPrototype = "ShipShield";

    //private const float DeflectionSpread = 25f;
    private const float EmitterUpdateRate = 1.5f;

    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly FixtureSystem _fixtureSystem = default!;
    [Dependency] private readonly PhysicsSystem _physicsSystem = default!;
    [Dependency] private readonly PvsOverrideSystem _pvsSys = default!;

    private EntityQuery<ProjectileComponent> _projectileQuery;
    private EntityQuery<ShipWeaponProjectileComponent> _shipWeaponProjectileQuery;
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ShipShieldEmitterComponent, ApcPowerReceiverComponent>();
        while (query.MoveNext(out var uid, out var emitter, out var power))
        {
            emitter.Accumulator += frameTime;

            if (emitter.Accumulator < EmitterUpdateRate)
                continue;

            if (CalculateLoadDamage(emitter) >= emitter.MaxDraw)
                emitter.Recharging = true;
            if (!power.Powered)
                emitter.Recharging = true;

            emitter.Accumulator -= EmitterUpdateRate;
            if (emitter.OverloadAccumulator > 0)
            {
                emitter.OverloadAccumulator -= EmitterUpdateRate;
            }

            var healed = emitter.HealPerSecond * EmitterUpdateRate * CalculateRechargeMultiplier(emitter, power);

            emitter.Damage -= healed;

            if (emitter.Damage < 0)
            {
                emitter.Damage = 0;
                if (power.Powered)
                    emitter.Recharging = false;
            }

            AdjustEmitterLoad(uid, emitter, power);

            var parent = Transform(uid).GridUid;

            if (parent == null)
                continue; // HardLight: return<continue

            if (emitter.Damage > emitter.DamageLimit)
                emitter.OverloadAccumulator = emitter.DamageOverloadTimePunishment;

            // HardLight: Keep emitter shield reference in sync if shield was deleted externally.
            if (emitter.Shield != null && !Exists(emitter.Shield.Value))
                emitter.Shield = null;

            // Check if we need to create a shield (not recharging, no valid shield, not overloaded)
            if (!emitter.Recharging && !HasEmitterShield(uid, parent.Value, emitter) && emitter.OverloadAccumulator < 1) // HardLight: Exists(emitter.Shield)<HasEmitterShield(uid, parent.Value, emitter)
            {
                var shield = ShieldEntity(parent.Value, source: uid);
                if (shield != EntityUid.Invalid)
                {
                    emitter.Shield = shield;
                    emitter.Shielded = parent.Value;
                    _audio.PlayPvs(emitter.PowerUpSound, uid, emitter.PowerUpSound.Params);
                }
            }
            // Check if we need to remove shield (recharging or overloaded, and shield exists)
            // HardLight start
            else if (emitter.Recharging || emitter.OverloadAccumulator > 0)
            {
                if (RemoveEmitterShield(uid, emitter, parent.Value))
                    _audio.PlayPvs(emitter.PowerDownSound, uid, emitter.PowerDownSound.Params);
            }
            // HardLight end

        }
    }
    public override void Initialize()
    {
        base.Initialize();
        _projectileQuery = GetEntityQuery<ProjectileComponent>();
        _shipWeaponProjectileQuery = GetEntityQuery<ShipWeaponProjectileComponent>();

        SubscribeLocalEvent<ShipShieldComponent, PreventCollideEvent>(OnPreventCollide);
        SubscribeLocalEvent<ShipShieldComponent, HitScanReflectAttemptEvent>(OnShieldHitscanHit); // Mono - intercept ship-weapon hitscans
        SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentShutdown>(OnEmitterShutdown); // Mono
        SubscribeLocalEvent<ShipShieldedComponent, MapInitEvent>(OnShieldedMapInit);

        InitializeCommands();
        InitializeEmitters();
    }

    private void OnShieldedMapInit(EntityUid uid, ShipShieldedComponent component, MapInitEvent args)
    {
        if (!IsValidShieldEntity(component.Shield, component.Source, uid))
        {
            RemComp<ShipShieldedComponent>(uid);
            return;
        }

        if (component.Source is { } source && TryComp<ShipShieldEmitterComponent>(source, out var emitter))
        {
            emitter.Shield = component.Shield;
            emitter.Shielded = uid;
        }

        if (TryComp<ShipShieldComponent>(component.Shield, out var shield))
        {
            shield.Shielded = uid;
            shield.Source = component.Source;
        }
    }

    private void OnPreventCollide(EntityUid uid, ShipShieldComponent component, ref PreventCollideEvent args)
    {
        // only handle ship weapons for now. engine update introduced physics regressions. Let's polish everything else and circle back yeah?
        if (!_shipWeaponProjectileQuery.HasComponent(args.OtherEntity) ||
        !_projectileQuery.TryGetComponent(args.OtherEntity, out var projectile) ||
        projectile.ProjectileSpent)
        {
            args.Cancelled = true;
            return;
        }

        // Don't deflect/consume projectiles fired from the same grid we're shielding. This was
        // previously claimed to be handled by ProjectileGridPhaseComponent, but that component
        // does not exist in this fork. SpaceArtillerySystem cancels the same-grid contact on the
        // projectile-side PreventCollideEvent, but each side gets a fresh event with Cancelled=false
        // so the order in which the two handlers run is non-deterministic. Without this check, a
        // ship's own ship-weapon fire frequently collides with its own shield on the first physics
        // step, gets QueueDel'd, and damages the emitter -- which players see as "shields don't
        // work" / "guns don't shoot through our shield".
        //
        // HardLight: Also pass through if Weapon is null. This happens on the very first physics
        // step after a projectile spawns before the gun system has had a chance to set the Weapon
        // field. Without this guard, projectiles fired from inside another ship's shield (or from
        // a ship whose own shield geometry overlaps its hull) are immediately consumed because the
        // null-weapon check falls through to the deflect path.
        if (projectile.Weapon == null
            || (_transformSystem.GetGrid(projectile.Weapon.Value) == component.Shielded))
        {
            args.Cancelled = true;
            return;
        }

        //if (TryComp<TimedDespawnComponent>(args.OtherEntity, out var despawn))
        //    despawn.Lifetime += despawn.Lifetime;

        // I originally tried reflection but the math is too hard with the fucked coordinate system in this game (WorldRotation can be negative. Vector to Angle conversion loses information. Etc etc.)
        // Might try again at some point using just vector math with this (https://math.stackexchange.com/questions/13261/how-to-get-a-reflection-vector)
        //var deflectionVector = Transform(args.OtherEntity).WorldPosition - Transform(uid).WorldPosition;
        //var angle = _random.NextFloat(DeflectionSpread);

        //if (_random.Prob(0.5f))
        //    angle = -angle;

        //deflectionVector = new Vector2((float) (Math.Cos(angle) * deflectionVector.X - Math.Sin(angle) * deflectionVector.Y), (float) (Math.Sin(angle) * deflectionVector.X - Math.Cos(angle) * deflectionVector.Y));

        // instead of reflecting the projectile, just delete it. this works better for gameplay and intuiting what is going on in a fight.
        // why shoot the projectile again when you can just 180 its physics, tho?
        //_gun.ShootProjectile(args.OtherEntity, deflectionVector, _physicsSystem.GetMapLinearVelocity(uid), uid, null, velocity.Length());

        if (component.Source is { } source)
        {
            var ev = new ShieldDeflectedEvent(args.OtherEntity, projectile);
            RaiseLocalEvent(source, ref ev);
        }
    }

    /// <summary>
    /// Handles hitscan (laser/energy) weapons fired by ship weapon turrets hitting the shield.
    /// Absorbs the shot and deals damage to the emitter so shields are meaningful against energy weapons.
    /// Regular crew handheld weapons are intentionally excluded via the SpaceArtillery check.
    /// </summary>
    private void OnShieldHitscanHit(EntityUid uid, ShipShieldComponent component, ref HitScanReflectAttemptEvent args)
    {
        // Only intercept ship-weapon turret fire (SpaceArtillery component on the gun entity).
        // This lets crew handheld laser fire pass through the shield from inside the ship.
        if (!HasComp<SpaceArtilleryComponent>(args.SourceItem))
            return;

        // Get the hitscan damage from the ammo provider attached to the gun.
        if (!TryComp<HitscanBatteryAmmoProviderComponent>(args.SourceItem, out var ammoProvider))
            return;

        if (!_prototypeManager.TryIndex<HitscanPrototype>(ammoProvider.Prototype, out var hitscanProto))
            return;

        var totalDamage = hitscanProto.Damage?.GetTotal() ?? 0;
        if (totalDamage <= 0)
            return;

        if (component.Source is { } source)
        {
            var ev = new ShieldHitscanDeflectedEvent((float) totalDamage);
            RaiseLocalEvent(source, ref ev);
        }

        // Do NOT set args.Reflected = true — we want the ray to terminate at the shield,
        // not bounce back. GunSystem will call TryChangeDamage on the shield entity which
        // has no DamageableComponent, so no further damage occurs. The hitscan is absorbed.
    }

    private void OnEmitterShutdown(EntityUid uid, ShipShieldEmitterComponent emitter, ComponentShutdown args) // Mono
    {
        // HardLight start
        var parent = Transform(uid).GridUid;
        if (parent == null || !Exists(parent.Value) || Terminating(parent.Value))
        {
            RemoveEmitterShield(uid, emitter);
            return;
        }

        RemoveEmitterShield(uid, emitter, parent.Value);
        // HardLight end
    }

    /// <summary>
    /// Produces a shield around a grid entity, if it doesn't already exist.
    /// </summary>
    /// <param name="entity">The entity being shielded.</param>
    /// <param name="mapGrid">The map grid component of the entity being shielded.</param>
    /// <param name="source">A shield generator or similar providing the shield for the entity</param>
    /// <returns>The shield entity.</returns>
    private EntityUid ShieldEntity(EntityUid entity, MapGridComponent? mapGrid = null, EntityUid? source = null)
    {
        // HardLight: also check Exists() — if the shield was externally deleted the component
        // lingers on the grid pointing at a dead UID, which would suppress recreation forever.
        if (TryComp<ShipShieldedComponent>(entity, out var existingShielded))
        {
            if (IsValidShieldEntity(existingShielded.Shield, source, entity))
            {
                return existingShielded.Shield;
            }

            // Stale reference; remove the dead component so we fall through to recreate.
            RemComp<ShipShieldedComponent>(entity);
        }

        if (!Resolve(entity, ref mapGrid, false))
            return EntityUid.Invalid;

        var prototype = ShipShieldPrototype;

        // HardLight: spawn in nullspace first, then parent to the grid before positioning.
        // Previously the shield was spawned at Transform(entity).Coordinates (map-space) and
        // SetLocalPosition was called while the shield was still a child of the MAP, so the
        // AABB-centre value (a small grid-local number) became the shield's map-space position,
        // placing it near the world origin instead of on the ship.
        var shield = Spawn(prototype);
        var shieldPhysics = EnsureComp<PhysicsComponent>(shield);
        var shieldComp = EnsureComp<ShipShieldComponent>(shield);
        shieldComp.Shielded = entity;
        shieldComp.Source = source;

        // Copy shield color from the generator to the shield visuals
        var shieldVisuals = EnsureComp<ShipShieldVisualsComponent>(shield);
        if (source != null && TryComp<ShipShieldEmitterComponent>(source.Value, out var emitter))
        {
            shieldVisuals.ShieldColor = emitter.ShieldColor;
            Dirty(shield, shieldVisuals);
        }

        // Parent first so SetLocalPosition is interpreted in grid-local space.
        _transformSystem.SetParent(shield, entity);
        _transformSystem.SetLocalPosition(shield, mapGrid.LocalAABB.Center);

        var chain = GenerateOvalFixture(shield, "shield", shieldPhysics, mapGrid, shieldVisuals.Padding);

        List<Vector2> roughPoly = new();

        var interval = chain.Count / PhysicsConstants.MaxPolygonVertices;

        int i = 0;

        while (i < PhysicsConstants.MaxPolygonVertices)
        {
            roughPoly.Add(chain.Vertices[i * interval]);
            i++;
        }

        var internalPoly = new PolygonShape();
        internalPoly.Set(roughPoly);

        _fixtureSystem.TryCreateFixture(shield, internalPoly, "internalShield",
            hard: true,
            collisionLayer: (int)CollisionGroup.BulletImpassable, // Mono - Only try to block bullets
            body: shieldPhysics);

        _physicsSystem.SetCanCollide(shield, true, body: shieldPhysics);
        _physicsSystem.WakeBody(shield, body: shieldPhysics);
        _physicsSystem.SetSleepingAllowed(shield, shieldPhysics, false);

        _pvsSys.AddGlobalOverride(shield);

        var shieldedComp = EnsureComp<ShipShieldedComponent>(entity);
        shieldedComp.Shield = shield;
        shieldedComp.Source = source;

        return shield;
    }

    private bool UnshieldEntity(EntityUid uid, ShipShieldedComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return false;

        Del(component.Shield);
        RemComp<ShipShieldedComponent>(uid);
        return true;
    }

    // 
    // HardLight start
    private bool HasEmitterShield(EntityUid emitterUid, EntityUid gridUid, ShipShieldEmitterComponent emitter)
    {
        if (emitter.Shield is { } shieldUid && IsValidShieldEntity(shieldUid, emitterUid, gridUid))
        {
            emitter.Shielded = gridUid;
            return true;
        }

        // Stale or mismatched shield reference; allow recreation.
        emitter.Shield = null;
        emitter.Shielded = null;

        if (TryComp<ShipShieldedComponent>(gridUid, out var shielded)
            && shielded.Source == emitterUid
            && IsValidShieldEntity(shielded.Shield, emitterUid, gridUid))
        {
            emitter.Shield = shielded.Shield;
            emitter.Shielded = gridUid;
            return true;
        }

        return false;
    }

    private bool RemoveEmitterShield(EntityUid emitterUid, ShipShieldEmitterComponent emitter, EntityUid? gridUid = null)
    {
        var removed = false;

        if (emitter.Shield != null
            && Exists(emitter.Shield.Value)
            && TryComp<ShipShieldComponent>(emitter.Shield.Value, out var emitterShield)
            && emitterShield.Source == emitterUid)
        {
            QueueDel(emitter.Shield.Value);
            removed = true;
        }

        if (gridUid != null && TryComp<ShipShieldedComponent>(gridUid.Value, out var shielded) && shielded.Source == emitterUid)
        {
            if (Exists(shielded.Shield))
            {
                QueueDel(shielded.Shield);
                removed = true;
            }

            RemComp<ShipShieldedComponent>(gridUid.Value);
        }
        else
        {
            // Fallback: cleanup any stale shielded markers that still point to this emitter.
            var shieldedQuery = EntityQueryEnumerator<ShipShieldedComponent>();
            while (shieldedQuery.MoveNext(out var shieldedUid, out var shieldedComp))
            {
                if (shieldedComp.Source != emitterUid)
                    continue;

                if (Exists(shieldedComp.Shield))
                {
                    QueueDel(shieldedComp.Shield);
                    removed = true;
                }

                RemComp<ShipShieldedComponent>(shieldedUid);
            }
        }

        var shieldQuery = EntityQueryEnumerator<ShipShieldComponent>();
        while (shieldQuery.MoveNext(out var shieldUid, out var shieldComp))
        {
            if (shieldComp.Source != emitterUid)
                continue;

            QueueDel(shieldUid);
            removed = true;
        }

        emitter.Shield = null;
        emitter.Shielded = null;
        return removed;
    }

    private bool IsValidShieldEntity(EntityUid shieldUid, EntityUid? sourceUid, EntityUid gridUid)
    {
        if (!Exists(shieldUid))
            return false;

        if (!TryComp<ShipShieldComponent>(shieldUid, out var shieldComp))
            return false;

        if (shieldComp.Shielded != gridUid)
            return false;

        if (sourceUid != null && shieldComp.Source != sourceUid)
            return false;

        if (!TryComp<FixturesComponent>(shieldUid, out var fixtures))
            return false;

        var fixture = _fixtureSystem.GetFixtureOrNull(shieldUid, "shield", fixtures);
        return fixture != null;
    }
    // HardLight end

    private ChainShape GenerateOvalFixture(EntityUid uid, string name, PhysicsComponent physics, MapGridComponent mapGrid, float padding)
    {
        float radius;
        float scale;
        var scaleX = true;

        var height = mapGrid.LocalAABB.Height + padding;
        var width = mapGrid.LocalAABB.Width + padding;

        if (width > height)
        {
            radius = 0.5f * height;
            scale = width / height;
        }
        else
        {
            radius = 0.5f * width;
            scale = height / width;
            scaleX = false;
        }

        var chain = new ChainShape();

        chain.CreateLoop(Vector2.Zero, radius);

        for (int i = 0; i < chain.Vertices.Length; i++)
        {
            if (scaleX)
            {
                chain.Vertices[i].X *= scale;
            }
            else
            {
                chain.Vertices[i].Y *= scale;
            }
        }

        _fixtureSystem.TryCreateFixture(uid, chain, name,
            hard: false,
            collisionLayer: (int)(CollisionGroup.BulletImpassable | CollisionGroup.HitscanImpassable), // Mono - blocks both projectiles and hitscan (laser/energy) weapons
            body: physics);

        return chain;
    }

    [ByRefEvent]
    public record struct ShieldDeflectedEvent(EntityUid Deflected, ProjectileComponent Projectile)
    {

    }

    /// <summary>
    /// Raised on a shield emitter when a ship-weapon hitscan (laser/energy) beam is absorbed by the shield.
    /// </summary>
    [ByRefEvent]
    public record struct ShieldHitscanDeflectedEvent(float Damage)
    {

    }
}
