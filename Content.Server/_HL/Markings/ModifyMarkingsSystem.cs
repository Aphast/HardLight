using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared._HL.Markings;
using Content.Shared.Popups;
using Robust.Shared.Player;
using Robust.Shared.Audio.Systems;
using Content.Shared.IdentityManagement;

namespace Content.Server._HL.Markings;

public sealed class ModifyMarkingsSystem : SharedModifyMarkingsSystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly MarkingManager _markingManager = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly SharedHumanoidAppearanceSystem _humanoid = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ModifyMarkingsComponent, ModifyMarkingsDoAfterEvent>(ToggleUndies);
    }

    private void ToggleUndies(Entity<ModifyMarkingsComponent> ent, ref ModifyMarkingsDoAfterEvent args)
    {
        if (!_markingManager.TryGetMarking(args.Marking, out var mProt))
            return;

        if (!TryComp<HumanoidAppearanceComponent>(args.Target, out var humanoid))
            return;

        var isVisible = args.IsVisible;
        var marking = args.Marking;
        // Integrated from floofs's modifyundies into ModifyMarkings system.
        var localizedName = string.IsNullOrWhiteSpace(marking.CustomName)
            ? args.MarkingPrototypeName
            : marking.CustomName;

        var user = args.User;
        var target = args.Target.Value;
        var isMine = user == target;

        _humanoid.SetMarkingVisibility(target, humanoid, mProt.ID, !args.IsVisible);

        if (isMine)
        {
            var selfPopup = Loc.GetString(
                "marking-toggle-self",
                ("marking-name", localizedName),
                ("verb", isVisible ? marking.TakeOffVerb : marking.PutOnVerb));
            _popupSystem.PopupClient(selfPopup, target, target, PopupType.Medium);
        }
        else
        {
            // to the user
            var userPopup = Loc.GetString(
                "marking-toggle-other",
                ("marking-name", localizedName),
                ("verb", isVisible ? marking.TakeOffVerb : marking.PutOnVerb));
            _popupSystem.PopupClient(userPopup, user, user, PopupType.Medium);

            // to the target
            var targetPopup = Loc.GetString(
                "marking-toggle-by-other",
                ("marking-name", localizedName),
                ("verb", isVisible ? marking.TakeOffVerb2p : marking.PutOnVerb2p),
                ("other", Identity.Entity(user, _entMan)));
            _popupSystem.PopupClient(targetPopup, target, target, PopupType.MediumCaution);
        }

        // and then play a sound! If they aren't genitals.
        if (mProt.MarkingCategory != MarkingCategories.Genital)
            _audio.PlayEntity(ent.Comp.Sound, Filter.Entities(user, target), target, false);
    }

}
