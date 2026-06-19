using System.Linq;
using Content.Server._NF.Radio; // Frontier
using Content.Server.Administration.Logs;
using Content.Server.Chat.Systems;
using Content.Server.Power.Components;
using Content.Server.Radio.Components;
using Content.Server.Speech.Components;
using Content.Shared.Access.Components; // HardLight
using Content.Shared.Access.Systems; // HardLight
using Content.Shared._Mono.Company;
using Content.Shared.Chat;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Database;
using Content.Shared.Mobs.Components;
using Content.Shared.PDA; // HardLight
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Silicons.Borgs.Components; // HardLight
using Content.Shared.Silicons.StationAi; // HardLight
using Robust.Server.GameObjects; // Frontier
using Content.Shared.Speech;
using Content.Shared.Ghost; // Nuclear-14
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Replays;
using Robust.Shared.Utility;
// Starlight start
using Content.Shared._Starlight.Language;
using Content.Shared._Starlight.Language.Systems;
using Content.Server._Starlight.Language;
// Starlight end

namespace Content.Server.Radio.EntitySystems;

/// <summary>
///     This system handles intrinsic radios and the general process of converting radio messages into chat messages.
/// </summary>
public sealed class RadioSystem : EntitySystem
{
    [Dependency] private readonly INetManager _netMan = default!;
    [Dependency] private readonly IReplayRecordingManager _replay = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly AccessReaderSystem _accessReader = default!; // HardLight
    [Dependency] private readonly LanguageSystem _language = default!; // Starlight
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    // set used to prevent radio feedback loops.
    private readonly HashSet<string> _messages = new();

    private EntityQuery<TelecomExemptComponent> _exemptQuery;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<IntrinsicRadioReceiverComponent, RadioReceiveEvent>(OnIntrinsicReceive);
        SubscribeLocalEvent<IntrinsicRadioTransmitterComponent, EntitySpokeEvent>(OnIntrinsicSpeak);

        _exemptQuery = GetEntityQuery<TelecomExemptComponent>();
    }

    private bool TryGetRadioCompany(EntityUid entity, out string companyName)
    {
        companyName = string.Empty;
        var current = entity;

        while (current.IsValid())
        {
            if (TryComp(current, out CompanyComponent? company)
                && !string.IsNullOrWhiteSpace(company.CompanyName)
                && !string.Equals(company.CompanyName, "None", StringComparison.Ordinal))
            {
                companyName = company.CompanyName;
                return true;
            }

            var parent = _transform.GetParentUid(current);
            if (parent == current)
                break;

            current = parent;
        }

        return false;
    }

    private void OnIntrinsicSpeak(EntityUid uid, IntrinsicRadioTransmitterComponent component, EntitySpokeEvent args)
    {
        if (args.Channel != null && component.Channels.Contains(args.Channel.ID))
        {
            SendRadioMessage(uid, args.Message, args.Channel, uid, language: args.Language, originalMessage: args.OriginalMessage);
            args.Channel = null; // prevent duplicate messages from other listeners.
        }
    }

    /// <summary>
    /// Nuclear-14: Gets the message frequency, if there is no such frequency, returns the standard channel frequency.
    /// </summary>
    public int GetFrequency(EntityUid source, RadioChannelPrototype channel)
    {
        if (TryComp<RadioMicrophoneComponent>(source, out var radioMicrophone))
            return radioMicrophone.Frequency;

        return channel.Frequency;
    }

    private void OnIntrinsicReceive(EntityUid uid, IntrinsicRadioReceiverComponent component, ref RadioReceiveEvent args)
    {
        if (TryComp(uid, out ActorComponent? actor))
        {
            // Starlight start
            var listener = uid;
            var msg = args.OriginalChatMsg;

            if (!HasXenoglossy(listener, EntityManager) && !_language.CanUnderstand(listener, args.Language.ID))
                msg = args.LanguageObfuscatedChatMsg;
            else if (args.MessageSource != uid)
                args.Receivers.Add(uid);

            _netMan.ServerSendMessage(new MsgChatMessage { Message = msg }, actor.PlayerSession.Channel);
            // Starlight end
        }
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    public void SendRadioMessage(
        EntityUid messageSource,
        string message,
        ProtoId<RadioChannelPrototype> channel,
        EntityUid radioSource,
        int? frequency = null, // Frontier
        LanguagePrototype? language = null, // Starlight
        bool escapeMarkup = true,
        string? originalMessage = null)
    {
        SendRadioMessage(messageSource, message, _prototype.Index(channel), radioSource, frequency: frequency, language: language, escapeMarkup: escapeMarkup, originalMessage: originalMessage); // Frontier: Added frequency; // Starlight: Added language: language
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    /// <param name="messageSource">Entity that spoke the message</param>
    /// <param name="radioSource">Entity that picked up the message and will send it, e.g. headset</param>
    public void SendRadioMessage(
        EntityUid messageSource,
        string message,
        RadioChannelPrototype channel,
        EntityUid radioSource,
        int? frequency = null, // Nuclear-14
        LanguagePrototype? language = null, // Starlight
        bool escapeMarkup = true,
        string? originalMessage = null)
    {
        // Starlight start
        if (language == null)
            language = _language.GetLanguage(messageSource);

        if (!language.Speech.AllowRadio)
            return;
        // Starlight end

        // TODO if radios ever garble / modify messages, feedback-prevention needs to be handled better than this.
        if (!_messages.Add(message))
            return;

        var evt = new TransformSpeakerNameEvent(messageSource, MetaData(messageSource).EntityName);
        RaiseLocalEvent(messageSource, evt);

        // Frontier start: add name transform event
        var transformEv = new RadioTransformMessageEvent(channel, radioSource, evt.VoiceName, message, messageSource);
        RaiseLocalEvent(radioSource, ref transformEv);
        message = transformEv.Message;
        messageSource = transformEv.MessageSource;
        // Frontier end

        var name = transformEv.Name; // Frontier: evt.VoiceName<transformEv.Name
        name = FormattedMessage.EscapeText(name);

        SpeechVerbPrototype speech;
        if (evt.SpeechVerb != null && _prototype.TryIndex(evt.SpeechVerb, out var evntProto))
            speech = evntProto;
        else
            speech = _chat.GetSpeechVerb(messageSource, message);

        var content = escapeMarkup
            ? FormattedMessage.EscapeText(message)
            : message;

        // Frontier start: append frequency if the channel requests it
        string channelText;
        if (channel.ShowFrequency)
            channelText = $"\\[{channel.LocalizedName} ({frequency})\\]";
        else
            channelText = $"\\[{channel.LocalizedName}\\]";
        // Frontier end

        var originalContent = originalMessage == null // HardLight
            ? content
            : (escapeMarkup ? FormattedMessage.EscapeText(originalMessage) : originalMessage);
        // HardLight-edit start
        var selectedVerb = Loc.GetString(_random.Pick(speech.SpeechVerbStrings));
        var (defaultNameString, obfuscatedNameString) = GetRadioNameStrings(messageSource, name, language);
        // originalContent -> content, use the transformed content for the wrapped message so radio messages are proper
        var wrappedMessage = WrapRadioMessage(channel, content, language, false, channelText, speech, selectedVerb, defaultNameString, obfuscatedNameString);
        // HardLight-edit end

        // most radios are relayed to chat, so lets parse the chat message beforehand
        // HardLight-edit start
        var originalChat = new ChatMessage(
            ChatChannel.Radio,
            originalMessage ?? message,
            wrappedMessage,
            GetNetEntity(messageSource), // Goobstation - Chat Pings -- Added GetNetEntity(messageSource), to source
            null)
        {
            RadioChannelId = channel.ID
        };
        var obfuscated = _language.ObfuscateSpeech(content, language);
        var obfuscatedWrapped = WrapRadioMessage(channel, obfuscated, language, true, channelText, speech, selectedVerb, defaultNameString, obfuscatedNameString); // HardLight
        var obfuscatedChat = new ChatMessage(
            ChatChannel.Radio,
            obfuscated,
            obfuscatedWrapped,
            GetNetEntity(messageSource), // Goobstation - Chat Pings -- Added GetNetEntity(messageSource), to source
            null)
        {
            RadioChannelId = channel.ID
        };
        var ev = new RadioReceiveEvent(messageSource, channel, originalChat, obfuscatedChat, language, radioSource, []);
        // HardLight-edit end

        var sendAttemptEv = new RadioSendAttemptEvent(channel, radioSource);
        RaiseLocalEvent(ref sendAttemptEv);
        RaiseLocalEvent(radioSource, ref sendAttemptEv);
        var canSend = !sendAttemptEv.Cancelled;

        var sourceMapId = Transform(radioSource).MapID;
        var hasActiveServer = HasActiveServer(sourceMapId, channel.ID);
        var sourceServerExempt = _exemptQuery.HasComp(radioSource);

        var radioQuery = EntityQueryEnumerator<ActiveRadioComponent, TransformComponent>();

        if (frequency == null) // Nuclear-14
            frequency = GetFrequency(messageSource, channel); // Nuclear-14

        while (canSend && radioQuery.MoveNext(out var receiver, out var radio, out var transform))
        {
            if (!radio.ReceiveAllChannels)
            {
                if (!radio.Channels.Contains(channel.ID) || (TryComp<IntercomComponent>(receiver, out var intercom) &&
                                                             !intercom.SupportedChannels.Contains(channel.ID)))
                    continue;
            }

            if (!HasComp<GhostComponent>(receiver) && GetFrequency(receiver, channel) != frequency) // Nuclear-14
                continue; // Nuclear-14

            if (!channel.LongRange && transform.MapID != sourceMapId && !radio.GlobalReceive)
                continue;

            // don't need telecom server for long range channels or handheld radios and intercoms
            var needServer = !channel.LongRange && !sourceServerExempt;
            if (needServer && !hasActiveServer)
                continue;

            if (channel.RestrictToSharedFaction)
            {
                if (!TryGetRadioCompany(messageSource, out var sourceCompany)
                    || !TryGetRadioCompany(receiver, out var listenerCompany)
                    || !string.Equals(sourceCompany, listenerCompany, StringComparison.Ordinal))
                    continue;
            }

            // check if message can be sent to specific receiver
            var attemptEv = new RadioReceiveAttemptEvent(channel, radioSource, receiver);
            RaiseLocalEvent(ref attemptEv);
            RaiseLocalEvent(receiver, ref attemptEv);
            if (attemptEv.Cancelled)
                continue;

            // send the message
            RaiseLocalEvent(receiver, ref ev);
        }

        if (name != Name(messageSource))
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} as {name} on {channel.LocalizedName}: {message}");
        else
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} on {channel.LocalizedName}: {message}");

        _replay.RecordServerMessage(originalChat);
        _messages.Remove(message);
    }

    // Starlight start
    private (string, string) GetJobIcon(EntityUid messageSource)
    {
        var iconId = "JobIconNoId";
        var jobName = "";

        if (_accessReader.FindAccessItemsInventory(messageSource, out var items))
        {
            foreach (var item in items)
            {
                // ID Card
                if (TryComp<IdCardComponent>(item, out var id))
                {
                    iconId = id.JobIcon;
                    jobName = id.LocalizedJobTitle;
                    break;
                }

                // PDA
                if (TryComp<PdaComponent>(item, out var pda)
                    && pda.ContainedId != null
                    && TryComp(pda.ContainedId, out id))
                {
                    iconId = id.JobIcon;
                    jobName = id.LocalizedJobTitle;
                    break;
                }
            }
        }

        if (TryComp<BorgChassisComponent>(messageSource, out _) || HasComp<BorgBrainComponent>(messageSource)) // HardLight
        {
            iconId = "JobIconBorg";
            jobName = Loc.GetString("job-name-borg");
        }

        if (HasComp<StationAiHeldComponent>(messageSource))
        {
            iconId = "JobIconStationAi";
            jobName = Loc.GetString("job-name-station-ai");
        }

        jobName ??= "";

        return (iconId, jobName);
    }

    // HardLight start
    private (string DefaultNameString, string ObfuscatedNameString) GetRadioNameStrings(
        EntityUid source,
        string name,
        LanguagePrototype language)
    {
        var (iconId, jobName) = GetJobIcon(source);
        var defaultNameString = $"[icon src=\"{iconId}\" tooltip=\"{jobName}\"]{name}"; // HardLight: Removed spaces
        var obfuscatedNameString = _language.GetLanguageIcon(language, true)
            ? $"[icon src=\"{iconId}\" tooltip=\"{jobName}\"][icon src=\"{language.Icon}\" tooltip=\"{language.Name}\"]{name}" // HardLight: Removed spaces
            : defaultNameString;

        return (defaultNameString, obfuscatedNameString);
    }
    // HardLight end

    private string WrapRadioMessage(
        RadioChannelPrototype channel,
        string message,
        LanguagePrototype language, // Starlight
        bool obfuscated,
        string channelText,
        // HardLight start
        SpeechVerbPrototype speech,
        string verb,
        string defaultNameString,
        string obfuscatedNameString)
        // HardLight end
    {
        var languageColor = channel.Color;

        if (language.Speech.Color is { } colorOverride)
            languageColor = Color.InterpolateBetween(Color.White, colorOverride, colorOverride.A); // Changed first param to Color.White so it shows color correctly.

        var namestring = obfuscated ? obfuscatedNameString : defaultNameString; // HardLight

        var fonttype = language.Speech.FontId ?? speech.FontId;
        if ((language.Speech.ObfuscationFont ?? false) && !obfuscated)
            fonttype = speech.FontId;

        return Loc.GetString(speech.Bold ? "chat-radio-message-wrap-bold" : "chat-radio-message-wrap",
            ("color", channel.Color),
            ("languageColor", languageColor),
            ("fontType", fonttype),
            ("fontSize", language.Speech.FontSize ?? speech.FontSize),
            ("verb", verb), // HardLight
            ("channel", channelText),
            ("name", namestring),
            ("message", message));
    }
    // Starlight end

    public static bool HasXenoglossy(EntityUid uid, IEntityManager entManager)
    {
        return entManager.TryGetComponent<PsionicComponent>(uid, out var psionic)
                   && psionic.ActivePowers.Any(power => power.ID == "XenoglossyPower")
               || entManager.HasComponent<ReplacementAccentComponent>(uid)
                   && entManager.HasComponent<MobStateComponent>(uid);
    }

    /// <inheritdoc cref="TelecomServerComponent"/>
    private bool HasActiveServer(MapId mapId, string channelId)
    {
        var servers = EntityQuery<TelecomServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        foreach (var (_, keys, power, transform) in servers)
        {
            if (transform.MapID == mapId &&
                power.Powered &&
                keys.Channels.Contains(channelId))
            {
                return true;
            }
        }
        return false;
    }
}
