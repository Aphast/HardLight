/* using Content.Shared.Access.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Events;
using Robust.Shared.Timing;
using Content.Shared.Atmos.Rotting;
using System.Linq;

namespace Content.Shared._DV.SmartFridge;

public sealed class SmartFridgeSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly AccessReaderSystem _accessReader = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SmartFridgeComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<SmartFridgeComponent, EntRemovedFromContainerMessage>(OnItemRemoved);
        SubscribeLocalEvent<SmartFridgeComponent, ComponentStartup>(OnComponentStartup);
        SubscribeLocalEvent<BeforeSerializationEvent>(OnBeforeSave);
        SubscribeLocalEvent<SmartFridgeComponent, MapInitEvent>(OnMapInit);

        Subs.BuiEvents<SmartFridgeComponent>(SmartFridgeUiKey.Key,
            sub =>
            {
                sub.Event<SmartFridgeDispenseItemMessage>(OnDispenseItem);
                sub.Event<SmartFridgeRemoveEntryMessage>(OnRemoveEntry);
            });
    }

    private void OnMapInit(Entity<SmartFridgeComponent> ent, ref MapInitEvent args)
    {
        // Ensure this fridge prevents rot for contained items.
        EnsureComp<AntiRottingContainerComponent>(ent.Owner);

        if (!_container.TryGetContainer(ent, ent.Comp.Container, out var container))
            return;

        // Rebuild entries from actual container contents to avoid stale/invalid NetEntity references
        ent.Comp.Entries.Clear();
        ent.Comp.ContainedEntries.Clear();

        foreach (var item in container.ContainedEntities.ToArray())
        {
            var name = Identity.Name(item, EntityManager);
            var entry = new SmartFridgeEntry(name);
            ent.Comp.Entries.Add(entry);

            if (!ent.Comp.ContainedEntries.TryGetValue(name, out var set))
            {
                set = new HashSet<NetEntity>();
                ent.Comp.ContainedEntries[name] = set;
            }

            set.Add(GetNetEntity(item));
        }

        Dirty(ent, ent.Comp);
    }

    private void OnComponentStartup(Entity<SmartFridgeComponent> ent, ref ComponentStartup args)
    {
        EnsureComp<AntiRottingContainerComponent>(ent.Owner);
    }

    private void OnBeforeSave(BeforeSerializationEvent ev)
    {
        foreach (var uid in ev.Entities)
        {
            if (!TryComp<SmartFridgeComponent>(uid, out var comp))
                continue;

            var removed = new List<SmartFridgeEntry>();

            foreach (var entry in comp.Entries)
            {
                if (!comp.ContainedEntries.TryGetValue(entry.Name, out var set) || set.Count == 0)
                {
                    removed.Add(entry);
                }
            }

            if (removed.Count == 0)
                continue;

            foreach (var entry in removed)
            {
                comp.Entries.Remove(entry);
                comp.ContainedEntries.Remove(entry.Name);
            }

            Dirty(uid, comp);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SmartFridgeComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!comp.Ejecting || _timing.CurTime <= comp.EjectEnd)
                continue;
            comp.EjectEnd = null;
            Dirty(uid, comp);
        }
    }

    /// <summary>
    /// Attempts to insert an item into a SmartFridge, checked against its item whitelist.
    /// Optionally checks user access, if a user is passed in, displaying an error in-game if they don't have access.
    /// </summary>
    /// <param name="ent">The SmartFridge being inserted into</param>
    /// <param name="item">The item being inserted</param>
    /// <param name="user">The user who should be access-checked</param>
    /// <param name="container">The SmartFridge's container if it's already known</param>
    /// <returns>Whether the insertion was successful</returns>
    public bool TryAddItem(Entity<SmartFridgeComponent?> ent,
        EntityUid item,
        EntityUid? user = null,
        BaseContainer? container = null)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return false;

        if (container == null && !_container.TryGetContainer(ent, ent.Comp.Container, out container))
            return false;

        if (!_whitelist.CheckBoth(item, ent.Comp.Blacklist, ent.Comp.Whitelist))
            return false;

        if (user != null && !Allowed((ent, ent.Comp), user.Value))
            return false;

        _container.Insert(item, container);
        var name = Identity.Name(item, EntityManager);
        var key = new SmartFridgeEntry(name);

        ent.Comp.Entries.Add(key);

        if (!ent.Comp.ContainedEntries.TryGetValue(name, out var set))
        {
            set = new HashSet<NetEntity>();
            ent.Comp.ContainedEntries[name] = set;
        }

        set.Add(GetNetEntity(item));

        Dirty(ent, ent.Comp);
        return true;
    }

    public void TryAddItem(Entity<SmartFridgeComponent?> ent,
        IEnumerable<EntityUid> items,
        EntityUid? user = null,
        BaseContainer? container = null)
    {
        if (!Resolve(ent, ref ent.Comp))
            return;

        if (container == null && !_container.TryGetContainer(ent, ent.Comp.Container, out container))
            return;

        if (user != null && !Allowed((ent, ent.Comp), user.Value))
            return;

        foreach (var item in items)
        {
            // Don't pass the user since we've already checked access
            TryAddItem(ent, item, null, container);
        }
    }

    private void OnInteractUsing(Entity<SmartFridgeComponent> ent, ref InteractUsingEvent args)
    {
        if (!_hands.CanDrop(args.User, args.Used))
            return;

        if (!TryAddItem(ent!, args.Used, args.User))
            return;

        _audio.PlayPredicted(ent.Comp.InsertSound, ent, args.User);
    }

    private void OnItemRemoved(Entity<SmartFridgeComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        var name = Identity.Name(args.Entity, EntityManager);

        if (ent.Comp.ContainedEntries.TryGetValue(name, out var contained))
        {
            contained.Remove(GetNetEntity(args.Entity));
        }

        Dirty(ent);
    }

    private bool Allowed(Entity<SmartFridgeComponent> machine, EntityUid user)
    {
        if (_accessReader.IsAllowed(user, machine))
            return true;

        _popup.PopupPredicted(Loc.GetString("smart-fridge-component-try-eject-access-denied"), machine, user);
        _audio.PlayPredicted(machine.Comp.SoundDeny, machine, user);
        return false;
    }

    private void OnDispenseItem(Entity<SmartFridgeComponent> ent, ref SmartFridgeDispenseItemMessage args)
    {
        if (!_timing.IsFirstTimePredicted || ent.Comp.Ejecting || !Allowed(ent, args.Actor))
            return;

        if (!ent.Comp.ContainedEntries.TryGetValue(args.Entry.Name, out var contained))
        {
            _audio.PlayPredicted(ent.Comp.SoundDeny, ent, args.Actor);
            _popup.PopupPredicted(Loc.GetString("smart-fridge-component-try-eject-unknown-entry"), ent, args.Actor);
            return;
        }

        foreach (var item in contained)
        {
            if (!_container.TryRemoveFromContainer(GetEntity(item)))
                continue;

            _audio.PlayPredicted(ent.Comp.SoundVend, ent, args.Actor);
            contained.Remove(item);
            ent.Comp.EjectEnd = _timing.CurTime + ent.Comp.EjectCooldown;
            Dirty(ent);
            return;
        }

        _audio.PlayPredicted(ent.Comp.SoundDeny, ent, args.Actor);
        _popup.PopupPredicted(Loc.GetString("smart-fridge-component-try-eject-out-of-stock"), ent, args.Actor);
    }

    private void OnRemoveEntry(Entity<SmartFridgeComponent> ent, ref SmartFridgeRemoveEntryMessage args)
    {
        if (!_timing.IsFirstTimePredicted || !Allowed(ent, args.Actor))
            return;

        if (ent.Comp.ContainedEntries.TryGetValue(args.Entry.Name, out var contained)
            && contained.Count > 0
            || !ent.Comp.Entries.Contains(args.Entry))
            return;

        ent.Comp.Entries.Remove(args.Entry);
        Dirty(ent);
    }
}
 */