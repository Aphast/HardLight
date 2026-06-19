using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Numerics;
using Content.Shared.Chat;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Humanoid;
using Content.Shared.Speech;
using Content.Shared.Sprite;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Chat.Systems;

// emotes using emote prototype
public partial class ChatSystem
{
    private FrozenDictionary<string, ImmutableList<EmotePrototype>> _wordEmoteDict = FrozenDictionary<string, ImmutableList<EmotePrototype>>.Empty; // DeltaV - Multiple emotes

    protected override void OnPrototypeReload(PrototypesReloadedEventArgs obj)
    {
        base.OnPrototypeReload(obj);
        if (obj.WasModified<EmotePrototype>())
            CacheEmotes();
    }

    private void CacheEmotes()
    {
        var dict = new Dictionary<string, ImmutableList<EmotePrototype>>(); // DeltaV - Multiple triggers for the same emote
        var emotes = _prototypeManager.EnumeratePrototypes<EmotePrototype>();
        foreach (var emote in emotes)
        {
            foreach (var word in emote.ChatTriggers)
            {
                var lowerWord = word.ToLower();
                if (dict.TryGetValue(lowerWord, out var value))
                {
                    // Begin DeltaV modification - Multiple emotes for the same words
                    dict[lowerWord] = value.Add(emote);

                    var errMsg = $"Duplicate of emote word {lowerWord}";
                    Log.Warning(errMsg);

                    continue;
                }

                dict.Add(lowerWord, ImmutableList.Create(emote)); // End DeltaV modification
            }
        }

        _wordEmoteDict = dict.ToFrozenDictionary();
    }

    /// <summary>
    ///     Makes selected entity to emote using <see cref="EmotePrototype"/> and sends message to chat.
    /// </summary>
    /// <param name="source">The entity that is speaking</param>
    /// <param name="emoteId">The id of emote prototype. Should has valid <see cref="EmotePrototype.ChatMessages"/></param>
    /// <param name="hideLog">Whether or not this message should appear in the adminlog window</param>
    /// <param name="range">Conceptual range of transmission, if it shows in the chat window, if it shows to far-away ghosts or ghosts at all...</param>
    /// <param name="nameOverride">The name to use for the speaking entity. Usually this should just be modified via <see cref="TransformSpeakerNameEvent"/>. If this is set, the event will not get raised.</param>
    /// <param name="forceEmote">Bypasses whitelist/blacklist/availibility checks for if the entity can use this emote</param>
    public void TryEmoteWithChat(
        EntityUid source,
        string emoteId,
        ChatTransmitRange range = ChatTransmitRange.Normal,
        bool hideLog = false,
        string? nameOverride = null,
        bool ignoreActionBlocker = false,
        bool forceEmote = false
        )
    {
        if (!_prototypeManager.TryIndex<EmotePrototype>(emoteId, out var proto))
            return;
        TryEmoteWithChat(source, proto, range, hideLog: hideLog, nameOverride, ignoreActionBlocker: ignoreActionBlocker, forceEmote: forceEmote);
    }

    /// <summary>
    ///     Makes selected entity to emote using <see cref="EmotePrototype"/> and sends message to chat.
    /// </summary>
    /// <param name="source">The entity that is speaking</param>
    /// <param name="emote">The emote prototype. Should has valid <see cref="EmotePrototype.ChatMessages"/></param>
    /// <param name="hideLog">Whether or not this message should appear in the adminlog window</param>
    /// <param name="hideChat">Whether or not this message should appear in the chat window</param>
    /// <param name="range">Conceptual range of transmission, if it shows in the chat window, if it shows to far-away ghosts or ghosts at all...</param>
    /// <param name="nameOverride">The name to use for the speaking entity. Usually this should just be modified via <see cref="TransformSpeakerNameEvent"/>. If this is set, the event will not get raised.</param>
    /// <param name="forceEmote">Bypasses whitelist/blacklist/availibility checks for if the entity can use this emote</param>
    public void TryEmoteWithChat(
        EntityUid source,
        EmotePrototype emote,
        ChatTransmitRange range = ChatTransmitRange.Normal,
        bool hideLog = false,
        string? nameOverride = null,
        bool ignoreActionBlocker = false,
        bool forceEmote = false
        )
    {
        if (!forceEmote && !AllowedToUseEmote(source, emote))
            return;

        // check if proto has valid message for chat
        if (emote.ChatMessages.Count != 0)
        {
            // not all emotes are loc'd, but for the ones that are we pass in entity
            var action = Loc.GetString(_random.Pick(emote.ChatMessages), ("entity", source));
            var language = _language.GetLanguage(source); // Starlight-edit: Languages
            SendEntityEmote(source, action, range, nameOverride, language, hideLog: hideLog, checkEmote: false, ignoreActionBlocker: ignoreActionBlocker); // Starlight-edit: Languages
        }

        // do the rest of emote event logic here
        TryEmoteWithoutChat(source, emote, ignoreActionBlocker);
    }

    /// <summary>
    ///     Makes selected entity to emote using <see cref="EmotePrototype"/> without sending any messages to chat.
    /// </summary>
    public void TryEmoteWithoutChat(EntityUid uid, string emoteId, bool ignoreActionBlocker = false)
    {
        if (!_prototypeManager.TryIndex<EmotePrototype>(emoteId, out var proto))
            return;

        TryEmoteWithoutChat(uid, proto, ignoreActionBlocker);
    }

    /// <summary>
    ///     Makes selected entity to emote using <see cref="EmotePrototype"/> without sending any messages to chat.
    /// </summary>
    public void TryEmoteWithoutChat(EntityUid uid, EmotePrototype proto, bool ignoreActionBlocker = false)
    {
        if (!_actionBlocker.CanEmote(uid) && !ignoreActionBlocker)
            return;

        InvokeEmoteEvent(uid, proto);
    }

    /// <summary>
    ///     Tries to find and play relevant emote sound in emote sounds collection.
    /// </summary>
    /// <returns>True if emote sound was played.</returns>
    public bool TryPlayEmoteSound(EntityUid uid, EmoteSoundsPrototype? proto, EmotePrototype emote)
    {
        return TryPlayEmoteSound(uid, proto, emote.ID);
    }

    /// <summary>
    ///     Tries to find and play relevant emote sound in emote sounds collection.
    /// </summary>
    /// <returns>True if emote sound was played.</returns>
    public bool TryPlayEmoteSound(EntityUid uid, EmoteSoundsPrototype? proto, string emoteId)
    {
        if (proto == null)
            return false;

        // try to get specific sound for this emote
        if (!proto.Sounds.TryGetValue(emoteId, out var sound))
        {
            // no specific sound - check fallback
            sound = proto.FallbackSound;
            if (sound == null)
                return false;
        }

        // if general params for all sounds set - use them
        var param = proto.GeneralParams ?? sound.Params;

        // Halve the random pitch spread so the size-based shift reads clearly against it.
        if (param.Variation is { } variation)
            param = param.WithVariation(variation * EmoteVariationMultiplier);

        // Shift pitch by size: bigger sounds deeper, smaller squeakier.
        param = param.WithPitchScale(param.Pitch * GetEmoteSizePitch(uid));

        _audio.PlayPvs(sound, uid, param);
        return true;
    }

    private const float EmoteVariationMultiplier = 0.5f; // halve every emote's random pitch spread
    // pitch = size^(-strength); big side steeper so giants boom harder than tinies squeak.
    private const float EmoteSizePitchStrengthSmall = 0.5f; // size<1: +6 st at the 0.5 floor
    private const float EmoteSizePitchStrengthBig = 1.0f;   // size>1: -12 st at the 2.0 ceiling
    // Clamp to the original Gaussian's note range: +/-1 octave = +/-12 semitones.
    private const float EmoteSizePitchMin = 0.5f;
    private const float EmoteSizePitchMax = 2.0f;

    /// <summary>
    ///     Returns a pitch multiplier for an entity's emote sounds based on its size.
    /// </summary>
    private float GetEmoteSizePitch(EntityUid uid)
    {
        var size = 1f;

        if (TryComp<AppearanceComponent>(uid, out var appearance))
        {
            // HumanoidVisuals.Scale carries both the editor height/width sliders and the
            // Big/Small/Tiny traits; ScaleVisuals.Scale covers borgs and admin-scaled mobs.
            if (_appearance.TryGetData<Vector2>(uid, HumanoidVisuals.Scale, out var humanoidScale, appearance))
                size = (humanoidScale.X + humanoidScale.Y) / 2f;
            else if (_appearance.TryGetData<Vector2>(uid, ScaleVisuals.Scale, out var spriteScale, appearance))
                size = (spriteScale.X + spriteScale.Y) / 2f;
        }
        else if (TryComp<HumanoidAppearanceComponent>(uid, out var humanoid))
        {
            // Fallback when default size left the appearance datum unset.
            size = (humanoid.Height + humanoid.Width) / 2f;
        }

        size = Math.Clamp(size, 0.5f, 2f);

        var strength = size >= 1f ? EmoteSizePitchStrengthBig : EmoteSizePitchStrengthSmall;
        return Math.Clamp(MathF.Pow(size, -strength), EmoteSizePitchMin, EmoteSizePitchMax);
    }
    /// <summary>
    /// Checks if a valid emote was typed, to play sounds and etc and invokes an event.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="textInput"></param>
    private bool TryEmoteChatInput(EntityUid uid, string textInput) // Frontier: void<bool
    {
        var actionTrimmedLower = TrimPunctuation(textInput.ToLower());
        if (!_wordEmoteDict.TryGetValue(actionTrimmedLower, out var emotes)) // DeltaV, renames to emotes
            return false; // Frontier: add false

        bool validEmote = false; // DeltaV - Multiple emotes for the same trigger
        foreach (var emote in emotes)
        {
            if (!AllowedToUseEmote(uid, emote))
                continue;

            InvokeEmoteEvent(uid, emote);
            validEmote = true; // DeltaV
            break; // Frontier: break on first emote (avoid playing multiple sounds at once)
        }

        return validEmote; // Frontier

        static string TrimPunctuation(string textInput)
        {
            var trimEnd = textInput.Length;
            while (trimEnd > 0 && char.IsPunctuation(textInput[trimEnd - 1]))
            {
                trimEnd--;
            }

            var trimStart = 0;
            while (trimStart < trimEnd && char.IsPunctuation(textInput[trimStart]))
            {
                trimStart++;
            }

            return textInput[trimStart..trimEnd];
        }
    }
    /// <summary>
    /// Checks if we can use this emote based on the emotes whitelist, blacklist, and availibility to the entity.
    /// </summary>
    /// <param name="source">The entity that is speaking</param>
    /// <param name="emote">The emote being used</param>
    /// <returns></returns>
    private bool AllowedToUseEmote(EntityUid source, EmotePrototype emote)
    {
        // If emote is in AllowedEmotes, it will bypass whitelist and blacklist
        if (TryComp<SpeechComponent>(source, out var speech) &&
            speech.AllowedEmotes.Contains(emote.ID))
        {
            return true;
        }

        // Check the whitelist and blacklist
        if (_whitelistSystem.IsWhitelistFail(emote.Whitelist, source) ||
            _whitelistSystem.IsBlacklistPass(emote.Blacklist, source))
        {
            return false;
        }

        // Check if the emote is available for all
        if (!emote.Available)
        {
            return false;
        }

        return true;
    }


    private void InvokeEmoteEvent(EntityUid uid, EmotePrototype proto)
    {
        var ev = new EmoteEvent(proto);
        RaiseLocalEvent(uid, ref ev);
    }
}

/// <summary>
///     Raised by chat system when entity made some emote.
///     Use it to play sound, change sprite or something else.
/// </summary>
[ByRefEvent]
public struct EmoteEvent
{
    public bool Handled;
    public readonly EmotePrototype Emote;

    public EmoteEvent(EmotePrototype emote)
    {
        Emote = emote;
        Handled = false;
    }
}
