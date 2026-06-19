using System.Linq;
using Content.Shared._Common.Consent;
using Content.Shared.DoAfter;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.IdentityManagement;
using Content.Shared.Inventory;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._HL.Markings;

/// <summary>
/// System that controls showing and hiding eligible humanoid markings through verbs.
/// </summary>
public abstract class SharedModifyMarkingsSystem : EntitySystem
{
    [Dependency] private readonly MarkingManager _markingManager = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfterSystem = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly INetManager _net = default!;

    public static readonly VerbCategory UndiesCat = new("verb-categories-undies", "/Textures/Interface/VerbIcons/undies.png");
    private static ProtoId<ConsentTogglePrototype> _genitalMarkingsConsent = "GenitalMarkings";

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ModifyMarkingsComponent, GetVerbsEvent<Verb>>(AddModifyMarkingsVerb);
    }

    private void AddModifyMarkingsVerb(Entity<ModifyMarkingsComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (args.Hands == null || !args.CanAccess || !args.CanInteract)
            return;

        if (!TryComp<HumanoidAppearanceComponent>(args.Target, out var humApp))
            return;

        if (args.User != args.Target && _inventory.TryGetSlotEntity(args.Target, "jumpsuit", out _))
            return; // mainly so people cant just spy on others markings *too* easily

        var user = args.User;
        var target = args.Target;
        var isMine = user == target;

        // okay go through their markings, and find all the undershirts and underwear markings
        // <marking_ID>, list:(localized name, bodypart enum, isvisible)
        foreach (var marking in humApp.MarkingSet.Markings.Values.SelectMany(markingLust => markingLust))
        {
            if (!_markingManager.TryGetMarking(marking, out var mProt))
                continue;

            // check if the Bodypart is in the component's BodyPartTargets
            if (!ent.Comp.BodyPartTargets.Contains(mProt.BodyPart))
                continue;

            // Integrated from floof's modifyundies into HardLight's ModifyMarkings
            if (isMine && !marking.CanToggleVisible)
                continue;

            if (!isMine && !marking.OtherCanToggleVisible)
                continue;

            var localizedName = string.IsNullOrWhiteSpace(marking.CustomName)
                ? Loc.GetString($"marking-{mProt.ID}")
                : marking.CustomName;
            var partSlot = mProt.BodyPart;
            var isVisible = !humApp.HiddenMarkings.Contains(mProt.ID);

            if (mProt.Sprites.Count < 1)
                continue; // no sprites means its not visible means its kinda already off and you cant put it on

            var underwearIcon = partSlot switch
            {
                HumanoidVisualLayers.UndergarmentTop => new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/bra.png")),
                HumanoidVisualLayers.UndergarmentBottom => new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/underpants.png")),
                HumanoidVisualLayers.Genital => new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/love.png")),
                HumanoidVisualLayers.Penis => new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/love.png")),
                HumanoidVisualLayers.Breasts => new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/love.png")),
                _ => new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/undies.png"))
            };

            var delay = mProt.MarkingCategory == MarkingCategories.Genital ? TimeSpan.Zero : TimeSpan.FromSeconds(2);

            // add the verb
            Verb verb = new()
            {
                Text = Loc.GetString(
                    "modify-undies-verb-text",
                    ("undies", localizedName),
                    ("isVisible", isVisible),
                    ("isMine", isMine),
                    ("target", Identity.Entity(target, _entMan)),
                    ("putOnVerb", isMine ? marking.PutOnVerb : marking.PutOnVerb2p),
                    ("takeOffVerb", isMine ? marking.TakeOffVerb : marking.TakeOffVerb2p)
                ),

                Icon = underwearIcon,
                Category = UndiesCat,
                Act = () =>
                {
                    var ev = new ModifyMarkingsDoAfterEvent(marking, localizedName, isVisible);

                    var doAfterArgs = new DoAfterArgs(
                        _entMan,
                        user,
                        delay,
                        ev,
                        target,
                        target,
                        used: user)
                    {
                        Hidden = false,
                        MovementThreshold = 0,
                        RequireCanInteract = true,
                        BlockDuplicate = true
                    };

                    if (isMine)
                    {
                        var selfPopup = Loc.GetString(
                            "marking-toggle-self-start",
                            ("marking-name", localizedName),
                            ("verb", isVisible ? marking.TakeOffVerb : marking.PutOnVerb));
                        _popupSystem.PopupClient(selfPopup, target, target, PopupType.Medium);
                    }
                    else
                    {
                        // to the user
                        var userPopup = Loc.GetString(
                            "marking-toggle-other-start",
                            ("marking-name", localizedName),
                            ("verb", isVisible ? marking.TakeOffVerb : marking.PutOnVerb));
                        _popupSystem.PopupClient(userPopup, user, user, PopupType.Medium);

                        // to the target
                        var targetPopup = Loc.GetString(
                            "marking-toggle-by-other-start",
                            ("marking-name", localizedName),
                            ("verb", isVisible ? marking.TakeOffVerb2p : marking.PutOnVerb2p),
                            ("other", Identity.Entity(user, _entMan)));
                        _popupSystem.PopupClient(targetPopup, target, target, PopupType.MediumCaution);
                    }

                    if (_net.IsServer && mProt.MarkingCategory != MarkingCategories.Genital)
                        _audio.PlayEntity(ent.Comp.Sound, Filter.Entities(user, target), target, false);

                    _doAfterSystem.TryStartDoAfter(doAfterArgs);
                },

                Disabled = false,
                Message = null
            };

            args.Verbs.Add(verb);
        }
    }}
