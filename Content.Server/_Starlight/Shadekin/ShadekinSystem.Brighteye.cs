using Content.Shared._Starlight.Shadekin;
using Content.Shared.Humanoid;
using Content.Shared.Rejuvenate;
using Content.Shared.Popups;
using Content.Shared.Mobs;
using Content.Shared.Inventory;
using Content.Shared.Zombies;
using Content.Shared.Eye;

namespace Content.Server._Starlight.Shadekin;

public sealed partial class ShadekinSystem : EntitySystem
{
    public void InitializeBrighteye()
    {
        SubscribeLocalEvent<BrighteyeComponent, ComponentStartup>(OnInit);
        SubscribeLocalEvent<BrighteyeComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<BrighteyeComponent, RejuvenateEvent>(OnRejuvenate);
        SubscribeLocalEvent<BrighteyeComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<BrighteyeComponent, NullSpaceShuntEvent>(NullSpaceShunt);
        SubscribeLocalEvent<BrighteyeComponent, GetVisMaskEvent>(OnGetVisMask);
        SubscribeLocalEvent<BrighteyeComponent, EntityZombifiedEvent>((uid, _, _) => RemComp<BrighteyeComponent>(uid));
    }

    private void OnGetVisMask(Entity<BrighteyeComponent> uid, ref GetVisMaskEvent args) =>
        args.VisibilityMask |= (int)VisibilityFlags.NullSpace;

    private void OnInit(EntityUid uid, BrighteyeComponent component, ComponentStartup args)
    {
        if (!HasComp<ShadekinComponent>(uid))
        {
            RemComp<BrighteyeComponent>(uid);
            return;
        }

        _alerts.ShowAlert(uid, component.BrighteyeAlert);

        _actionsSystem.AddAction(uid, ref component.PortalAction, component.BrighteyePortalAction, uid);
        _actionsSystem.AddAction(uid, ref component.PhaseAction, component.BrighteyePhaseAction, uid);
        _actionsSystem.AddAction(uid, ref component.CreateShadeAction, component.BrighteyeCreateShadeAction, uid);
        _actionsSystem.AddAction(uid, ref component.DarkTrapAction, component.BrighteyeDarkTrapAction, uid);

        if (TryComp<HumanoidAppearanceComponent>(uid, out var humanoid))
            SetBrighteyes(uid, humanoid);

        _eye.RefreshVisibilityMask(uid);
    }

    private void OnShutdown(EntityUid uid, BrighteyeComponent component, ComponentShutdown args)
    {
        _alerts.ClearAlert(uid, component.BrighteyeAlert);

        _actionsSystem.RemoveAction(uid, component.PortalAction);
        _actionsSystem.RemoveAction(uid, component.PhaseAction);
        _actionsSystem.RemoveAction(uid, component.CreateShadeAction);
        _actionsSystem.RemoveAction(uid, component.DarkTrapAction);

        if (component.Portal is not null)
        {
            SpawnAtPosition(component.ShadekinShadow, Transform(component.Portal.Value).Coordinates);
            QueueDel(component.Portal.Value);
        }

        if (TryComp<HumanoidAppearanceComponent>(uid, out var humanoid))
            SetBlackeyes(uid, humanoid);

        _eye.RefreshVisibilityMask(uid);
    }

    private void OnRejuvenate(EntityUid uid, BrighteyeComponent component, RejuvenateEvent args)
    {
        component.Energy = component.MaxEnergy;
        Dirty(uid, component);
    }

    private void NullSpaceShunt(EntityUid uid, BrighteyeComponent component, NullSpaceShuntEvent args)
    {
        component.Energy = 0;
        Dirty(uid, component);
    }

    private void OnMobStateChanged(EntityUid uid, BrighteyeComponent component, MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        // Fenn request to just... POOOF!
        SpawnAtPosition(component.ShadekinShadow, Transform(uid).Coordinates);

        // First, Drop Everything we have.
        if (TryComp<InventoryComponent>(uid, out var inventoryComponent) && _inventorySystem.TryGetSlots(uid, out var slots))
            foreach (var slot in slots)
                _inventorySystem.TryUnequip(uid, slot.Name, true, true, false, inventoryComponent);

        // Vore
        _container.EmptyContainer(_container.GetContainer(uid, "stomach"));

        QueueDel(uid);
    }

    /// <summary>
    /// Change the humanoid eye to be bright and glow!
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="humanoid"></param>
    public void SetBrighteyes(EntityUid uid, HumanoidAppearanceComponent humanoid)
    {
        humanoid.EyeColor = EyeColor.MakeBrighteyeValid(humanoid.EyeColor);
        humanoid.EyeGlowing = true;
        Dirty(uid, humanoid);
    }

    /// <summary>
    /// Change the humanoid eye to be validated by HumanoidEyeColor.Shadekin (Blackeyes)
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="humanoid"></param>
    public void SetBlackeyes(EntityUid uid, HumanoidAppearanceComponent humanoid)
    {
        humanoid.EyeColor = EyeColor.MakeShadekinValid(humanoid.EyeColor);
        humanoid.EyeGlowing = false;

        Dirty(uid, humanoid);
    }

    /// <summary>
    /// When triggered, will check if we have enough energy and if yes drain the energy and return the value.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="component"></param>
    /// <param name="cost">cost of energy (if null then no cost needed)</param>
    /// <returns></returns>
    public bool OnAttemptEnergyUse(EntityUid uid, BrighteyeComponent component, int? cost = null)
    {
        var ev = new OnAttemptEnergyUseEvent(uid);
        RaiseLocalEvent(uid, ev);

        if (ev.Cancelled)
            return false;

        if (cost is null)
            return true;

        if (component.Energy >= cost)
        {
            component.Energy -= (int)cost;
            Dirty(uid, component);
        }
        else
        {
            _popup.PopupEntity(Loc.GetString("shadekin-noenergy"), uid, uid, PopupType.LargeCaution);
            return false;
        }

        return true;
    }

    private void UpdateEnergy(EntityUid uid, ShadekinComponent component, BrighteyeComponent brighteye)
    {
        if (component.CurrentState == ShadekinState.Low) // On Low State, we gain and lose nothing!
            return;

        int newenergy = 0;

        if (brighteye.Energy > 0 && component.CurrentState != ShadekinState.Dark) // First we will handle energy drain on light.
        {
            if (component.CurrentState == ShadekinState.Extreme)
                newenergy = -5;
            else if (component.CurrentState == ShadekinState.High)
                newenergy = -2;
            else if (component.CurrentState == ShadekinState.Annoying)
                newenergy = -1;
        }
        else if (brighteye.Energy < brighteye.MaxEnergy && component.CurrentState == ShadekinState.Dark) // We now handle energy gain.
        {
            // TODO: Add buffs here depanding on different situations?
            newenergy = 1;
        }

        brighteye.Energy = Math.Clamp(brighteye.Energy + newenergy, 0, brighteye.MaxEnergy);
        Dirty(uid, brighteye);
    }
}
