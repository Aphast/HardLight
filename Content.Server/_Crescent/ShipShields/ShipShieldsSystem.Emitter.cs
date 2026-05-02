using Content.Shared._Crescent.ShipShields;
using Content.Server.Power.Components;
using Content.Shared.Projectiles;
using Robust.Shared.Physics.Components;
using Content.Server.Emp;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Station.Systems;
using Robust.Shared.Audio.Systems;
using Content.Shared.Examine;
using Content.Server.Explosion.Components;
using Content.Shared.Explosion.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Maths;

namespace Content.Server._Crescent.ShipShields;

public partial class ShipShieldsSystem
{
    private const float MAX_EMP_DAMAGE = 10000f;
    [Dependency] private readonly TriggerSystem _trigger = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    public void InitializeEmitters()
    {
        SubscribeLocalEvent<ShipShieldEmitterComponent, ShieldDeflectedEvent>(OnShieldDeflected);
        SubscribeLocalEvent<ShipShieldEmitterComponent, ShieldHitscanDeflectedEvent>(OnShieldHitscanDeflected); // Mono - hitscan interception
        SubscribeLocalEvent<ShipShieldEmitterComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentRemove>(OnRemoved);
        SubscribeLocalEvent<ShipShieldEmitterComponent, MapInitEvent>(OnEmitterMapInit);
    }

    private void OnEmitterMapInit(EntityUid uid, ShipShieldEmitterComponent component, MapInitEvent args)
    {
        // Clean up any stale shield references and orphaned runtime shields from save/load.
        // This guarantees a fresh spawn on the next update tick even if old shield entities
        // were serialized in bad transform state (e.g. legacy world-origin placement).
        var parent = Transform(uid).GridUid;
        if (parent is null)
            RemoveEmitterShield(uid, component);
        else
            RemoveEmitterShield(uid, component, parent.Value);

        component.Shield = null;
        component.Shielded = null;
        component.Recharging = false;
    }


    private void OnRemoved(Entity<ShipShieldEmitterComponent> owner, ref ComponentRemove remove)
    {
        var parent = Transform(owner.Owner).GridUid;
        if (parent is null)
        {
            RemoveEmitterShield(owner.Owner, owner.Comp); // HardLight
            return;
        }

        RemoveEmitterShield(owner.Owner, owner.Comp, parent.Value); // HardLight
    }

    private void OnShieldDeflected(EntityUid uid, ShipShieldEmitterComponent component, ShieldDeflectedEvent args)
    {
        if (TryComp<EmpOnTriggerComponent>(args.Deflected, out var emp))
        {
            component.Damage += Math.Clamp(emp.EnergyConsumption, 0f, MAX_EMP_DAMAGE);
            _trigger.Trigger(args.Deflected);
        }

        if (TryComp<ExplosiveComponent>(args.Deflected, out var exp) && _prototypeManager.TryIndex(exp.ExplosionType, out var type))
        {
            component.Damage += exp.TotalIntensity * (float)type.DamagePerIntensity.GetTotal();
        }

        component.Damage += (float)args.Projectile.Damage.GetTotal();
        args.Projectile.ProjectileSpent = true;

        QueueDel(args.Deflected);
    }

    /// <summary>
    /// Handles shield emitter taking damage from an intercepted ship-weapon hitscan beam.
    /// </summary>
    private void OnShieldHitscanDeflected(EntityUid uid, ShipShieldEmitterComponent component, ref ShieldHitscanDeflectedEvent args)
    {
        component.Damage += args.Damage;
    }

    private void OnExamined(EntityUid uid, ShipShieldEmitterComponent component, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        args.PushMarkup(Loc.GetString("shield-emitter-examine", ("basedraw", component.BaseDraw), ("additional", CalculateLoadDamage(component))));
    }

    private static float CalculateLoadDamage(ShipShieldEmitterComponent emitter)
    {
        return (float)Math.Clamp(Math.Pow(emitter.Damage, emitter.DamageExp) * emitter.PowerModifier, 0f, emitter.MaxDraw);
    }

    private static float CalculateNormalLoad(ShipShieldEmitterComponent emitter)
    {
        return emitter.BaseDraw + CalculateLoadDamage(emitter);
    }

    private static float CalculateRechargeLoad(ShipShieldEmitterComponent emitter)
    {
        return emitter.BaseDraw + emitter.MaxDraw;
    }

    private static float CalculateRequestedLoad(ShipShieldEmitterComponent emitter)
    {
        return emitter.Recharging ? CalculateRechargeLoad(emitter) : CalculateNormalLoad(emitter);
    }

    private static float CalculateRechargeMultiplier(ShipShieldEmitterComponent emitter, ApcPowerReceiverComponent receiver)
    {
        if (!emitter.Recharging)
            return 1f;

        var rechargeLoad = CalculateRechargeLoad(emitter);
        if (rechargeLoad <= 0f)
            return emitter.UnpoweredBonus;

        var suppliedFraction = Math.Clamp(receiver.PowerReceived / rechargeLoad, 0f, 1f);
        return MathHelper.Lerp(1f, emitter.UnpoweredBonus, suppliedFraction);
    }

    private void AdjustEmitterLoad(EntityUid uid, ShipShieldEmitterComponent? emitter = null, ApcPowerReceiverComponent? receiver = null)
    {
        if (!Resolve(uid, ref emitter, ref receiver))
            return;

        receiver.Load = CalculateRequestedLoad(emitter);
    }
}
