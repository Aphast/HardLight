using Content.Server.Administration.Logs;
using Content.Server.Destructible;
using Content.Server.Effects;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Camera;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Database;
using Content.Shared.FixedPoint;
using Content.Shared.Projectiles;
using Content.Shared.Physics;
using Content.Shared.Whitelist; // HardLight
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics; // Mono
using Robust.Shared.Physics.Events; // HardLight - PreventCollideEvent in anti-tunnel raycast
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using System.Linq;
using System.Numerics;

namespace Content.Server.Projectiles;

public sealed class ProjectileSystem : SharedProjectileSystem
{
    [Dependency] private readonly DestructibleSystem _destructibleSystem = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!; // HardLight

    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;

    private EntityQuery<PhysicsComponent> _physQuery;
    private EntityQuery<FixturesComponent> _fixQuery;

    /// <summary>
    /// Minimum velocity for a projectile to be considered for raycast hit detection.
    /// Projectiles slower than this will rely on standard StartCollideEvent.
    /// </summary>
    private const float MinRaycastVelocity = 75f; // 100->75 Mono

    public override void Initialize()
    {
        base.Initialize();

        // Mono
        _physQuery = GetEntityQuery<PhysicsComponent>();
        _fixQuery = GetEntityQuery<FixturesComponent>();

        // Mono
        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
    }

    public override DamageSpecifier? ProjectileCollide(Entity<ProjectileComponent, PhysicsComponent> projectile, EntityUid target, MapCoordinates? collisionCoordinates, bool predicted = false)
    {
        var (uid, component, ourBody) = projectile;
        // Check if projectile is already spent (server-specific check)
        if (component.ProjectileSpent)
            return null;

        if (TryComp<ProjectileTargetWhitelistComponent>(uid, out var targetFilter) // HardLight
            && !_whitelist.CheckBoth(target, targetFilter.Blacklist, targetFilter.Whitelist))
        {
            return null;
        }

        var otherName = ToPrettyString(target);
        // Get damage required for destructible before base applies damage
        var damageRequired = FixedPoint2.Zero;
        if (TryComp(target, out DamageableComponent? damageableComponent))
        {
            damageRequired = _destructibleSystem.DestroyedAt(target);
            damageRequired -= damageableComponent.TotalDamage;
            damageRequired = FixedPoint2.Max(damageRequired, FixedPoint2.Zero);
        }
        var deleted = Deleted(target);

        // Call base implementation to handle damage application and other effects
        var modifiedDamage = base.ProjectileCollide(projectile, target, collisionCoordinates, predicted);

        if (modifiedDamage == null)
        {
            component.ProjectileSpent = true;
            if (component.DeleteOnCollide && component.ProjectileSpent)
                QueueDel(uid);
            return null;
        }

        // Server-specific logic: penetration
        if (component.PenetrationThreshold != 0)
        {
            // If a damage type is required, stop the bullet if the hit entity doesn't have that type.
            if (component.PenetrationDamageTypeRequirement != null)
            {
                var stopPenetration = false;
                foreach (var requiredDamageType in component.PenetrationDamageTypeRequirement)
                {
                    if (!modifiedDamage.DamageDict.Keys.Contains(requiredDamageType))
                    {
                        stopPenetration = true;
                        break;
                    }
                }

                if (stopPenetration)
                    component.ProjectileSpent = true;
            }

            // If the object won't be destroyed, it "tanks" the penetration hit.
            if (modifiedDamage.GetTotal() < damageRequired)
            {
                component.ProjectileSpent = true;
            }

            if (!component.ProjectileSpent)
            {
                component.PenetrationAmount += damageRequired;
                // The projectile has dealt enough damage to be spent.
                if (component.PenetrationAmount >= component.PenetrationThreshold)
                {
                    component.ProjectileSpent = true;
                }
            }
        }
        else
        {
            component.ProjectileSpent = true;
        }

        return modifiedDamage;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ProjectileComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var projectileComp, out var physicsComp, out var xform))
        {
            if (projectileComp.ProjectileSpent || TerminatingOrDeleted(uid))
                continue;

            var currentVelocity = physicsComp.LinearVelocity;
            if (currentVelocity.Length() < MinRaycastVelocity)
                continue;

            var lastPosition = _transformSystem.GetWorldPosition(xform, GetEntityQuery<TransformComponent>());
            var rayDirection = currentVelocity.Normalized();
            // Ensure rayDistance is not zero to prevent issues with IntersectRay if frametime or velocity is zero.
            var rayDistance = currentVelocity.Length() * frameTime;
            if (rayDistance <= 0f)
                continue;

            if (!_fixQuery.TryComp(uid, out var fix) || !fix.Fixtures.TryGetValue(ProjectileFixture, out var projFix))
                continue;

            var collisionMask = projFix.CollisionMask;

            var hits = _physics.IntersectRay(xform.MapID,
                new CollisionRay(lastPosition, rayDirection, collisionMask),
                rayDistance,
                uid, // Entity to ignore (self)
                false) // IncludeNonHard = false
                .ToList();

            TryComp<ProjectileTargetWhitelistComponent>(uid, out var targetFilter); // HardLight

            // HardLight: walk hits nearest-first and route them through the same PreventCollide logic
            // as a normal physics collision. Stop at the first real hit; if a handler consumes the shell
            // (e.g. a shield intercept), stop there too instead of punching through to hits behind it.
            hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            foreach (var hit in hits)
            {
                var hitEnt = hit.HitEntity;

                if (projectileComp.IgnoreShooter && projectileComp.Shooter == hitEnt)
                    continue;

                if (targetFilter != null && !_whitelist.CheckBoth(hitEnt, targetFilter.Blacklist, targetFilter.Whitelist))
                    continue;

                if (RaycastHitPrevented(uid, physicsComp, projFix, hitEnt))
                {
                    // Collision prevented. If the shell was also consumed (shield intercept), stop;
                    // otherwise it genuinely passes through (own grid / EMP bypass) so keep scanning.
                    if (projectileComp.ProjectileSpent || TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid))
                        break;
                    continue;
                }

                // Real collision: snap to the hit point so the normal collision pipeline resolves it.
                var tpPos = lastPosition + rayDirection * hit.Distance;
                _transformSystem.SetWorldPosition(uid, tpPos);
                if (projectileComp.RaycastResetVelocity)
                    _physics.SetLinearVelocity(uid, rayDirection * MinRaycastVelocity * 0.99f);
                break;
            }
        }
    }

    /// <summary>
    /// HardLight: mirror the engine's PreventCollide handshake so the anti-tunnel raycast ignores
    /// entities the projectile would phase through (own grid, shields, shooter). Returns true to ignore.
    /// </summary>
    private bool RaycastHitPrevented(EntityUid uid, PhysicsComponent body, Fixture projFix, EntityUid hitEnt)
    {
        if (!_physQuery.TryComp(hitEnt, out var otherBody) || !_fixQuery.TryComp(hitEnt, out var otherFixtures))
            return true;

        Fixture? hitFix = null;
        foreach (var kv in otherFixtures.Fixtures)
        {
            if (kv.Value.Hard)
            {
                hitFix = kv.Value;
                break;
            }
        }

        if (hitFix == null)
            return true; // nothing hard to actually collide with

        var ourEv = new PreventCollideEvent(uid, hitEnt, body, otherBody, projFix, hitFix);
        RaiseLocalEvent(uid, ref ourEv);
        if (ourEv.Cancelled)
            return true;

        var otherEv = new PreventCollideEvent(hitEnt, uid, otherBody, body, hitFix, projFix);
        RaiseLocalEvent(hitEnt, ref otherEv);
        return otherEv.Cancelled;
    }
}
