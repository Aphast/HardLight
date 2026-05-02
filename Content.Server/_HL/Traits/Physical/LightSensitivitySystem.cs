using Content.Server._Starlight;
using Content.Server._Starlight.Shadekin;
using Content.Shared._HL.Traits.Physical;
using Content.Shared._Starlight;
using Content.Shared._Starlight.Shadekin;
using Content.Shared.Alert;
using Content.Shared.Damage;
using Content.Shared.Movement.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._HL.Traits.Physical;

/// <summary>
/// Handles light sensitivity burn damage and movement penalty for non-shadekin entities.
/// Shadekin entities are handled by ShadekinSystem instead.
/// </summary>
public sealed class LightSensitivitySystem : EntitySystem
{
    private static readonly ProtoId<AlertPrototype> LightExposureAlert = "Shadekin";

    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _speed = default!;
    [Dependency] private readonly ShadekinSystem _shadekin = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LightSensitivityComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeedModifiers);
    }

    private void OnRefreshSpeedModifiers(EntityUid uid, LightSensitivityComponent comp, RefreshMovementSpeedModifiersEvent args)
    {
        if (HasComp<ShadekinComponent>(uid))
            return; // ShadekinSystem handles shadekins

        if (comp.CurrentLightExposure < comp.SlowdownThreshold)
            return;

        args.ModifySpeed(comp.SpeedMultiplier, comp.SpeedMultiplier);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var curTime = _timing.CurTime;
        var query = EntityQueryEnumerator<LightSensitivityComponent, DamageableComponent>();

        while (query.MoveNext(out var uid, out var comp, out var _))
        {
            if (HasComp<ShadekinComponent>(uid))
                continue; // ShadekinSystem handles shadekins

            if (curTime < comp.NextUpdate)
                continue;

            comp.NextUpdate = curTime + comp.UpdateCooldown;

            // Discretize to 0-4 scale matching ShadekinSystem so thresholds (burnThreshold, slowdownThreshold)
            // behave identically for non-shadekins as they do for shadekins.
            var raw = _shadekin.GetLightExposure(uid);
            comp.CurrentLightExposure = DiscretizeExposure(raw);

            ApplyBurnDamage(uid, comp);
            _speed.RefreshMovementSpeedModifiers(uid);
            _alerts.ShowAlert(uid, LightExposureAlert, (short) comp.CurrentLightExposure);
        }
    }

    private static float DiscretizeExposure(float raw)
    {
        if (raw >= 15f) return 4f;
        if (raw >= 10f) return 3f;
        if (raw >= 5f) return 2f;
        if (raw >= 0.8f) return 1f;
        return 0f;
    }

    private void ApplyBurnDamage(EntityUid uid, LightSensitivityComponent comp)
    {
        if (comp.CurrentLightExposure < comp.BurnThreshold)
            return;

        var multiplier = (int) comp.CurrentLightExposure - comp.BurnThreshold + 1;
        var damage = new DamageSpecifier();
        damage.DamageDict.Add("Heat", multiplier);
        _damageable.TryChangeDamage(uid, damage, true, false);
    }
}
