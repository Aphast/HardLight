// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 Redrover1760
// SPDX-FileCopyrightText: 2025 ark1368
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Mining;
using Content.Shared._Crescent.ShipShields;
using Content.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
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
using Robust.Shared.GameObjects;
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
    [Dependency] private readonly SharedMapSystem _map = default!;

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

            // MaxDraw caps the emitter's requested additional power load, but it should not
            // collapse the shield by itself. Keep the shield up until it actually overloads or
            // loses power.
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
            {
                // Emitters that are no longer on a grid must not keep a live shield behind.
                RemoveEmitterShield(uid, emitter);
                continue; // HardLight: return<continue
            }

            // If the emitter moved to a different grid, tear down old links before we evaluate
            // create/remove logic for the new parent grid.
            if (emitter.Shielded is { } previousGrid && previousGrid != parent.Value)
                RemoveEmitterShield(uid, emitter, previousGrid);

            // A grid can only have one ShipShieldedComponent marker/source. If another valid
            // emitter already owns this grid's shield, do not let this emitter churn ownership.
            if (TryComp<ShipShieldedComponent>(parent.Value, out var gridShielded)
                && gridShielded.Source is { } ownerEmitter
                && ownerEmitter != uid
                && IsValidShieldEntity(gridShielded.Shield, ownerEmitter, parent.Value))
            {
                emitter.Shield = null;
                emitter.Shielded = null;
                continue;
            }

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
        SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentShutdown>(OnEmitterShutdown); // Mono
        SubscribeLocalEvent<ShipShieldedComponent, MapInitEvent>(OnShieldedMapInit);
        // HardLight: key the grid-shape-change sub on ShipShieldedComponent rather than
        // MapGridComponent. PointCannonSystem already owns the
        // (MapGridComponent, GridFixtureChangeEvent) slot and Robust's directed bus only
        // allows one handler per (component, event) pair, so subscribing on MapGridComponent
        // here threw 'Duplicate Subscriptions' at startup. The event is raised directed on the
        // grid entity, which is also the entity that carries ShipShieldedComponent, so this
        // is functionally identical and the existing handler's TryComp guard is preserved
        // as a defensive no-op.
        SubscribeLocalEvent<ShipShieldedComponent, GridFixtureChangeEvent>(OnShieldedGridShapeChanged);

        InitializeCommands();
        InitializeEmitters();
    }

    private void OnShieldedMapInit(EntityUid uid, ShipShieldedComponent component, MapInitEvent args)
    {
        if (!IsValidShieldEntity(component.Shield, component.Source, uid))
        {
            if (Exists(component.Shield))
                QueueDel(component.Shield);

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
        if (HasComp<MeteorComponent>(args.OtherEntity))
        {
            var meteorShieldSource = component.Source;
            if (meteorShieldSource != null)
                BreakEmitterFromMeteorImpact(meteorShieldSource.Value);

            if (_projectileQuery.TryGetComponent(args.OtherEntity, out var meteorProjectile))
                meteorProjectile.ProjectileSpent = true;

            QueueDel(args.OtherEntity);
            args.Cancelled = true;
            return;
        }

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
        // Pass through projectiles fired by the same grid we are shielding. Use Weapon first,
        // then Shooter as fallback for projectiles that don't carry a weapon reference.
        var firingGrid = projectile.Weapon is { } weaponUid
            ? _transformSystem.GetGrid(weaponUid)
            : projectile.Shooter is { } shooterUid
                ? _transformSystem.GetGrid(shooterUid)
                : null;

        if (firingGrid == component.Shielded)
        {
            args.Cancelled = true;
            return;
        }

        // HardLight: EMP weapons (any round with EmpOnTrigger) bypass shields to detonate on the hull.
        if (HasComp<Content.Server.Emp.EmpOnTriggerComponent>(args.OtherEntity))
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

        // Foreign, non-EMP ship projectile: the shield INTERCEPTS it. The emitter absorbs the hit;
        // OnShieldDeflected also strips Explosive/trigger payloads so nothing detonates at the shield.
        if (component.Source is { } emitterSource)
        {
            var ev = new ShieldDeflectedEvent(args.OtherEntity, projectile);
            RaiseLocalEvent(emitterSource, ref ev);
        }

        // Guarantee the projectile is consumed even with no emitter source: just delete it. A deleted
        // projectile never runs its trigger/explosive payload, so it's gone with no explosion.
        projectile.ProjectileSpent = true;
        QueueDel(args.OtherEntity);

        // Cancel the physics collision so nothing bounces / triggers at the shield boundary.
        args.Cancelled = true;
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

            // Stale reference; ensure any orphaned shield entity is removed before recreate.
            if (Exists(existingShielded.Shield))
                QueueDel(existingShielded.Shield);

            RemComp<ShipShieldedComponent>(entity);
        }

        if (!Resolve(entity, ref mapGrid, false))
            return EntityUid.Invalid;

        var prototype = ShipShieldPrototype;
        var shieldBounds = GetShieldBounds(entity, mapGrid);

        // HardLight: spawn at the grid's map-space coordinates so the shield is created on the
        // correct map with a valid broadphase, then move it to the grid's AABB centre via
        // EntityCoordinates(entity, ...) which both reparents to the grid AND sets the local
        // position in a single transform update. The previous nullspace-spawn-then-SetParent
        // path briefly placed the body in nullspace (no broadphase) and SetParent preserves
        // world position by default, so the shield could land at world origin before
        // SetLocalPosition corrected it -- causing PVS/render races and visible mis-placement.
        var shield = Spawn(prototype, Transform(entity).Coordinates);
        var shieldPhysics = EnsureComp<PhysicsComponent>(shield);
        var shieldComp = EnsureComp<ShipShieldComponent>(shield);
        shieldComp.Shielded = entity;
        shieldComp.Source = source;

        // Copy shield color from the generator to the shield visuals
        var shieldVisuals = EnsureComp<ShipShieldVisualsComponent>(shield);
        if (source != null && TryComp<ShipShieldEmitterComponent>(source.Value, out var emitter))
        {
            shieldVisuals.ShieldColor = emitter.ShieldColor;
            shieldVisuals.Padding = emitter.ShieldPadding; // HardLight: per-emitter shield size
            Dirty(shield, shieldVisuals);
        }

        // HardLight: reparent to the grid and place at the *occupied-tile* bounds centre in a
        // single SetCoordinates call (atomic parent + local-position, avoids a transient
        // world-position drift between SetParent and SetLocalPosition).
        // shieldBounds is the AABB of actually-occupied tiles, NOT mapGrid.LocalAABB, so empty
        // expanded grid regions don't pull the shield off-centre.
        var gridCenter = new EntityCoordinates(entity, shieldBounds.Center);
        _transformSystem.SetCoordinates(shield, gridCenter);
        // Align rotation so the chain-shape oval matches the grid orientation. Without this,
        // rotated ships render and collide with a mis-aligned shield outline.
        _transformSystem.SetWorldRotation(shield, _transformSystem.GetWorldRotation(entity));

        var chain = GenerateOvalFixture(shield, "shield", shieldPhysics, shieldBounds, shieldVisuals.Padding);

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
            // HardLight: also HitscanImpassable.
            collisionLayer: (int)(CollisionGroup.BulletImpassable | CollisionGroup.HitscanImpassable),
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
            EnsureShieldAligned(shieldUid, gridUid);
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
            EnsureShieldAligned(shielded.Shield, gridUid);
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

        // HardLight perf: only run the full-world ShipShieldComponent scan when the targeted
        // paths above failed to locate a shield. In the common case (emitter map-init, normal
        // remove) the targeted lookup via emitter.Shield or the grid's ShipShieldedComponent
        // will find and clean up the shield, and this O(all-shields-in-world) scan finds
        // nothing. Skipping it makes ship-spawn cost O(1) per emitter instead of O(world-shields)
        // per emitter, which compounded to O(K^2) when K shielded ships were active.
        // The scan still runs in the genuine orphan case (no targeted hit) so cleanup
        // semantics for truly stale runtime shield entities are preserved.
        if (!removed)
        {
            var shieldQuery = EntityQueryEnumerator<ShipShieldComponent>();
            while (shieldQuery.MoveNext(out var shieldUid, out var shieldComp))
            {
                if (shieldComp.Source != emitterUid)
                    continue;

                QueueDel(shieldUid);
                removed = true;
            }
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

        if (sourceUid != null)
        {
            if (!Exists(sourceUid.Value))
                return false;

            if (!HasComp<ShipShieldEmitterComponent>(sourceUid.Value))
                return false;

            if (shieldComp.Source != sourceUid)
                return false;
        }

        if (!TryComp<FixturesComponent>(shieldUid, out var fixtures))
            return false;

        var fixture = _fixtureSystem.GetFixtureOrNull(shieldUid, "shield", fixtures);
        return fixture != null;
    }

    private void EnsureShieldAligned(EntityUid shieldUid, EntityUid gridUid)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var mapGrid))
            return;

        var shieldBounds = GetShieldBounds(gridUid, mapGrid);

        // HardLight: atomic parent + local-position, mirrors the spawn path so realignment
        // doesn't briefly preserve the old world position before being corrected.
        _transformSystem.SetCoordinates(shieldUid, new EntityCoordinates(gridUid, shieldBounds.Center));
        // Keep the chain-shape oval aligned with the grid orientation after realign too.
        _transformSystem.SetWorldRotation(shieldUid, _transformSystem.GetWorldRotation(gridUid));
    }

    private void OnShieldedGridShapeChanged(EntityUid uid, ShipShieldedComponent shielded, ref GridFixtureChangeEvent args)
    {
        if (!TryComp<MapGridComponent>(uid, out var component))
            return;

        RefreshShieldForGrid(uid, component, shielded);
    }

    private Box2 GetShieldBounds(EntityUid gridUid, MapGridComponent mapGrid)
    {
        var gotTile = false;
        var minX = 0;
        var minY = 0;
        var maxX = 0;
        var maxY = 0;

        var tileEnumerator = _map.GetAllTilesEnumerator(gridUid, mapGrid, ignoreEmpty: true);
        while (tileEnumerator.MoveNext(out var tileRef))
        {
            if (tileRef == null)
                continue;

            var tile = tileRef.Value;
            var x = tile.GridIndices.X;
            var y = tile.GridIndices.Y;

            if (!gotTile)
            {
                minX = x;
                minY = y;
                maxX = x + 1;
                maxY = y + 1;
                gotTile = true;
                continue;
            }

            if (x < minX)
                minX = x;
            if (y < minY)
                minY = y;
            if (x + 1 > maxX)
                maxX = x + 1;
            if (y + 1 > maxY)
                maxY = y + 1;
        }

        if (!gotTile)
            return mapGrid.LocalAABB;

        return new Box2(minX, minY, maxX, maxY);
    }

    private void RefreshShieldForGrid(EntityUid gridUid, MapGridComponent mapGrid, ShipShieldedComponent shielded)
    {
        if (!Exists(shielded.Shield)
            || !TryComp<ShipShieldComponent>(shielded.Shield, out var shieldComp)
            || shieldComp.Shielded != gridUid)
        {
            if (shielded.Source is { } source && TryComp<ShipShieldEmitterComponent>(source, out var emitter))
            {
                emitter.Shield = null;
                emitter.Shielded = null;
            }

            RemComp<ShipShieldedComponent>(gridUid);
            return;
        }

        var expectedBounds = GetShieldBounds(gridUid, mapGrid);
        EnsureShieldAligned(shielded.Shield, gridUid);

        if (!NeedsShieldFixtureRefresh(shielded.Shield, expectedBounds))
            return;

        RebuildShieldFixtures(shielded.Shield, mapGrid);
    }

    private bool NeedsShieldFixtureRefresh(EntityUid shieldUid, Box2 shieldBounds)
    {
        var xform = Transform(shieldUid);

        if ((xform.LocalPosition - shieldBounds.Center).LengthSquared() > 0.01f)
            return true;

        if (!TryComp<FixturesComponent>(shieldUid, out var fixtures))
            return true;

        if (_fixtureSystem.GetFixtureOrNull(shieldUid, "internalShield", fixtures) == null)
            return true;

        var fixture = _fixtureSystem.GetFixtureOrNull(shieldUid, "shield", fixtures);
        if (fixture?.Shape is not ChainShape chain)
            return true;

        var actualBounds = GetShapeBounds(chain.Vertices);
        var padding = TryComp<ShipShieldVisualsComponent>(shieldUid, out var visuals) ? visuals.Padding : 50f;
        var expectedWidth = shieldBounds.Width + padding;
        var expectedHeight = shieldBounds.Height + padding;
        var expectedBounds = new Box2(-expectedWidth / 2f, -expectedHeight / 2f, expectedWidth / 2f, expectedHeight / 2f);

        return Math.Abs(actualBounds.Width - expectedBounds.Width) > 0.1f
            || Math.Abs(actualBounds.Height - expectedBounds.Height) > 0.1f;
    }

    private void RebuildShieldFixtures(EntityUid shieldUid, MapGridComponent mapGrid)
    {
        if (!TryComp<PhysicsComponent>(shieldUid, out var physics)
            || !TryComp<FixturesComponent>(shieldUid, out var fixtures))
            return;

        var bounds = GetShieldBounds(Comp<ShipShieldComponent>(shieldUid).Shielded, mapGrid);
        var padding = TryComp<ShipShieldVisualsComponent>(shieldUid, out var visuals) ? visuals.Padding : 50f;

        _fixtureSystem.DestroyFixture(shieldUid, "shield", updates: false, body: physics, manager: fixtures);
        _fixtureSystem.DestroyFixture(shieldUid, "internalShield", updates: false, body: physics, manager: fixtures);

        var chain = GenerateOvalFixture(shieldUid, "shield", physics, bounds, padding);
        CreateInternalShieldFixture(shieldUid, physics, chain);
        _fixtureSystem.FixtureUpdate(shieldUid, manager: fixtures, body: physics);
        _physicsSystem.WakeBody(shieldUid, body: physics);
    }

    private void CreateInternalShieldFixture(EntityUid shieldUid, PhysicsComponent shieldPhysics, ChainShape chain)
    {
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

        _fixtureSystem.TryCreateFixture(shieldUid, internalPoly, "internalShield",
            hard: true,
            // HardLight: HitscanImpassable so rebuilt shields keep stopping energy beams (see ShieldEntity).
            collisionLayer: (int)(CollisionGroup.BulletImpassable | CollisionGroup.HitscanImpassable),
            updates: false,
            body: shieldPhysics);
    }

    private static Box2 GetShapeBounds(IReadOnlyList<Vector2> vertices)
    {
        var min = vertices[0];
        var max = vertices[0];

        for (var i = 1; i < vertices.Count; i++)
        {
            var vertex = vertices[i];
            min = Vector2.Min(min, vertex);
            max = Vector2.Max(max, vertex);
        }

        return new Box2(min, max);
    }
    // HardLight end

    private ChainShape GenerateOvalFixture(EntityUid uid, string name, PhysicsComponent physics, Box2 shieldBounds, float padding)
    {
        float radius;
        float scale;
        var scaleX = true;

        var height = shieldBounds.Height + padding;
        var width = shieldBounds.Width + padding;

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
