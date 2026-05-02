using Content.Server._Mono.FireControl;
using Content.Server.Emp;
using Content.Shared.Emp;
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;
using Robust.Shared.Spawners;
using System.Numerics;

namespace Content.Server._Mono.NPC.HTN;

public sealed partial class ShipTargetingSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly FireControlSystem _cannon = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private EntityQuery<GunComponent> _gunQuery;
    private EntityQuery<PhysicsComponent> _physQuery;

    // Reused per-tick to avoid allocating a HashSet for every ShipTargeting entity each weapon-check.
    private readonly HashSet<Entity<FireControllableComponent>> _scratchCannons = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShipEmpDisableGunsComponent, EmpPulseEvent>(OnEmpPulse);

        _gunQuery = GetEntityQuery<GunComponent>();
        _physQuery = GetEntityQuery<PhysicsComponent>();
    }

    private void OnEmpPulse(Entity<ShipEmpDisableGunsComponent> ent, ref EmpPulseEvent args)
    {
        args.Affected = true;
        args.Disabled = true;
        args = args with { Duration = args.Duration * ent.Comp.DurationMultiplier };
    }

    // have to use this because RT's is broken and unusable for navigation
    // another algorithm stolen from myself from orbitfight
    public Angle ShortestAngleDistance(Angle from, Angle to)
    {
        var diff = (to - from) % Math.Tau;
        return diff + Math.Tau * (diff < -Math.PI ? 1 : diff > Math.PI ? -1 : 0);
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<ShipTargetingComponent>();

        while (query.MoveNext(out var uid, out var comp))
        {
            var pilotXform = Transform(uid);

            var shipUid = pilotXform.GridUid;

            var target = comp.Target;
            var targetUid = target.EntityId; // if we have a target try to lead it

            if (shipUid == null
                || TerminatingOrDeleted(targetUid)
                || !_physQuery.TryComp(shipUid, out var shipBody)
                || !TryComp<MapGridComponent>(shipUid, out var shipGrid)
            )
                continue;

            var targetGrid = Transform(targetUid).GridUid;

            var shipXform = Transform(shipUid.Value);

            var mapTarget = _transform.ToMapCoordinates(target);
            var shipPos = _transform.GetMapCoordinates(shipXform);

            // we or target might just be in FTL so don't count us as finished
            if (mapTarget.MapId != shipPos.MapId)
                continue;

            if (HasComp<EmpDisabledComponent>(uid))
                continue;

            var linVel = shipBody.LinearVelocity;
            var targetVel = targetGrid == null ? Vector2.Zero : _physics.GetMapLinearVelocity(targetGrid.Value);
            var leadBy = 1f - MathF.Pow(1f - comp.LeadingAccuracy, frameTime);
            comp.CurrentLeadingVelocity = Vector2.Lerp(comp.CurrentLeadingVelocity, targetVel, leadBy);

            comp.WeaponCheckAccum -= frameTime;
            if (comp.WeaponCheckAccum < 0f)
            {
                comp.Cannons.Clear();
                _scratchCannons.Clear();
                _lookup.GetLocalEntitiesIntersecting(shipUid.Value, shipGrid.LocalAABB, _scratchCannons);
                foreach (var cannon in _scratchCannons)
                {
                    comp.Cannons.Add(cannon);
                }
                comp.WeaponCheckAccum += comp.WeaponCheckSpacing;
            }

            var allowHitscanFire = ShouldFireHitscan(comp, frameTime);
            FireWeapons(shipUid.Value, comp.Cannons, mapTarget, linVel, comp.CurrentLeadingVelocity, allowHitscanFire);
        }
    }

    private void FireWeapons(EntityUid shipUid, List<EntityUid> cannons, MapCoordinates destMapPos, Vector2 ourVel, Vector2 otherVel, bool allowHitscanFire)
    {
        var shipXform = Transform(shipUid);
        if (!_physQuery.TryComp(shipUid, out var shipBody))
            return;

        if (HasComp<EmpDisabledComponent>(shipUid))
            return;

        if (!_cannon.CanFireWeapons(shipUid))
            return;

        var shipAngVel = shipBody.AngularVelocity;
        var shipCenter = shipBody.LocalCenter;

        foreach (var uid in cannons)
        {
            if (TerminatingOrDeleted(uid))
                continue;

            var gXform = Transform(uid);

            if (!gXform.Anchored || !_gunQuery.TryComp(uid, out var gun))
                continue;

            var hitTime = 0f;
            var leadBy = Vector2.Zero;
            if (_gun.TryNextShootPrototype((uid, gun), out var proto))
            {
                var gunToDestVec = destMapPos.Position - _transform.GetWorldPosition(gXform);

                if (proto.TryGetComponent<HitscanBasicRaycastComponent>(out var raycast, Factory))
                {
                    // check if too far
                    if (raycast.MaxDistance < gunToDestVec.Length())
                        continue;

                    if (!allowHitscanFire)
                        continue;
                }
                else
                {
                    var centerToGunVec = gXform.LocalPosition - shipBody.LocalCenter;
                    // rotate 90deg left
                    var gunAngVel = new Vector2(-centerToGunVec.Y, centerToGunVec.X) * shipAngVel;
                    gunAngVel = shipXform.LocalRotation.RotateVec(gunAngVel);
                    leadBy = otherVel - ourVel - gunAngVel;

                    var gunToDestDir = NormalizedOrZero(gunToDestVec);

                    var projVel = gun.ProjectileSpeedModified;
                    var normVel = gunToDestDir * Vector2.Dot(leadBy, gunToDestDir);
                    var tgVel = leadBy - normVel;
                    // going too fast to the side, we can't possibly hit it
                    if (tgVel.Length() > projVel)
                        continue;

                    var normTarget = gunToDestDir * MathF.Sqrt(projVel * projVel - tgVel.LengthSquared());
                    // going too fast away, we can't hit it
                    if (Vector2.Dot(normTarget, normVel) > 0f && normVel.Length() > normTarget.Length())
                        continue;

                    var approachVel = (normTarget - normVel).Length();
                    hitTime = gunToDestVec.Length() / approachVel;

                    // might take too long to hit
                    var bulletProto = _gun.GetBulletPrototype(proto);
                    if (bulletProto.TryGetComponent<TimedDespawnComponent>(out var despawn, Factory) && hitTime > despawn.Lifetime)
                        continue;
                }
            }

            var targetMapPos = destMapPos.Offset(leadBy * hitTime);

            _cannon.AttemptFire(uid, uid, _transform.ToCoordinates(targetMapPos), noServer: true);
        }
    }

    private bool ShouldFireHitscan(ShipTargetingComponent comp, float frameTime)
    {
        if (!comp.HitscanBurstEnabled)
            return true;

        if (comp.HitscanReaimTimeRemaining > 0f)
        {
            comp.HitscanReaimTimeRemaining -= frameTime;
            if (comp.HitscanReaimTimeRemaining > 0f)
                return false;

            comp.HitscanBurstTimeRemaining = NextRandomInRange(comp.HitscanBurstMinDuration, comp.HitscanBurstMaxDuration);
        }

        if (comp.HitscanBurstTimeRemaining <= 0f)
            comp.HitscanBurstTimeRemaining = NextRandomInRange(comp.HitscanBurstMinDuration, comp.HitscanBurstMaxDuration);

        comp.HitscanBurstTimeRemaining -= frameTime;
        if (comp.HitscanBurstTimeRemaining > 0f)
            return true;

        comp.HitscanReaimTimeRemaining = NextRandomInRange(comp.HitscanReaimMinDelay, comp.HitscanReaimMaxDelay);
        return false;
    }

    private float NextRandomInRange(float min, float max)
    {
        var low = MathF.Min(min, max);
        var high = MathF.Max(min, max);
        return _random.NextFloat(low, high);
    }

    public Vector2 NormalizedOrZero(Vector2 vec)
    {
        return vec.LengthSquared() == 0 ? Vector2.Zero : vec.Normalized();
    }

    /// <summary>
    /// Adds the AI to the steering system to move towards a specific target.
    /// Returns null on failure.
    /// </summary>
    public ShipTargetingComponent? Target(Entity<ShipTargetingComponent?> ent, EntityCoordinates coordinates)
    {
        var xform = Transform(ent);
        var shipUid = xform.GridUid;

        if (!Resolve(ent, ref ent.Comp, false))
            ent.Comp = AddComp<ShipTargetingComponent>(ent);

        ent.Comp.Target = coordinates;

        return ent.Comp;
    }

    /// <summary>
    /// Stops the steering behavior for the AI and cleans up.
    /// </summary>
    public void Stop(Entity<ShipTargetingComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        RemComp<ShipTargetingComponent>(ent);
    }
}
