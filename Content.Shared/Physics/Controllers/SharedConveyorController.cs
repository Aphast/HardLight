using System.Numerics;
using Content.Shared.Conveyor;
using Content.Shared.Gravity;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Robust.Shared.Collections;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Threading;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared;

namespace Content.Shared.Physics.Controllers;

public abstract class SharedConveyorController : VirtualController
{
    [Dependency] protected readonly IMapManager MapManager = default!;
    [Dependency] private   readonly IParallelManager _parallel = default!;
    [Dependency] private   readonly CollisionWakeSystem _wake = default!;
    [Dependency] protected readonly EntityLookupSystem Lookup = default!;
    [Dependency] private   readonly FixtureSystem _fixtures = default!;
    [Dependency] private   readonly SharedGravitySystem _gravity = default!;
    [Dependency] private   readonly SharedMoverController _mover = default!;
    [Dependency] private   readonly IConfigurationManager _cfg = default!;

    protected const string ConveyorFixture = "conveyor";

    private ConveyorJob _job;

    private EntityQuery<ConveyorComponent> _conveyorQuery;
    private EntityQuery<ConveyedComponent> _conveyedQuery;
    protected EntityQuery<PhysicsComponent> PhysicsQuery;
    protected EntityQuery<TransformComponent> XformQuery;

    protected HashSet<EntityUid> Intersecting = new();

    // Cached CVar value. Previously HasPlayerInRange called _cfg.GetCVar(CVars.NetMaxUpdateRange)
    // for every conveyed entity, every tick - that's a dictionary lookup per item per frame.
    // Refreshed via OnValueChanged so behavior is identical.
    private float _netMaxUpdateRange;

    // Reused per-tick scratch buffers for UpdateBeforeSolve. Replaces a fresh Dictionary +
    // HashSet allocation every physics tick. Cleared at the top of each call.
    private readonly Dictionary<EntityUid, int> _selected = new();
    private readonly HashSet<EntityUid> _refreshConveyors = new();

    public override void Initialize()
    {
        _job = new ConveyorJob(this);
        _conveyorQuery = GetEntityQuery<ConveyorComponent>();
        _conveyedQuery = GetEntityQuery<ConveyedComponent>();
        PhysicsQuery = GetEntityQuery<PhysicsComponent>();
        XformQuery = GetEntityQuery<TransformComponent>();

        UpdatesAfter.Add(typeof(SharedMoverController));

        SubscribeLocalEvent<ConveyedComponent, TileFrictionEvent>(OnConveyedFriction);
        SubscribeLocalEvent<ConveyedComponent, ComponentStartup>(OnConveyedStartup);
        SubscribeLocalEvent<ConveyedComponent, ComponentShutdown>(OnConveyedShutdown);

        SubscribeLocalEvent<ConveyorComponent, StartCollideEvent>(OnConveyorStartCollide);
        SubscribeLocalEvent<ConveyorComponent, ComponentStartup>(OnConveyorStartup);

        _cfg.OnValueChanged(CVars.NetMaxUpdateRange, value => _netMaxUpdateRange = value, true);

        base.Initialize();
    }

    private void OnConveyedFriction(Entity<ConveyedComponent> ent, ref TileFrictionEvent args)
    {
        if(!ent.Comp.Conveying)
            return;

        // Conveyed entities don't get friction, they just get wishdir applied so will inherently slowdown anyway.
        args.Modifier = 0f;
    }

    private void OnConveyedStartup(Entity<ConveyedComponent> ent, ref ComponentStartup args)
    {
        // We need waking / sleeping to work and don't want collisionwake interfering with us.
        _wake.SetEnabled(ent.Owner, false);
    }

    private void OnConveyedShutdown(Entity<ConveyedComponent> ent, ref ComponentShutdown args)
    {
        _wake.SetEnabled(ent.Owner, true);
    }

    private void OnConveyorStartup(Entity<ConveyorComponent> ent, ref ComponentStartup args)
    {
        AwakenConveyor(ent.Owner);
    }

    /// <summary>
    /// Forcefully awakens all entities near the conveyor.
    /// </summary>
    protected virtual void AwakenConveyor(Entity<TransformComponent?> ent)
    {
    }

    /// <summary>
    /// Wakes all conveyed entities contacting this conveyor.
    /// </summary>
    protected void WakeConveyed(EntityUid conveyorUid)
    {
        var contacts = PhysicsSystem.GetContacts(conveyorUid);

        while (contacts.MoveNext(out var contact))
        {
            var other = contact.OtherEnt(conveyorUid);

            if (_conveyedQuery.HasComp(other))
            {
                PhysicsSystem.WakeBody(other);
            }
        }
    }

    private void OnConveyorStartCollide(Entity<ConveyorComponent> conveyor, ref StartCollideEvent args)
    {
        var otherUid = args.OtherEntity;

        if (!args.OtherFixture.Hard || args.OtherBody.BodyType == BodyType.Static)
            return;

        EnsureComp<ConveyedComponent>(otherUid);
    }

    public override void UpdateBeforeSolve(bool prediction, float frameTime)
    {
        base.UpdateBeforeSolve(prediction, frameTime);

        _job.Prediction = prediction;
        _job.Conveyed.Clear();

        var query = EntityQueryEnumerator<ConveyedComponent, FixturesComponent, PhysicsComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var fixtures, out var physics, out var xform))
        {
            _job.Conveyed.Add(((uid, comp, fixtures, physics, xform), Vector2.Zero, false, null, 0f));
        }

        _parallel.ProcessNow(_job, _job.Conveyed.Count);

        // Reuse the per-tick scratch dictionary instead of allocating a fresh one.
        _selected.Clear();

        for (var i = 0; i < _job.Conveyed.Count; i++)
        {
            var ent = _job.Conveyed[i];

            if (!ent.Result || ent.BestConveyor == null)
                continue;

            if (!_selected.TryGetValue(ent.BestConveyor.Value, out var existing) || ent.Priority > _job.Conveyed[existing].Priority)
            {
                _selected[ent.BestConveyor.Value] = i;
            }
        }

        for (var i = 0; i < _job.Conveyed.Count; i++)
        {
            var ent = _job.Conveyed[i];

            if (ent.BestConveyor != null && ent.Result && _selected[ent.BestConveyor.Value] != i)
            {
                ent.Result = false;
                _job.Conveyed[i] = ent;
            }
        }

        // Reuse the per-tick scratch hashset.
        _refreshConveyors.Clear();
        var refreshConveyors = _refreshConveyors;

        foreach (var ent in _job.Conveyed)
        {
            if (!ent.Entity.Comp3.Predict && prediction)
                continue;

            var physics = ent.Entity.Comp3;
            var wasConveying = ent.Entity.Comp1.Conveying;

            if (physics.BodyStatus != BodyStatus.OnGround)
            {
                SetConveying(ent.Entity.Owner, ent.Entity.Comp1, false);

                if (wasConveying && ent.BestConveyor != null)
                    refreshConveyors.Add(ent.BestConveyor.Value);

                continue;
            }

            var velocity = physics.LinearVelocity;
            var angularVelocity = physics.AngularVelocity;
            var targetDir = ent.Direction;

            // If mob is moving with the conveyor then combine the directions.
            var wishDir = _mover.GetWishDir(ent.Entity.Owner);

            if (Vector2.Dot(wishDir, targetDir) > 0f)
            {
                targetDir += wishDir;
            }

            var isConveying = ent.Result && targetDir.LengthSquared() > 0f;

            if (ent.Result)
            {
                SetConveying(ent.Entity.Owner, ent.Entity.Comp1, isConveying);

                // We apply friction here so when we push items towards the center of the conveyor they don't go overspeed.
                // We also don't want this to apply to mobs as they apply their own friction and otherwise
                // they'll go too slow.
                if (!_mover.UsedMobMovement.TryGetValue(ent.Entity.Owner, out var usedMob) || !usedMob)
                {
                    // We provide a small minimum friction speed as well for those times where the friction would stop large objects
                    // snagged on corners from sliding into the centerline.
                    _mover.Friction(0.2f, frameTime: frameTime, friction: 5f, ref velocity);
                    _mover.Friction(0f, frameTime: frameTime, friction: 5f, ref angularVelocity);
                }

                SharedMoverController.Accelerate(ref velocity, targetDir, 20f, frameTime);
            }
            else if (!_mover.UsedMobMovement.TryGetValue(ent.Entity.Owner, out var usedMob) || !usedMob)
            {
                // Need friction to outweigh the movement as it will bounce a bit against the wall.
                // This facilitates being able to sleep entities colliding into walls.
                _mover.Friction(0f, frameTime: frameTime, friction: 40f, ref velocity);
                _mover.Friction(0f, frameTime: frameTime, friction: 40f, ref angularVelocity);
            }

            PhysicsSystem.SetAngularVelocity(ent.Entity.Owner, angularVelocity);
            PhysicsSystem.SetLinearVelocity(ent.Entity.Owner, velocity, wakeBody: false);

            if (wasConveying && !isConveying && ent.BestConveyor != null)
                refreshConveyors.Add(ent.BestConveyor.Value);

            if (!IsConveyed((ent.Entity.Owner, ent.Entity.Comp2)))
            {
                if (ent.BestConveyor != null)
                    refreshConveyors.Add(ent.BestConveyor.Value);

                RemComp<ConveyedComponent>(ent.Entity.Owner);
            }
        }

        foreach (var conveyor in refreshConveyors)
        {
            WakeConveyed(conveyor);
        }
    }

    private void SetConveying(EntityUid uid, ConveyedComponent conveyed, bool value)
    {
        if (conveyed.Conveying == value)
            return;

        conveyed.Conveying = value;
        Dirty(uid, conveyed);
    }

    /// <summary>
    /// Gets the conveying direction for an entity.
    /// </summary>
    /// <returns>False if we should no longer be considered actively conveyed.</returns>
    private bool TryConvey(Entity<ConveyedComponent, FixturesComponent, PhysicsComponent, TransformComponent> entity,
        bool prediction,
        out Vector2 direction,
        out EntityUid? bestConveyorUid,
        out float priority)
    {
        direction = Vector2.Zero;
        bestConveyorUid = null;
        priority = 0f;

        // perf: check awake before HasPlayerInRange - the awake check is free while
        // HasPlayerInRange does an AABB entity lookup every tick per conveyed entity.
        if (!entity.Comp3.Awake)
            return true;

        if (!HasPlayerInRange(entity.Owner))
            return true;

        var fixtures = entity.Comp2;
        var physics = entity.Comp3;
        var xform = entity.Comp4;

        // Client moment
        if (!physics.Predict && prediction)
            return true;

        if (xform.GridUid == null)
            return true;

        if (physics.BodyStatus == BodyStatus.InAir ||
            _gravity.IsWeightless(entity, physics, xform))
        {
            return true;
        }

        Entity<ConveyorComponent> bestConveyor = default;
        var bestSpeed = 0f;
        var contacts = PhysicsSystem.GetContacts((entity.Owner, fixtures));
        var transform = PhysicsSystem.GetPhysicsTransform(entity.Owner);
        var anyConveyors = false;

        while (contacts.MoveNext(out var contact))
        {
            if (!contact.IsTouching)
                continue;

            // Check if our center is over their fixture otherwise ignore it.
            var other = contact.OtherEnt(entity.Owner);

            // Check for blocked, if so then we can't convey at all and just try to sleep
            // Otherwise we may just keep pushing it into the wall

            if (!_conveyorQuery.TryComp(other, out var conveyor))
                continue;

            anyConveyors = true;
            var otherFixture = contact.OtherFixture(entity.Owner);
            var otherTransform = PhysicsSystem.GetPhysicsTransform(other);

            // Check if our center is over the conveyor, otherwise ignore it.
            if (!_fixtures.TestPoint(otherFixture.Item2.Shape, otherTransform, transform.Position))
                continue;

            if (conveyor.Speed > bestSpeed && CanRun(conveyor))
            {
                bestSpeed = conveyor.Speed;
                bestConveyor = (other, conveyor);
            }
        }

        // If we have no touching contacts we shouldn't be using conveyed anyway so nuke it.
        if (!anyConveyors)
            return true;

        if (bestSpeed == 0f || bestConveyor == default)
            return true;

        var comp = bestConveyor.Comp!;
        var conveyorXform = XformQuery.GetComponent(bestConveyor.Owner);
        var (conveyorPos, conveyorRot) = TransformSystem.GetWorldPositionRotation(conveyorXform);

        conveyorRot += bestConveyor.Comp!.Angle;

        if (comp.State == ConveyorState.Reverse)
            conveyorRot += MathF.PI;

        var conveyorDirection = conveyorRot.ToWorldVec();
        direction = conveyorDirection;

        var itemRelative = conveyorPos - transform.Position;
        direction = Convey(direction, bestSpeed, itemRelative);

        bestConveyorUid = bestConveyor.Owner;
        priority = Vector2.Dot(transform.Position, conveyorDirection);

        // Do a final check for hard contacts so if we're conveying into a wall then NOOP.
        contacts = PhysicsSystem.GetContacts((entity.Owner, fixtures));

        while (contacts.MoveNext(out var contact))
        {
            if (!contact.Hard || !contact.IsTouching)
                continue;

            var other = contact.OtherEnt(entity.Owner);
            var otherBody = contact.OtherBody(entity.Owner);

            // If the blocking body is dynamic then don't ignore it for this.
            if (otherBody.BodyType != BodyType.Static)
                continue;

            var otherTransform = PhysicsSystem.GetPhysicsTransform(other);
            var dotProduct = Vector2.Dot(otherTransform.Position - transform.Position, direction);

            // TODO: This should probably be based on conveyor speed, this is mainly so we don't
            // go to sleep when conveying and colliding with tables perpendicular to the conveyance direction.
            if (dotProduct > 1.5f)
            {
                direction = Vector2.Zero;
                return false;
            }
        }

        return true;
    }

    private bool HasPlayerInRange(EntityUid uid)
    {
        // _netMaxUpdateRange is the cached CVar value; previously this was looked up via
        // _cfg.GetCVar per call (called once per conveyed item per tick).
        var coords = Transform(uid).Coordinates;

        foreach (var _ in Lookup.GetEntitiesInRange<ActorComponent>(coords, _netMaxUpdateRange))
        {
            return true;
        }

        return false;
    }
    private static Vector2 Convey(Vector2 direction, float speed, Vector2 itemRelative)
    {
        if (speed == 0 || direction.LengthSquared() == 0)
            return Vector2.Zero;

        /*
         * Basic idea: if the item is not in the middle of the conveyor in the direction that the conveyor is running,
         * move the item towards the middle. Otherwise, move the item along the direction. This lets conveyors pick up
         * items that are not perfectly aligned in the middle, and also makes corner cuts work.
         *
         * We do this by computing the projection of 'itemRelative' on 'direction', yielding a vector 'p' in the direction
         * of 'direction'. We also compute the rejection 'r'. If the magnitude of 'r' is not (near) zero, then the item
         * is not on the centerline.
         */

        var p = direction * (Vector2.Dot(itemRelative, direction) / Vector2.Dot(direction, direction));
        var r = itemRelative - p;

        if (r.Length() < 0.1)
        {
            var velocity = direction * speed;
            return velocity;
        }
        else
        {
            // Give a slight nudge in the direction of the conveyor to prevent
            // to collidable objects (e.g. crates) on the locker from getting stuck
            // pushing each other when rounding a corner.
            var velocity = (r + direction).Normalized() * speed;
            return velocity;
        }
    }

    public bool CanRun(ConveyorComponent component)
    {
        return component.State != ConveyorState.Off && component.Powered;
    }

    private record struct ConveyorJob : IParallelRobustJob
    {
        public int BatchSize => 16;

        public List<(Entity<ConveyedComponent, FixturesComponent, PhysicsComponent, TransformComponent> Entity,
            Vector2 Direction,
            bool Result,
            EntityUid? BestConveyor,
            float Priority)> Conveyed = new();

        public SharedConveyorController System;

        public bool Prediction;

        public ConveyorJob(SharedConveyorController controller)
        {
            System = controller;
        }

        public void Execute(int index)
        {
            var convey = Conveyed[index];

            var result = System.TryConvey(
                (convey.Entity.Owner, convey.Entity.Comp1, convey.Entity.Comp2, convey.Entity.Comp3, convey.Entity.Comp4),
                Prediction, out var direction, out var bestConveyor, out var priority);

            Conveyed[index] = (convey.Entity, direction, result, bestConveyor, priority);
        }
    }

    /// <summary>
    /// Checks an entity's contacts to see if it's still being conveyed.
    /// </summary>
    private bool IsConveyed(Entity<FixturesComponent?> ent)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return false;

        var contacts = PhysicsSystem.GetContacts(ent.Owner);

        while (contacts.MoveNext(out var contact))
        {
            if (!contact.IsTouching)
                continue;

            var other = contact.OtherEnt(ent.Owner);

            if (_conveyorQuery.HasComp(other))
                return true;
        }

        return false;
    }
}
