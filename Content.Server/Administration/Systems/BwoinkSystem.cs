using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.Afk;
using Content.Server.Database;
using Content.Server.Discord;
using Content.Server.GameTicking;
using Content.Server._NF.Bank;
using Content.Server.Spawners.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Players.RateLimiting;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Players.RateLimiting;
using Content.Shared.Roles;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Content.Server.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.Access.Components;
using Content.Shared._HL.Rescue.Rescue;
using Content.Server.Shuttles.Save;
using Content.Server.Access.Systems;
using JetBrains.Annotations;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Content.Shared.Maps;
using Robust.Shared.Network;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.ContentPack;
using Content.Shared.Physics;
using Content.Shared.Item;
using Content.Shared.Prototypes;

namespace Content.Server.Administration.Systems
{
    [UsedImplicitly]
    public sealed partial class BwoinkSystem : SharedBwoinkSystem
    {
        private const string RateLimitKey = "AdminHelp";
        private const int MinimumMessageLength = 2;

        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IAdminManager _adminManager = default!;
        [Dependency] private readonly IConfigurationManager _config = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IPlayerLocator _playerLocator = default!;
        [Dependency] private readonly GameTicker _gameTicker = default!;
        [Dependency] private readonly SharedMindSystem _minds = default!;
        [Dependency] private readonly IAfkManager _afkManager = default!;
        [Dependency] private readonly IServerDbManager _dbManager = default!;
        [Dependency] private readonly PlayerRateLimitManager _rateLimit = default!;
        [Dependency] private readonly StationSystem _station = default!;
        [Dependency] private readonly StationJobsSystem _stationJobs = default!;
        [Dependency] private readonly IPrototypeManager _prototype = default!;
        [Dependency] private readonly BankSystem _bank = default!;
        [Dependency] private readonly Content.Server.Shuttles.Systems.ShuttleSystem _shuttle = default!;
        [Dependency] private readonly Robust.Shared.Random.IRobustRandom _random = default!;
        [Dependency] private readonly SharedMapSystem _mapSystem = default!;
        [Dependency] private readonly TurfSystem _turf = default!;
        [Dependency] private readonly ShipSaveSystem _shipSave = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly IResourceManager _resources = default!;
        [Dependency] private readonly SharedIdCardSystem _idCard = default!;

        private static readonly ResPath SharedAdminMacrosPath = new("shared_admin_macros.txt");

        [GeneratedRegex(@"^https://(?:(?:canary|ptb)\.)?discord\.com/api/webhooks/(\d+)/((?!.*/).*)$")]
        private static partial Regex DiscordRegex();

        private string _webhookUrl = string.Empty;
        private WebhookData? _webhookData;

        private string _onCallUrl = string.Empty;
        private WebhookData? _onCallData;

        private ISawmill _sawmill = default!;
        private readonly HttpClient _httpClient = new();

        private string _footerIconUrl = string.Empty;
        private string _avatarUrl = string.Empty;
        private string _serverName = string.Empty;

        private readonly Dictionary<NetUserId, DiscordRelayInteraction> _relayMessages = new();

        private Dictionary<NetUserId, string> _oldMessageIds = new();
        private readonly Dictionary<NetUserId, Queue<DiscordRelayedData>> _messageQueues = new();
        private readonly HashSet<NetUserId> _processingChannels = new();
        private readonly Dictionary<NetUserId, (TimeSpan Timestamp, bool Typing)> _typingUpdateTimestamps = new();
        private TimeSpan _nextTypingCachePrune;
        private static readonly TimeSpan TypingCachePruneInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan TypingCacheTtl = TimeSpan.FromMinutes(20);
        private string _overrideClientName = string.Empty;

        // Max embed description length is 4096, according to https://discord.com/developers/docs/resources/channel#embed-object-embed-limits
        // Keep small margin, just to be safe
        private const ushort DescriptionMax = 4000;

        // Maximum length a message can be before it is cut off
        // Should be shorter than DescriptionMax
        private const ushort MessageLengthCap = 3000;

        // Text to be used to cut off messages that are too long. Should be shorter than MessageLengthCap
        private const string TooLongText = "... **(too long)**";

        private static readonly Dictionary<string, string[]> DefaultAhelpTriageRules = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ship Saving / Loading"] = new[] { "failed to save", "save bricked", "cant load", "can't load", "backup", "duplicate ship", "ship save", "ship load" },
            ["Ship Knotting / Collision / Spawn"] = new[] { "knotted", "collision", "inside mine", "inside another ship", "spawned inside", "ship overlap", "stuck in another ship" },
            ["Lost Items / Gear"] = new[] { "lost", "deleted", "missing", "my bag", "hardsuit", "gear", "item vanished", "item disappeared" },
            ["Lag / Server Performance"] = new[] { "lag", "rubberband", "rubber band", "server dying", "massive lag spike", "desync", "high ping" },
            ["Flatpack / Unpacking"] = new[] { "flatpack", "flat pack", "unpacking", "unpack", "no space", "broken flatpack" },
            ["Ship FTL / Movement"] = new[] { "currently in ftl", "cannot ftl", "can't ftl", "thruster", "ftl", "ship wont move", "ship won't move" },
            ["Admin Spawn / Custom Request"] = new[] { "spawn me", "give me", "custom item", "custom suit", "change my blood", "admin spawn" },
            ["Chemistry / Reagents"] = new[] { "nank temperature", "chem", "reagent", "beaker", "chem master", "not filling" },
            ["Expedition"] = new[] { "exped", "expedition", "expd", "stuck on exped", "exped wont end", "exped won't end" },
            ["Spawn Location"] = new[] { "spawned in space", "trapped in engineering", "spawned in", "wrong spawn", "bad spawn" },
            ["Ship Deed / ID"] = new[] { "ship deed", "deed", "lost my id", "not attached to my id", "ship id" },
            ["Trait / Loadout"] = new[] { "trait", "loadout", "spawned without", "trait not working", "synthetic trait" },
            ["Protogen / Borg Surgery"] = new[] { "protogen", "borg", "surgery", "limb", "armour", "airloss", "stuck in a chassis" },
            ["UI / Hotkey / Action Bar"] = new[] { "toggle helmet", "hotkey", "action bar", "ui", "button not working", "keybind" },
            ["NPC / Vent Critter / Monster"] = new[] { "npc", "vent critter", "monster", "dreadtalon", "ai behavior", "ai behaviour" },
            ["Self-Antag / Rule"] = new[] { "self antag", "self-antag", "rule", "no rp", "cryo cycling", "arrest" },
            ["Antag Objective"] = new[] { "objective", "kill target cryo", "objective doesnt spawn", "objective doesn't spawn", "new objective" },
            ["Time Transfer"] = new[] { "transfer my playtime", "time transfer", "playtime transfer" },
        };

        // Auto-reply templates are admin-defined. The dictionary stays so legacy
        // helpers compile; admins populate _customAutoReplyTemplates via the AHelp
        // editor (Add Category / Set Template).
        private static readonly Dictionary<string, string> AhelpAutoReplyLocKeysByCategory = new(StringComparer.OrdinalIgnoreCase);

        private int _maxAdditionalChars;
        private readonly Dictionary<NetUserId, DateTime> _activeConversations = new();
        private readonly HashSet<NetUserId> _automatedAhelpHistory = new();
        private readonly Dictionary<string, string> _autoReplyOverrides = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _triageRuleOverrides = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _customAutoReplyTemplates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _customTriageRules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _removedAutoReplyCategories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _removedTriageCategories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _enabledAutoReplyCategories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _enabledTriageCategories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SharedAdminMacroRecord> _sharedAdminMacros = new(StringComparer.OrdinalIgnoreCase);
        private bool _autoReplyEnabled = false;
        private bool _panicAutoReplyEnabled = false;
        private bool _triageEnabled = true;
        private string _autoReplyBotName = "Auto-Reply";
        private string _panicAutoReplyTemplate = string.Empty;

        public bool AutoReplyEnabled => _autoReplyEnabled;
        public bool PanicAutoReplyEnabled => _panicAutoReplyEnabled;
        public bool TriageEnabled => _triageEnabled;

        public override void Initialize()
        {
            base.Initialize();

            Subs.CVar(_config, CCVars.DiscordOnCallWebhook, OnCallChanged, true);

            Subs.CVar(_config, CCVars.DiscordAHelpWebhook, OnWebhookChanged, true);
            Subs.CVar(_config, CCVars.DiscordAHelpFooterIcon, OnFooterIconChanged, true);
            Subs.CVar(_config, CCVars.DiscordAHelpAvatar, OnAvatarChanged, true);
            Subs.CVar(_config, CVars.GameHostName, OnServerNameChanged, true);
            Subs.CVar(_config, CCVars.AdminAhelpOverrideClientName, OnOverrideChanged, true);
            _sawmill = IoCManager.Resolve<ILogManager>().GetSawmill("AHELP");

            // Default behavior: triage rules start enabled; auto-replies remain disabled
            // until explicitly enabled by admins.
            _enabledTriageCategories.Clear();
            foreach (var category in DefaultAhelpTriageRules.Keys)
            {
                _enabledTriageCategories.Add(category);
            }

            var defaultParams = new AHelpMessageParams(
                string.Empty,
                string.Empty,
                true,
                _gameTicker.RoundDuration().ToString("hh\\:mm\\:ss"),
                _gameTicker.RunLevel,
                playedSound: false
            );
            _maxAdditionalChars = GenerateAHelpMessage(defaultParams).Message.Length;
            _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;

            SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameRunLevelChanged);
            SubscribeNetworkEvent<BwoinkClientTypingUpdated>(OnClientTypingUpdated);
            SubscribeNetworkEvent<RequestAhelpAdminStateMessage>(OnAhelpAdminStateRequested);
            SubscribeNetworkEvent<SetAhelpAutoReplyEnabledMessage>(OnSetAhelpAutoReplyEnabled);
            SubscribeNetworkEvent<SetAhelpPanicAutoReplyEnabledMessage>(OnSetAhelpPanicAutoReplyEnabled);
            SubscribeNetworkEvent<SetAhelpPanicAutoReplyTemplateMessage>(OnSetAhelpPanicAutoReplyTemplate);
            SubscribeNetworkEvent<SetAhelpTriageEnabledMessage>(OnSetAhelpTriageEnabled);
            SubscribeNetworkEvent<AddOrRestoreAhelpCategoryMessage>(OnAddOrRestoreAhelpCategory);
            SubscribeNetworkEvent<RemoveAhelpCategoryMessage>(OnRemoveAhelpCategory);
            SubscribeNetworkEvent<SetAhelpAutoReplyTemplateMessage>(OnSetAhelpAutoReplyTemplate);
            SubscribeNetworkEvent<ResetAhelpAutoReplyTemplateMessage>(OnResetAhelpAutoReplyTemplate);
            SubscribeNetworkEvent<SetAhelpTriageKeywordsMessage>(OnSetAhelpTriageKeywords);
            SubscribeNetworkEvent<ResetAhelpTriageKeywordsMessage>(OnResetAhelpTriageKeywords);
            SubscribeNetworkEvent<SetAhelpAutoReplyBotNameMessage>(OnSetAhelpAutoReplyBotName);
            SubscribeNetworkEvent<SetAhelpCategoryAutoReplyEnabledMessage>(OnSetAhelpCategoryAutoReplyEnabled);
            SubscribeNetworkEvent<SetAhelpCategoryTriageEnabledMessage>(OnSetAhelpCategoryTriageEnabled);
            SubscribeNetworkEvent<RequestSharedAdminMacrosMessage>(OnRequestSharedAdminMacros);
            SubscribeNetworkEvent<UpsertSharedAdminMacroMessage>(OnUpsertSharedAdminMacro);
            SubscribeNetworkEvent<DeleteSharedAdminMacroMessage>(OnDeleteSharedAdminMacro);
            SubscribeNetworkEvent<RequestPlayerShipInspectionMessage>(OnRequestPlayerShipInspection);
            SubscribeNetworkEvent<RequestPlayerSnapshotMessage>(OnRequestPlayerSnapshot);
            SubscribeNetworkEvent<RequestAdminStatisticsMessage>(OnRequestAdminStatistics);
            SubscribeNetworkEvent<RequestPlayerBankInfoMessage>(OnRequestPlayerBankInfo);
            SubscribeNetworkEvent<RequestModifyPlayerBankMessage>(OnRequestModifyPlayerBank);
            SubscribeNetworkEvent<RequestTeleportPlayerToStationMessage>(OnRequestTeleportPlayerToStation);
            SubscribeNetworkEvent<RequestTeleportPlayerToShipMessage>(OnRequestTeleportPlayerToShip);
            SubscribeNetworkEvent<RequestShipDeedListMessage>(OnRequestShipDeedList);
            SubscribeNetworkEvent<RequestAssignShipDeedMessage>(OnRequestAssignShipDeed);
            SubscribeNetworkEvent<RequestUnstickPlayerShipPreviewMessage>(OnRequestUnstickPlayerShipPreview);
            SubscribeNetworkEvent<RequestUnstickPlayerShipMessage>(OnRequestUnstickPlayerShip);
            SubscribeNetworkEvent<RequestSaveShipPreviewMessage>(OnRequestSaveShipPreview);
            SubscribeNetworkEvent<RequestSaveShipMessage>(OnRequestSaveShip);
            SubscribeNetworkEvent<RequestSpawnAhelpItemMessage>(OnRequestSpawnAhelpItem);
            SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
            {
                _activeConversations.Clear();
                _automatedAhelpHistory.Clear();
            });

            LoadSharedAdminMacros();

        	_rateLimit.Register(
                RateLimitKey,
                new RateLimitRegistration(CCVars.AhelpRateLimitPeriod,
                    CCVars.AhelpRateLimitCount,
                    PlayerRateLimitedAction)
                );
        }

        private async void OnCallChanged(string url)
        {
            _onCallUrl = url;

            if (url == string.Empty)
                return;

            var match = DiscordRegex().Match(url);

            if (!match.Success)
            {
                Log.Error("On call URL does not appear to be valid.");
                return;
            }

            if (match.Groups.Count <= 2)
            {
                Log.Error("Could not get webhook ID or token for on call URL.");
                return;
            }

            var webhookId = match.Groups[1].Value;
            var webhookToken = match.Groups[2].Value;

            _onCallData = await GetWebhookData(url);
        }

        private void PlayerRateLimitedAction(ICommonSession obj)
        {
            RaiseNetworkEvent(
                new BwoinkTextMessage(obj.UserId, default, Loc.GetString("bwoink-system-rate-limited"), playSound: false),
                obj.Channel);
        }

        private void OnOverrideChanged(string obj)
        {
            _overrideClientName = obj;
        }

        private async void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
        {
            if (e.NewStatus == SessionStatus.Disconnected)
            {
                _typingUpdateTimestamps.Remove(e.Session.UserId);

                if (_activeConversations.TryGetValue(e.Session.UserId, out var lastMessageTime))
                {
                    var timeSinceLastMessage = DateTime.Now - lastMessageTime;
                    if (timeSinceLastMessage > TimeSpan.FromMinutes(5))
                    {
                        _activeConversations.Remove(e.Session.UserId);
                        return; // Do not send disconnect message if timeout exceeded
                    }
                }

                // Check if the user has been banned
                var ban = await _dbManager.GetBanAsync(null, e.Session.UserId, null, null);
                if (ban != null)
                {
                    var banMessage = Loc.GetString("bwoink-system-player-banned", ("banReason", ban.Reason));
                    NotifyAdmins(e.Session, banMessage, PlayerStatusType.Banned);
                    _activeConversations.Remove(e.Session.UserId);
                    return;
                }
            }

            // Notify all admins if a player disconnects or reconnects
            var message = e.NewStatus switch
            {
                SessionStatus.Connected => Loc.GetString("bwoink-system-player-reconnecting"),
                SessionStatus.Disconnected => Loc.GetString("bwoink-system-player-disconnecting"),
                _ => null
            };

            if (message != null)
            {
                var statusType = e.NewStatus == SessionStatus.Connected
                    ? PlayerStatusType.Connected
                    : PlayerStatusType.Disconnected;
                NotifyAdmins(e.Session, message, statusType);
            }

            if (e.NewStatus != SessionStatus.InGame)
                return;

            RaiseNetworkEvent(new BwoinkDiscordRelayUpdated(!string.IsNullOrWhiteSpace(_webhookUrl)), e.Session);
        }

        public override void Shutdown()
        {
            base.Shutdown();
            _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
        }

        private void NotifyAdmins(ICommonSession session, string message, PlayerStatusType statusType)
        {
            if (!_activeConversations.ContainsKey(session.UserId))
            {
                // If the user is not part of an active conversation, do not notify admins.
                return;
            }

            // Get the current timestamp
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var roundTime = _gameTicker.RoundDuration().ToString("hh\\:mm\\:ss");

            // Determine the icon based on the status type
            string icon = statusType switch
            {
                PlayerStatusType.Connected => ":green_circle:",
                PlayerStatusType.Disconnected => ":red_circle:",
                PlayerStatusType.Banned => ":no_entry:",
                _ => ":question:"
            };

            // Create the message parameters for Discord
            var messageParams = new AHelpMessageParams(
                session.Name,
                message,
                true,
                roundTime,
                _gameTicker.RunLevel,
                playedSound: true,
                icon: icon
            );

            // Create the message for in-game with username
            var color = statusType switch
            {
                PlayerStatusType.Connected => Color.Green.ToHex(),
                PlayerStatusType.Disconnected => Color.Yellow.ToHex(),
                PlayerStatusType.Banned => Color.Orange.ToHex(),
                _ => Color.Gray.ToHex(),
            };
            var inGameMessage = $"[color={color}]{session.Name} {message}[/color]";

            var bwoinkMessage = new BwoinkTextMessage(
                userId: session.UserId,
                trueSender: SystemUserId,
                text: inGameMessage,
                sentAt: DateTime.Now,
                playSound: false
            );

            var admins = GetTargetAdmins();
            foreach (var admin in admins)
            {
                RaiseNetworkEvent(bwoinkMessage, admin);
            }

            // Enqueue the message for Discord relay
            if (_webhookUrl != string.Empty)
            {
                // if (!_messageQueues.ContainsKey(session.UserId))
                //     _messageQueues[session.UserId] = new Queue<string>();
                //
                // var escapedText = FormattedMessage.EscapeText(message);
                // messageParams.Message = escapedText;
                //
                // var discordMessage = GenerateAHelpMessage(messageParams);
                // _messageQueues[session.UserId].Enqueue(discordMessage);

                var queue = _messageQueues.GetOrNew(session.UserId);
                var escapedText = FormattedMessage.EscapeText(message);
                messageParams.Message = escapedText;
                var discordMessage = GenerateAHelpMessage(messageParams);
                queue.Enqueue(discordMessage);
            }
        }

        private void OnGameRunLevelChanged(GameRunLevelChangedEvent args)
        {
            // Don't make a new embed if we
            // 1. were in the lobby just now, and
            // 2. are not entering the lobby or directly into a new round.
            if (args.Old is GameRunLevel.PreRoundLobby ||
                args.New is not (GameRunLevel.PreRoundLobby or GameRunLevel.InRound))
            {
                return;
            }

            // Store the Discord message IDs of the previous round
            _oldMessageIds = new Dictionary<NetUserId, string>();
            foreach (var (user, interaction) in _relayMessages)
            {
                var id = interaction.Id;
                if (id == null)
                    return;

                _oldMessageIds[user] = id;
            }

            _relayMessages.Clear();
        }

        private void OnClientTypingUpdated(BwoinkClientTypingUpdated msg, EntitySessionEventArgs args)
        {
            if (_typingUpdateTimestamps.TryGetValue(args.SenderSession.UserId, out var tuple) &&
                tuple.Typing == msg.Typing &&
                tuple.Timestamp + TimeSpan.FromSeconds(1) > _timing.RealTime)
            {
                return;
            }

            _typingUpdateTimestamps[args.SenderSession.UserId] = (_timing.RealTime, msg.Typing);

            // Non-admins can only ever type on their own ahelp, guard against fake messages
            var isAdmin = _adminManager.GetAdminData(args.SenderSession)?.HasFlag(AdminFlags.Adminhelp) ?? false;
            var channel = isAdmin ? msg.Channel : args.SenderSession.UserId;
            var update = new BwoinkPlayerTypingUpdated(channel, args.SenderSession.Name, msg.Typing);

            foreach (var admin in GetTargetAdmins())
            {
                if (admin.UserId == args.SenderSession.UserId)
                    continue;

                RaiseNetworkEvent(update, admin);
            }
        }

        private void OnServerNameChanged(string obj)
        {
            _serverName = obj;
        }

        private async void OnWebhookChanged(string url)
        {
            _webhookUrl = url;

            RaiseNetworkEvent(new BwoinkDiscordRelayUpdated(!string.IsNullOrWhiteSpace(url)));

            if (url == string.Empty)
                return;

            // Basic sanity check and capturing webhook ID and token
            var match = DiscordRegex().Match(url);

            if (!match.Success)
            {
                // TODO: Ideally, CVar validation during setting should be better integrated
                Log.Warning("Webhook URL does not appear to be valid. Using anyways...");
                _webhookData = await GetWebhookData(url); // Frontier - Support for Custom URLS, we still want to see if theres Webhook data available
                return;
            }

            if (match.Groups.Count <= 2)
            {
                Log.Error("Could not get webhook ID or token.");
                return;
            }

            // Fire and forget
            _webhookData = await GetWebhookData(url); // Frontier - Support for Custom URLS
        }

        private async Task<WebhookData?> GetWebhookData(string url)
        {
            var response = await _httpClient.GetAsync(url);

            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _sawmill.Log(LogLevel.Error,
                    $"Webhook returned bad status code when trying to get webhook data (perhaps the webhook URL is invalid?): {response.StatusCode}\nResponse: {content}");
                return null;
            }

            return JsonSerializer.Deserialize<WebhookData>(content);
        }

        private void OnFooterIconChanged(string url)
        {
            _footerIconUrl = url;
        }

        private void OnAvatarChanged(string url)
        {
            _avatarUrl = url;
        }

        private async void ProcessQueue(NetUserId userId, Queue<DiscordRelayedData> messages)
        {
            // Whether an embed already exists for this player
            var exists = _relayMessages.TryGetValue(userId, out var existingEmbed);

            // Whether the message will become too long after adding these new messages
            var tooLong = exists && messages.Sum(msg => Math.Min(msg.Message.Length, MessageLengthCap) + "\n".Length)
                    + existingEmbed?.Description.Length > DescriptionMax;

            // If there is no existing embed, or it is getting too long, we create a new embed
            if (!exists || tooLong)
            {
                var lookup = await _playerLocator.LookupIdAsync(userId);

                if (lookup == null)
                {
                    _sawmill.Log(LogLevel.Error,
                        $"Unable to find player for NetUserId {userId} when sending webhook."); // Frontier: remove "discord"
                    _relayMessages.Remove(userId);
                    return;
                }

                var linkToPrevious = string.Empty;

                // If we have all the data required, we can link to the embed of the previous round or embed that was too long
                if (_webhookData is { GuildId: { } guildId, ChannelId: { } channelId })
                {
                    if (tooLong && existingEmbed?.Id != null)
                    {
                        linkToPrevious =
                            $"**[Go to previous embed of this round](https://discord.com/channels/{guildId}/{channelId}/{existingEmbed.Id})**\n";
                    }
                    else if (_oldMessageIds.TryGetValue(userId, out var id) && !string.IsNullOrEmpty(id))
                    {
                        linkToPrevious =
                            $"**[Go to last round's conversation with this player](https://discord.com/channels/{guildId}/{channelId}/{id})**\n";
                    }
                }

                var characterName = _minds.GetCharacterName(userId);
                existingEmbed = new DiscordRelayInteraction()
                {
                    Id = null,
                    CharacterName = characterName,
                    Description = linkToPrevious,
                    Username = lookup.Username,
                    LastRunLevel = _gameTicker.RunLevel,
                };

                _relayMessages[userId] = existingEmbed;
            }

            // Previous message was in another RunLevel, so show that in the embed
            if (existingEmbed!.LastRunLevel != _gameTicker.RunLevel)
            {
                existingEmbed.Description += _gameTicker.RunLevel switch
                {
                    GameRunLevel.PreRoundLobby => "\n\n:arrow_forward: _**Pre-round lobby started**_\n",
                    GameRunLevel.InRound => "\n\n:arrow_forward: _**Round started**_\n",
                    GameRunLevel.PostRound => "\n\n:stop_button: _**Post-round started**_\n",
                    _ => throw new ArgumentOutOfRangeException(nameof(_gameTicker.RunLevel),
                        $"{_gameTicker.RunLevel} was not matched."),
                };

                existingEmbed.LastRunLevel = _gameTicker.RunLevel;
            }

            // If last message of the new batch is SOS then relay it to on-call.
            // ... as long as it hasn't been relayed already.
            var discordMention = messages.Last();
            var onCallRelay = !discordMention.Receivers && !existingEmbed.OnCall;

            // Add available messages to the embed description
            while (messages.TryDequeue(out var message))
            {
                string text;

                // In case someone thinks they're funny
                if (message.Message.Length > MessageLengthCap)
                    text = message.Message[..(MessageLengthCap - TooLongText.Length)] + TooLongText;
                else
                    text = message.Message;

                existingEmbed.Description += $"\n{text}";
            }

            var payload = GeneratePayload(existingEmbed.Description,
                existingEmbed.Username,
                userId.UserId, // Frontier, this is used to identify the players in the webhook
                existingEmbed.CharacterName);

            // If there is no existing embed, create a new one
            // Otherwise patch (edit) it
            if (existingEmbed.Id == null)
            {
                var request = await _httpClient.PostAsync($"{_webhookUrl}?wait=true",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                var content = await request.Content.ReadAsStringAsync();
                if (!request.IsSuccessStatusCode)
                {
                    _sawmill.Log(LogLevel.Error,
                        $"Webhook returned bad status code when posting message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}"); // Frontier: "Discord"<"Webhook"
                    _relayMessages.Remove(userId);
                    return;
                }

                var id = JsonNode.Parse(content)?["id"];
                if (id == null)
                {
                    _sawmill.Log(LogLevel.Error,
                        $"Could not find id in json-content returned from webhook: {content}"); // Frontier: remove "discord"
                    _relayMessages.Remove(userId);
                    return;
                }

                existingEmbed.Id = id.ToString();
            }
            else
            {
                var request = await _httpClient.PatchAsync($"{_webhookUrl}/messages/{existingEmbed.Id}",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                if (!request.IsSuccessStatusCode)
                {
                    var content = await request.Content.ReadAsStringAsync();
                    _sawmill.Log(LogLevel.Error,
                        $"Webhook returned bad status code when patching message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}"); // Frontier: "Discord"<"Webhook"
                    _relayMessages.Remove(userId);
                    return;
                }
            }

            _relayMessages[userId] = existingEmbed;

            // Actually do the on call relay last, we just need to grab it before we dequeue every message above.
            if (onCallRelay &&
                _onCallData != null)
            {
                existingEmbed.OnCall = true;
                var roleMention = _config.GetCVar(CCVars.DiscordAhelpMention);

                if (!string.IsNullOrEmpty(roleMention))
                {
                    var message = new StringBuilder();
                    message.AppendLine($"<@&{roleMention}>");
                    message.AppendLine("Unanswered SOS");

                    // Need webhook data to get the correct link for that channel rather than on-call data.
                    if (_webhookData is { GuildId: { } guildId, ChannelId: { } channelId })
                    {
                        message.AppendLine(
                            $"**[Go to ahelp](https://discord.com/channels/{guildId}/{channelId}/{existingEmbed.Id})**");
                    }

                    payload = GeneratePayload(message.ToString(), existingEmbed.Username, userId, existingEmbed.CharacterName);

                    var request = await _httpClient.PostAsync($"{_onCallUrl}?wait=true",
                        new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                    var content = await request.Content.ReadAsStringAsync();
                    if (!request.IsSuccessStatusCode)
                    {
                        _sawmill.Log(LogLevel.Error, $"Webhook returned bad status code when posting relay message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}"); // Frontier: Discord<Webhook
                    }
                }
            }
            else
            {
                existingEmbed.OnCall = false;
            }

            _processingChannels.Remove(userId);
        }

        private WebhookPayload GeneratePayload(string messages, string username, Guid userId, string? characterName = null) // Frontier: added Guid
        {
            // Add character name
            if (characterName != null)
                username += $" ({characterName})";

            // If no admins are online, set embed color to red. Otherwise green
            var color = GetNonAfkAdmins().Count > 0 ? 0x41F097 : 0xFF0000;

            // Limit server name to 1500 characters, in case someone tries to be a little funny
            var serverName = _serverName[..Math.Min(_serverName.Length, 1500)];

            var round = _gameTicker.RunLevel switch
            {
                GameRunLevel.PreRoundLobby => _gameTicker.RoundId == 0
                    ? "pre-round lobby after server restart" // first round after server restart has ID == 0
                    : $"pre-round lobby for round {_gameTicker.RoundId + 1}",
                GameRunLevel.InRound => $"round {_gameTicker.RoundId}",
                GameRunLevel.PostRound => $"post-round {_gameTicker.RoundId}",
                _ => throw new ArgumentOutOfRangeException(nameof(_gameTicker.RunLevel),
                    $"{_gameTicker.RunLevel} was not matched."),
            };

            return new WebhookPayload
            {
                Username = username,
                UserID = userId, // Frontier, this is used to identify the players in the webhook
                AvatarUrl = string.IsNullOrWhiteSpace(_avatarUrl) ? null : _avatarUrl,
                Embeds = new List<WebhookEmbed>
                {
                    new()
                    {
                        Description = messages,
                        Color = color,
                        Footer = new WebhookEmbedFooter
                        {
                            Text = $"{serverName} ({round})",
                            IconUrl = string.IsNullOrWhiteSpace(_footerIconUrl) ? null : _footerIconUrl
                        },
                    },
                },
            };
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            if (_timing.RealTime >= _nextTypingCachePrune)
            {
                _nextTypingCachePrune = _timing.RealTime + TypingCachePruneInterval;
                var cutoff = _timing.RealTime - TypingCacheTtl;
                foreach (var (userId, state) in _typingUpdateTimestamps.ToArray())
                {
                    if (state.Timestamp < cutoff)
                        _typingUpdateTimestamps.Remove(userId);
                }
            }

            foreach (var userId in _messageQueues.Keys.ToArray())
            {
                if (_processingChannels.Contains(userId))
                    continue;

                var queue = _messageQueues[userId];
                _messageQueues.Remove(userId);
                if (queue.Count == 0)
                    continue;

                _processingChannels.Add(userId);

                ProcessQueue(userId, queue);
            }
        }

        // Frontier: webhook text messages
        public void OnWebhookBwoinkTextMessage(BwoinkTextMessage message, ServerApi.BwoinkActionBody body)
        {
            // Note for forks:
            AdminData webhookAdminData = new();

            // TODO: fix args
            OnBwoinkInternal(message, SystemUserId, webhookAdminData, body.Username, null, body.UserOnly, body.WebhookUpdate, true);
        }

        protected override void OnBwoinkTextMessage(BwoinkTextMessage message, EntitySessionEventArgs eventArgs)
        {
            base.OnBwoinkTextMessage(message, eventArgs);

            var senderSession = eventArgs.SenderSession;

            // TODO: Sanitize text?
            // Confirm that this person is actually allowed to send a message here.
            var personalChannel = senderSession.UserId == message.UserId;
            var senderAdmin = _adminManager.GetAdminData(senderSession);
            var senderAHelpAdmin = senderAdmin?.HasFlag(AdminFlags.Adminhelp) ?? false;
            var authorized = personalChannel && !message.AdminOnly || senderAHelpAdmin;
            if (!authorized)
            {
                // Unauthorized bwoink (log?)
                return;
            }

            if (message.Text.Trim().Length < MinimumMessageLength)
            {
                RaiseNetworkEvent(
                    new BwoinkTextMessage(
                        message.UserId,
                        SystemUserId,
                        Loc.GetString("bwoink-system-message-too-short", ("length", MinimumMessageLength)),
                        playSound: false),
                    senderSession.Channel);
                return;
            }

            if (_rateLimit.CountAction(eventArgs.SenderSession, RateLimitKey) != RateLimitStatus.Allowed)
                return;

            OnBwoinkInternal(message, eventArgs.SenderSession.UserId, senderAdmin, eventArgs.SenderSession.Name, eventArgs.SenderSession.Channel, false, true, false);
        }

        /// <summary>
        /// Sends a bwoink. Common to both internal messages (sent via the ahelp or admin interface) and webhook messages (sent through the webhook, e.g. via Discord)
        /// </summary>
        /// <param name="message">The message being sent.</param>
        /// <param name="senderId">The network GUID of the person sending the message. Frontier: This can be a SystemUserId if originated from a webhook.</param>
        /// <param name="senderAdmin">The admin privileges of the person sending the message.</param>
        /// <param name="senderName">The name of the person sending the message.</param>
        /// <param name="senderChannel">The channel to send a message to, e.g. in case of failure to send</param>
        /// <param name="sendWebhook">If true, message should be sent off through the webhook if possible</param>
        /// <param name="fromWebhook">Message originated from a webhook (e.g. Discord)</param>
        private void OnBwoinkInternal(BwoinkTextMessage message, NetUserId senderId, AdminData? senderAdmin, string senderName, INetChannel? senderChannel, bool userOnly, bool sendWebhook, bool fromWebhook)
        {
            var senderAHelpAdmin = senderAdmin?.HasFlag(AdminFlags.Adminhelp) ?? false;
            var isNewIncomingAhelpConversation =
                !fromWebhook &&
                senderId == message.UserId &&
                !senderAHelpAdmin &&
                !message.AdminOnly &&
                !_activeConversations.ContainsKey(message.UserId);

            _activeConversations[message.UserId] = DateTime.Now;

            var escapedText = FormattedMessage.EscapeText(message.Text);

            string bwoinkText;
            string adminPrefix = "";

            //Getting an administrator position
            if (_config.GetCVar(CCVars.AhelpAdminPrefix) && senderAdmin is not null && senderAdmin.Title is not null)
            {
                adminPrefix = $"[bold]\\[{senderAdmin.Title}\\][/bold] ";
            }

            if (senderAdmin is not null &&
                senderAdmin.Flags ==
                AdminFlags.Adminhelp) // Mentor. Not full admin. That's why it's colored differently.
            {
                bwoinkText = $"[color=purple]{adminPrefix}{senderName}[/color]";
            }
            else if (fromWebhook || senderAdmin is not null && senderAdmin.HasFlag(AdminFlags.Adminhelp)) // Frontier: anything sent via webhooks are from an admin.
            {
                bwoinkText = $"[color=red]{adminPrefix}{senderName}[/color]";
            }
            else
            {
                bwoinkText = $"{senderName}";
            }

            bwoinkText = $"{(message.AdminOnly ? Loc.GetString("bwoink-message-admin-only") : !message.PlaySound ? Loc.GetString("bwoink-message-silent") : "")}{(fromWebhook ? Loc.GetString("bwoink-message-discord") : "")} {bwoinkText}: {escapedText}";

            // If it's not an admin / admin chooses to keep the sound and message is not an admin only message, then play it.
            var playSound = (!senderAHelpAdmin || message.PlaySound) && !message.AdminOnly;
            var msg = new BwoinkTextMessage(message.UserId, senderId, bwoinkText, playSound: playSound, adminOnly: message.AdminOnly);

            var shouldProcessAutomatedAhelp = !fromWebhook && senderId == message.UserId && !senderAHelpAdmin && !message.AdminOnly && ShouldProcessAutomatedAhelp(message.UserId);
            var shouldTriage = _triageEnabled && shouldProcessAutomatedAhelp;
            var triageCategory = shouldTriage ? ClassifyAHelpCategory(message.Text) : null;

            if (shouldProcessAutomatedAhelp && !string.IsNullOrWhiteSpace(triageCategory))
                _automatedAhelpHistory.Add(message.UserId);

            LogBwoink(msg);

            var admins = GetTargetAdmins();

            // Notify all admins
            if (!userOnly)
            {
                foreach (var channel in admins)
                {
                    RaiseNetworkEvent(msg, channel);

                    if (!string.IsNullOrWhiteSpace(triageCategory))
                    {
                        var triageText = Loc.GetString("bwoink-message-triage", ("category", triageCategory));
                        var triageMsg = new BwoinkTextMessage(message.UserId,
                            SystemUserId,
                            triageText,
                            playSound: false,
                            adminOnly: true);
                        RaiseNetworkEvent(triageMsg, channel);
                    }
                }
            }

            string adminPrefixWebhook = "";

            if (_config.GetCVar(CCVars.AhelpAdminPrefixWebhook) && senderAdmin is not null && senderAdmin.Title is not null)
            {
                adminPrefixWebhook = $"[bold]\\[{senderAdmin.Title}\\][/bold] ";
            }

            // Notify player
            if (_playerManager.TryGetSessionById(message.UserId, out var session) && !message.AdminOnly)
            {
                if (!admins.Contains(session.Channel))
                {
                    // If _overrideClientName is set, we generate a new message with the override name. The admins name will still be the original name for the webhooks.
                    if (_overrideClientName != string.Empty)
                    {
                        string overrideMsgText;
                        // Doing the same thing as above, but with the override name. Theres probably a better way to do this.
                        if (senderAdmin is not null &&
                            senderAdmin.Flags ==
                            AdminFlags.Adminhelp) // Mentor. Not full admin. That's why it's colored differently.
                        {
                            overrideMsgText = $"[color=purple]{adminPrefixWebhook}{_overrideClientName}[/color]";
                        }
                        else if (senderAdmin is not null && senderAdmin.HasFlag(AdminFlags.Adminhelp))
                        {
                            overrideMsgText = $"[color=red]{adminPrefixWebhook}{_overrideClientName}[/color]";
                        }
                        else
                        {
                            overrideMsgText = $"{senderName}"; // Not an admin, name is not overridden.
                        }

                        if (fromWebhook)
                            overrideMsgText = $"(DC) {overrideMsgText}";

                        overrideMsgText = $"{(message.PlaySound ? "" : "(S) ")}{overrideMsgText}: {escapedText}";

                        RaiseNetworkEvent(new BwoinkTextMessage(message.UserId,
                                senderId,
                                overrideMsgText,
                                playSound: playSound),
                            session.Channel);
                    }
                    else
                        RaiseNetworkEvent(msg, session.Channel);
                }

                if (isNewIncomingAhelpConversation &&
                    _panicAutoReplyEnabled &&
                    !string.IsNullOrWhiteSpace(_panicAutoReplyTemplate))
                {
                    var escapedBotName = FormattedMessage.EscapeText(_autoReplyBotName);
                    var panicReply = $"{escapedBotName}: {_panicAutoReplyTemplate}";
                    RaiseNetworkEvent(new BwoinkTextMessage(message.UserId,
                            SystemUserId,
                            panicReply,
                            playSound: false),
                        session.Channel);
                }

                if (shouldProcessAutomatedAhelp &&
                    _autoReplyEnabled &&
                    !string.IsNullOrWhiteSpace(triageCategory) &&
                    TryGetAhelpAutoReply(triageCategory, out var autoReply))
                {
                    var escapedBotName = FormattedMessage.EscapeText(_autoReplyBotName);
                    var autoReplyText = $"{escapedBotName}: {autoReply}";
                    RaiseNetworkEvent(new BwoinkTextMessage(message.UserId,
                            SystemUserId,
                            autoReplyText,
                            playSound: false),
                        session.Channel);
                }
            }

            var sendsWebhook = _webhookUrl != string.Empty;
            if (sendsWebhook && sendWebhook)
            {
                if (!_messageQueues.ContainsKey(msg.UserId))
                    _messageQueues[msg.UserId] = new Queue<DiscordRelayedData>();

                var str = message.Text;
                var unameLength = senderName.Length;

                if (unameLength + str.Length + _maxAdditionalChars > DescriptionMax)
                {
                    str = str[..(DescriptionMax - _maxAdditionalChars - unameLength)];
                }

                var nonAfkAdmins = GetNonAfkAdmins();
                var messageParams = new AHelpMessageParams(
                    senderName,
                    str,
                    senderId != message.UserId,
                    _gameTicker.RoundDuration().ToString("hh\\:mm\\:ss"),
                    _gameTicker.RunLevel,
                    playedSound: playSound,
                    isDiscord: fromWebhook,
                    adminOnly: message.AdminOnly,
                    noReceivers: nonAfkAdmins.Count == 0,
                    triageCategory: triageCategory
                );
                _messageQueues[msg.UserId].Enqueue(GenerateAHelpMessage(messageParams));
            }

            if (admins.Count != 0 || sendsWebhook)
                return;

            // No admin online, let the player know
            if (senderChannel != null)
            {
                var systemText = Loc.GetString("bwoink-system-starmute-message-no-other-users");
                var starMuteMsg = new BwoinkTextMessage(message.UserId, SystemUserId, systemText);
                RaiseNetworkEvent(starMuteMsg, senderChannel);
            }
        }
        // End Frontier: webhook text messages

        private IList<INetChannel> GetNonAfkAdmins()
        {
            return _adminManager.ActiveAdmins
                .Where(p => (_adminManager.GetAdminData(p)?.HasFlag(AdminFlags.Adminhelp) ?? false) &&
                            !_afkManager.IsAfk(p))
                .Select(p => p.Channel)
                .ToList();
        }

        private IList<INetChannel> GetTargetAdmins()
        {
            return _adminManager.ActiveAdmins
                .Where(p => _adminManager.GetAdminData(p)?.HasFlag(AdminFlags.Adminhelp) ?? false)
                .Select(p => p.Channel)
                .ToList();
        }

        private string? ClassifyAHelpCategory(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return null;

            var normalized = message.ToLowerInvariant();
            var bestScore = 0;
            string? bestCategory = null;

            foreach (var category in GetTriageCategories())
            {
                if (!TryGetActiveTriageKeywords(category, out var keywords))
                    continue;

                var score = 0;

                foreach (var keyword in keywords)
                {
                    if (!normalized.Contains(keyword.ToLowerInvariant()))
                        continue;

                    score += keyword.Contains(' ') ? 2 : 1;
                }

                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestCategory = category;
            }

            return bestScore > 0 ? bestCategory : null;
        }

        public IEnumerable<string> GetTriageCategories()
        {
            return DefaultAhelpTriageRules.Keys
                .Where(k => !_removedTriageCategories.Contains(k))
                .Concat(_customTriageRules.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k);
        }

        public bool TryGetTriageRuleKeywords(string category, out string keywords)
        {
            keywords = string.Empty;

            if (!TryGetStoredTriageKeywords(category, out var activeKeywords))
                return false;

            keywords = string.Join(", ", activeKeywords);
            return true;
        }

        public bool TrySetTriageRuleKeywords(string category, string? keywords)
        {
            category = category.Trim();
            if (category.Length == 0)
                return false;

            if (_customTriageRules.ContainsKey(category))
            {
                if (string.IsNullOrWhiteSpace(keywords))
                {
                    _customTriageRules.Remove(category);
                    return true;
                }

                if (!TryParseTriageKeywords(keywords, out var customKeywords))
                    return false;

                _customTriageRules[category] = customKeywords;
                return true;
            }

            if (!DefaultAhelpTriageRules.ContainsKey(category) || _removedTriageCategories.Contains(category))
                return false;

            if (string.IsNullOrWhiteSpace(keywords))
            {
                _triageRuleOverrides.Remove(category);
                return true;
            }

            if (!TryParseTriageKeywords(keywords, out var parsed))
                return false;

            _triageRuleOverrides[category] = parsed;
            return true;
        }

        public void SetTriageEnabled(bool enabled)
        {
            _triageEnabled = enabled;
        }

        public bool SetTriageRuleEnabled(string category, bool enabled)
        {
            category = category.Trim();
            if (category.Length == 0 || !TryGetStoredTriageKeywords(category, out _))
                return false;

            if (enabled)
                return _enabledTriageCategories.Add(category);

            return _enabledTriageCategories.Remove(category);
        }

        private bool TryGetActiveTriageKeywords(string category, out string[] keywords)
        {
            if (!_enabledTriageCategories.Contains(category))
            {
                keywords = Array.Empty<string>();
                return false;
            }

            return TryGetStoredTriageKeywords(category, out keywords);
        }

        private bool TryGetStoredTriageKeywords(string category, out string[] keywords)
        {
            keywords = Array.Empty<string>();

            if (_customTriageRules.TryGetValue(category, out var customKeywords))
            {
                keywords = customKeywords;
                return true;
            }

            if (_removedTriageCategories.Contains(category))
                return false;

            if (_triageRuleOverrides.TryGetValue(category, out var overrideKeywords))
            {
                keywords = overrideKeywords;
                return true;
            }

            if (!DefaultAhelpTriageRules.TryGetValue(category, out var defaultKeywords))
                return false;

            keywords = defaultKeywords;
            return true;
        }

        private bool ShouldProcessAutomatedAhelp(NetUserId userId)
        {
            return !_automatedAhelpHistory.Contains(userId);
        }

        public IEnumerable<string> GetAutoReplyCategories()
        {
            return AhelpAutoReplyLocKeysByCategory.Keys
                .Where(k => !_removedAutoReplyCategories.Contains(k))
                .Concat(_customAutoReplyTemplates.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k);
        }

        public bool TryGetAutoReplyTemplate(string category, out string template)
        {
            return TryGetStoredAhelpAutoReply(category, out template);
        }

        public bool TrySetAutoReplyTemplate(string category, string? template)
        {
            category = category.Trim();
            if (category.Length == 0)
                return false;

            // Empty template clears any existing custom/override entry.
            if (string.IsNullOrWhiteSpace(template))
            {
                var removed = _customAutoReplyTemplates.Remove(category);
                removed |= _autoReplyOverrides.Remove(category);
                return removed;
            }

            template = template.Trim();

            // Default-backed categories use the override slot so reset works.
            if (AhelpAutoReplyLocKeysByCategory.ContainsKey(category) && !_removedAutoReplyCategories.Contains(category))
            {
                _autoReplyOverrides[category] = template;
                return true;
            }

            // Otherwise store as a fully custom entry. Categories that only
            // exist as triage rules (or are brand new) land here.
            _customAutoReplyTemplates[category] = template;
            return true;
        }

        public void SetAutoReplyEnabled(bool enabled)
        {
            _autoReplyEnabled = enabled;
        }

        public void SetPanicAutoReplyEnabled(bool enabled)
        {
            _panicAutoReplyEnabled = enabled;
        }

        public void SetPanicAutoReplyTemplate(string template)
        {
            _panicAutoReplyTemplate = template.Trim();
        }

        public bool SetAutoReplyRuleEnabled(string category, bool enabled)
        {
            category = category.Trim();
            if (category.Length == 0 || !TryGetStoredAhelpAutoReply(category, out _))
                return false;

            if (enabled)
                return _enabledAutoReplyCategories.Add(category);

            return _enabledAutoReplyCategories.Remove(category);
        }

        public void SetAutoReplyBotName(string botName)
        {
            botName = botName.Trim();
            _autoReplyBotName = string.IsNullOrWhiteSpace(botName) ? "Auto-Reply" : botName;
        }

        private bool TryGetAhelpAutoReply(string category, out string autoReply)
        {
            if (!_enabledAutoReplyCategories.Contains(category))
            {
                autoReply = string.Empty;
                return false;
            }

            return TryGetStoredAhelpAutoReply(category, out autoReply);
        }

        private bool TryGetStoredAhelpAutoReply(string category, out string autoReply)
        {
            autoReply = string.Empty;

            if (_customAutoReplyTemplates.TryGetValue(category, out var customReply))
            {
                autoReply = customReply;
                return true;
            }

            if (_removedAutoReplyCategories.Contains(category))
                return false;

            if (_autoReplyOverrides.TryGetValue(category, out var overrideReply) && overrideReply is not null)
            {
                autoReply = overrideReply;
                return true;
            }

            if (!AhelpAutoReplyLocKeysByCategory.TryGetValue(category, out var locKey))
                return false;

            autoReply = Loc.GetString(locKey);
            return true;
        }

        public bool TryAddOrRestoreAhelpCategory(string category, string template, string keywords)
        {
            category = category.Trim();
            template = template.Trim();

            if (category.Length == 0 || template.Length == 0)
                return false;

            if (!TryParseTriageKeywords(keywords, out var parsedKeywords))
                return false;

            if (AhelpAutoReplyLocKeysByCategory.ContainsKey(category))
            {
                _removedAutoReplyCategories.Remove(category);
                _autoReplyOverrides[category] = template;
            }
            else
            {
                _customAutoReplyTemplates[category] = template;
            }

            if (DefaultAhelpTriageRules.ContainsKey(category))
            {
                _removedTriageCategories.Remove(category);
                _triageRuleOverrides[category] = parsedKeywords;
            }
            else
            {
                _customTriageRules[category] = parsedKeywords;
            }

            // New or restored categories should become active immediately
            // so admins can validate behavior without an extra enable step.
            _enabledAutoReplyCategories.Add(category);
            _enabledTriageCategories.Add(category);

            return true;
        }

        public bool TryRemoveAhelpCategory(string category)
        {
            category = category.Trim();
            if (category.Length == 0)
                return false;

            var changed = false;

            changed |= _customAutoReplyTemplates.Remove(category);
            changed |= _customTriageRules.Remove(category);
            changed |= _autoReplyOverrides.Remove(category);
            changed |= _triageRuleOverrides.Remove(category);
            changed |= _enabledAutoReplyCategories.Remove(category);
            changed |= _enabledTriageCategories.Remove(category);

            if (AhelpAutoReplyLocKeysByCategory.ContainsKey(category))
                changed |= _removedAutoReplyCategories.Add(category);

            if (DefaultAhelpTriageRules.ContainsKey(category))
                changed |= _removedTriageCategories.Add(category);

            return changed;
        }

        private static bool TryParseTriageKeywords(string keywords, out string[] parsedKeywords)
        {
            parsedKeywords = keywords
                .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return parsedKeywords.Length > 0;
        }

        private bool CanManageAhelpConfig(ICommonSession? session)
        {
            return session != null && _adminManager.HasAdminFlag(session, AdminFlags.Admin);
        }

        private sealed class SharedAdminMacroRecord
        {
            public string Name { get; set; } = string.Empty;
            public string Command { get; set; } = string.Empty;
            public string UpdatedBy { get; set; } = string.Empty;
        }

        private bool CanManageSharedAdminMacros(ICommonSession? session)
        {
            return CanManageAhelpConfig(session);
        }

        private void SendSharedAdminMacrosState(ICommonSession session)
        {
            var macros = _sharedAdminMacros.Values
                .OrderBy(macro => macro.Name, StringComparer.OrdinalIgnoreCase)
                .Select(macro => new SharedAdminMacroState(macro.Name, macro.Command, macro.UpdatedBy))
                .ToArray();

            RaiseNetworkEvent(new SharedAdminMacrosStateMessage(macros), session);
        }

        private void BroadcastSharedAdminMacrosStateToAdmins()
        {
            foreach (var session in _playerManager.Sessions)
            {
                if (!CanManageSharedAdminMacros(session))
                    continue;

                SendSharedAdminMacrosState(session);
            }
        }

        private bool TryUpsertSharedAdminMacro(string name, string command, string updatedBy)
        {
            name = name.Trim();
            command = command.Trim();
            updatedBy = updatedBy.Trim();

            if (name.Length == 0 || command.Length == 0)
                return false;

            _sharedAdminMacros[name] = new SharedAdminMacroRecord
            {
                Name = name,
                Command = command,
                UpdatedBy = updatedBy.Length == 0 ? "Unknown" : updatedBy,
            };

            return true;
        }

        private bool TryDeleteSharedAdminMacro(string name)
        {
            name = name.Trim();
            return name.Length != 0 && _sharedAdminMacros.Remove(name);
        }

        private static string EscapeSharedMacroValue(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static string UnescapeSharedMacroValue(string value)
        {
            var chars = new List<char>(value.Length);
            var escaping = false;

            foreach (var ch in value)
            {
                if (!escaping)
                {
                    if (ch == '\\')
                    {
                        escaping = true;
                        continue;
                    }

                    chars.Add(ch);
                    continue;
                }

                chars.Add(ch switch
                {
                    'r' => '\r',
                    'n' => '\n',
                    't' => '\t',
                    '\\' => '\\',
                    _ => ch,
                });

                escaping = false;
            }

            if (escaping)
                chars.Add('\\');

            return new string(chars.ToArray());
        }

        private static bool TryParseSharedAdminMacro(string line, out SharedAdminMacroRecord? record)
        {
            record = null;

            var firstSeparator = line.IndexOf('\t');
            if (firstSeparator <= 0 || firstSeparator >= line.Length - 1)
                return false;

            var secondSeparator = line.IndexOf('\t', firstSeparator + 1);
            if (secondSeparator <= firstSeparator)
                return false;

            var name = UnescapeSharedMacroValue(line[..firstSeparator]).Trim();
            var command = UnescapeSharedMacroValue(line[(firstSeparator + 1)..secondSeparator]).Trim();
            var updatedBy = UnescapeSharedMacroValue(line[(secondSeparator + 1)..]).Trim();

            if (name.Length == 0 || command.Length == 0)
                return false;

            record = new SharedAdminMacroRecord
            {
                Name = name,
                Command = command,
                UpdatedBy = updatedBy.Length == 0 ? "Unknown" : updatedBy,
            };

            return true;
        }

        private void LoadSharedAdminMacros()
        {
            _sharedAdminMacros.Clear();

            try
            {
                if (!_resources.UserData.Exists(SharedAdminMacrosPath))
                    return;

                using var reader = _resources.UserData.OpenText(SharedAdminMacrosPath);
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (!TryParseSharedAdminMacro(line, out var record) || record == null)
                        continue;

                    _sharedAdminMacros[record.Name] = record;
                }
            }
            catch (IOException e)
            {
                _sawmill.Warning($"Failed to read shared admin macros file: {e.Message}");
            }
            catch (Exception e)
            {
                _sawmill.Warning($"Failed to load shared admin macros: {e.Message}");
            }
        }

        private void PersistSharedAdminMacros()
        {
            try
            {
                using var writer = _resources.UserData.OpenWriteText(SharedAdminMacrosPath);
                foreach (var macro in _sharedAdminMacros.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var name = EscapeSharedMacroValue(macro.Name);
                    var command = EscapeSharedMacroValue(macro.Command);
                    var updatedBy = EscapeSharedMacroValue(macro.UpdatedBy);
                    writer.WriteLine($"{name}\t{command}\t{updatedBy}");
                }
            }
            catch (IOException e)
            {
                _sawmill.Warning($"Failed to persist shared admin macros file: {e.Message}");
            }
            catch (Exception e)
            {
                _sawmill.Warning($"Failed to persist shared admin macros: {e.Message}");
            }
        }

        private void SendAhelpAdminState(ICommonSession session)
        {
            var categories = GetAutoReplyCategories()
                .Union(GetTriageCategories(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category)
                .Select(category =>
                {
                    var hasAutoReply = TryGetAutoReplyTemplate(category, out var template);
                    var hasTriage = TryGetTriageRuleKeywords(category, out var keywords);
                    var isDefault = AhelpAutoReplyLocKeysByCategory.ContainsKey(category) || DefaultAhelpTriageRules.ContainsKey(category);

                    return new AhelpAdminCategoryState(
                        category,
                        hasAutoReply ? template : string.Empty,
                        hasTriage ? keywords : string.Empty,
                        isDefault,
                        hasAutoReply,
                        hasTriage,
                        hasAutoReply && _enabledAutoReplyCategories.Contains(category),
                        hasTriage && _enabledTriageCategories.Contains(category));
                })
                .ToArray();

            RaiseNetworkEvent(new AhelpAdminConfigStateMessage(new AhelpAdminConfigState(
                _autoReplyEnabled,
                _panicAutoReplyEnabled,
                _triageEnabled,
                _autoReplyBotName,
                _panicAutoReplyTemplate,
                categories)), session);
        }

        private void BroadcastAhelpAdminStateToAdmins()
        {
            foreach (var session in _playerManager.Sessions)
            {
                if (!CanManageAhelpConfig(session))
                    continue;

                SendAhelpAdminState(session);
            }
        }

        private void OnAhelpAdminStateRequested(RequestAhelpAdminStateMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            SendAhelpAdminState(args.SenderSession!);
        }

        private void OnSetAhelpAutoReplyEnabled(SetAhelpAutoReplyEnabledMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            SetAutoReplyEnabled(message.Enabled);
            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnSetAhelpPanicAutoReplyEnabled(SetAhelpPanicAutoReplyEnabledMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            SetPanicAutoReplyEnabled(message.Enabled);
            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnSetAhelpPanicAutoReplyTemplate(SetAhelpPanicAutoReplyTemplateMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            SetPanicAutoReplyTemplate(message.Template);
            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnSetAhelpTriageEnabled(SetAhelpTriageEnabledMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            SetTriageEnabled(message.Enabled);
            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnSetAhelpAutoReplyBotName(SetAhelpAutoReplyBotNameMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            SetAutoReplyBotName(message.BotName);
            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnSetAhelpCategoryAutoReplyEnabled(SetAhelpCategoryAutoReplyEnabledMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            if (!SetAutoReplyRuleEnabled(message.Category, message.Enabled))
                return;

            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnSetAhelpCategoryTriageEnabled(SetAhelpCategoryTriageEnabledMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            if (!SetTriageRuleEnabled(message.Category, message.Enabled))
                return;

            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnRequestSharedAdminMacros(RequestSharedAdminMacrosMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageSharedAdminMacros(args.SenderSession))
                return;

            SendSharedAdminMacrosState(args.SenderSession!);
        }

        private void OnUpsertSharedAdminMacro(UpsertSharedAdminMacroMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageSharedAdminMacros(args.SenderSession))
                return;

            if (!TryUpsertSharedAdminMacro(message.Name, message.Command, args.SenderSession?.Name ?? string.Empty))
                return;

            PersistSharedAdminMacros();
            BroadcastSharedAdminMacrosStateToAdmins();
        }

        private void OnDeleteSharedAdminMacro(DeleteSharedAdminMacroMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageSharedAdminMacros(args.SenderSession))
                return;

            if (!TryDeleteSharedAdminMacro(message.Name))
                return;

            PersistSharedAdminMacros();
            BroadcastSharedAdminMacrosStateToAdmins();
        }

        // ---------- Ship Incident Inspector (on-demand) ----------
        // Scans owned ships once per admin click. Strict admin-flag gate, response
        // is unicast back to the requesting session only.
        private void OnRequestPlayerShipInspection(RequestPlayerShipInspectionMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
                return;

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey))
            {
                RaiseNetworkEvent(
                    new PlayerShipInspectionResponseMessage(message.OwnerUserId ?? string.Empty, Array.Empty<PlayerShipSummary>(), "empty-owner"),
                    session.Channel);
                return;
            }

            var ownerOnline = TryParseUserId(ownerKey, out var ownerGuid)
                && _playerManager.TryGetSessionById(new NetUserId(ownerGuid), out _);

            var summaries = new List<PlayerShipSummary>();

            // Iterate ships only — a ShuttleDeedComponent also lives on ID cards;
            // requiring ShuttleComponent narrows to actual grids and avoids dupes.
            var query = EntityQueryEnumerator<ShuttleDeedComponent, ShuttleComponent>();
            var transformSystem = EntityManager.System<SharedTransformSystem>();
            var metaQuery = GetEntityQuery<MetaDataComponent>();
            var ftlQuery = GetEntityQuery<FTLComponent>();
            var xformQuery = GetEntityQuery<TransformComponent>();

            while (query.MoveNext(out var uid, out var deed, out _))
            {
                if (!string.Equals(deed.OwnerUserId, ownerKey, StringComparison.Ordinal))
                    continue;

                var inFtl = false;
                var ftlState = "Available";
                if (ftlQuery.TryGetComponent(uid, out var ftl))
                {
                    inFtl = ftl.State != FTLState.Available;
                    ftlState = ftl.State.ToString();
                }

                var worldPos = Vector2.Zero;
                var mapName = "Unknown";
                if (xformQuery.TryGetComponent(uid, out var xform))
                {
                    worldPos = transformSystem.GetWorldPosition(xform);
                    if (xform.MapUid is { } mapUid && metaQuery.TryGetComponent(mapUid, out var mapMeta))
                        mapName = mapMeta.EntityName;
                }

                summaries.Add(new PlayerShipSummary(
                    name: deed.ShuttleName ?? "Unknown",
                    suffix: deed.ShuttleNameSuffix ?? string.Empty,
                    gridUid: GetNetEntity(uid),
                    worldPosition: worldPos,
                    mapName: mapName,
                    inFtl: inFtl,
                    ftlState: ftlState,
                    ownerOnline: ownerOnline,
                    purchasedWithVoucher: deed.PurchasedWithVoucher));
            }

            RaiseNetworkEvent(
                new PlayerShipInspectionResponseMessage(ownerKey, summaries.ToArray()),
                session.Channel);
        }

        private static bool TryParseUserId(string raw, out Guid guid)
        {
            return Guid.TryParse(raw, out guid);
        }

        // ---------- Player Recovery Snapshot (on-demand) ----------
        private void OnRequestPlayerSnapshot(RequestPlayerSnapshotMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
                return;

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey) || !TryParseUserId(ownerKey, out var guid))
            {
                RaiseNetworkEvent(
                    new PlayerSnapshotResponseMessage(message.OwnerUserId ?? string.Empty, null, "invalid-owner"),
                    session.Channel);
                return;
            }

            var netId = new NetUserId(guid);
            var online = _playerManager.TryGetSessionById(netId, out var targetSession);

            EntityUid? attached = online ? targetSession!.AttachedEntity : null;
            EntityUid? owned = null;
            var hasMind = false;
            string[] roleNames = Array.Empty<string>();

            if (online && _minds.TryGetMind(targetSession!, out var mindId, out var mind))
            {
                hasMind = true;
                owned = mind.OwnedEntity;
                var roles = EntityManager.System<SharedRoleSystem>();
                roleNames = roles.MindGetAllRoleInfo(mindId)
                    .Select(r => r.Name ?? string.Empty)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToArray();
            }

            var attachedName = TryGetEntityName(attached);
            var ownedName = TryGetEntityName(owned);
            var detached = attached.HasValue && owned.HasValue && attached.Value != owned.Value;

            var worldPos = Vector2.Zero;
            var mapName = "Unknown";
            if (attached is { } a && TryComp<TransformComponent>(a, out var xform))
            {
                var ts = EntityManager.System<SharedTransformSystem>();
                worldPos = ts.GetWorldPosition(xform);
                if (xform.MapUid is { } mapUid && TryComp<MetaDataComponent>(mapUid, out var mapMeta))
                    mapName = mapMeta.EntityName;
            }

            var snapshot = new PlayerSnapshot(
                online: online,
                hasMind: hasMind,
                attachedEntityName: attachedName,
                ownedEntityName: ownedName,
                detachedFromBody: detached,
                worldPosition: worldPos,
                mapName: mapName,
                roles: roleNames);

            RaiseNetworkEvent(new PlayerSnapshotResponseMessage(ownerKey, snapshot), session.Channel);
        }

        private string TryGetEntityName(EntityUid? uid)
        {
            if (uid is not { } u || !TryComp<MetaDataComponent>(u, out var meta))
                return string.Empty;
            return meta.EntityName;
        }

        private void OnRequestSpawnAhelpItem(RequestSpawnAhelpItemMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            var requestPrototypeId = message.PrototypeId ?? string.Empty;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new SpawnAhelpItemResponseMessage(message.OwnerUserId, requestPrototypeId, false, "not-authorized"), session.Channel);
                return;
            }

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey) || !TryParseUserId(ownerKey, out var ownerGuid))
            {
                RaiseNetworkEvent(new SpawnAhelpItemResponseMessage(message.OwnerUserId ?? string.Empty, requestPrototypeId, false, "invalid-owner"), session.Channel);
                return;
            }

            var prototypeId = message.PrototypeId?.Trim();
            if (string.IsNullOrEmpty(prototypeId) || !_prototype.TryIndex<EntityPrototype>(prototypeId, out var proto))
            {
                RaiseNetworkEvent(new SpawnAhelpItemResponseMessage(ownerKey, prototypeId ?? requestPrototypeId, false, "invalid-prototype"), session.Channel);
                return;
            }

            if (!proto.HasComponent<ItemComponent>())
            {
                RaiseNetworkEvent(new SpawnAhelpItemResponseMessage(ownerKey, prototypeId, false, "not-item", proto.Name), session.Channel);
                return;
            }

            if (!_playerManager.TryGetSessionById(new NetUserId(ownerGuid), out var targetSession))
            {
                RaiseNetworkEvent(new SpawnAhelpItemResponseMessage(ownerKey, prototypeId, false, "offline", proto.Name), session.Channel);
                return;
            }

            if (!targetSession.AttachedEntity.HasValue)
            {
                RaiseNetworkEvent(new SpawnAhelpItemResponseMessage(ownerKey, prototypeId, false, "no-attached-entity", proto.Name), session.Channel);
                return;
            }

            var attached = targetSession.AttachedEntity.Value;
            if (!TryComp<TransformComponent>(attached, out var xform))
            {
                RaiseNetworkEvent(new SpawnAhelpItemResponseMessage(ownerKey, prototypeId, false, "no-transform", proto.Name), session.Channel);
                return;
            }

            var spawnCoords = xform.Coordinates;
            if (TryFindAdjacentFloorSpawn(xform, out var adjacentCoords))
                spawnCoords = adjacentCoords;

            Spawn(prototypeId, spawnCoords);

            _sawmill.Info($"Admin {session.Name} ({session.UserId}) spawned item '{prototypeId}' near ahelp player {ownerKey} at {spawnCoords.Position}.");
            RaiseNetworkEvent(new SpawnAhelpItemResponseMessage(ownerKey, prototypeId, true, itemName: proto.Name, spawnPosition: spawnCoords.Position), session.Channel);
        }

        private bool TryFindAdjacentFloorSpawn(TransformComponent xform, out EntityCoordinates coords)
        {
            coords = xform.Coordinates;

            if (xform.GridUid is not { } gridUid)
                return false;

            if (!TryComp<MapGridComponent>(gridUid, out var grid))
                return false;

            if (!_mapSystem.TryGetTileRef(gridUid, grid, xform.Coordinates, out var centerTile))
                return false;

            var offsets = new[]
            {
                new Vector2i(1, 0),
                new Vector2i(-1, 0),
                new Vector2i(0, 1),
                new Vector2i(0, -1),
            };

            var start = _random.Next(offsets.Length);
            for (var i = 0; i < offsets.Length; i++)
            {
                var offset = offsets[(start + i) % offsets.Length];
                if (!_mapSystem.TryGetTileRef(gridUid, grid, centerTile.GridIndices + offset, out var tile))
                    continue;

                if (tile.Tile.IsEmpty || _turf.IsTileBlocked(tile, CollisionGroup.MobMask))
                    continue;

                coords = _turf.GetTileCenter(tile);
                return true;
            }

            return false;
        }

        private void OnRequestAdminStatistics(RequestAdminStatisticsMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new AdminStatisticsResponseMessage(null, "not-authorized"), session.Channel);
                return;
            }

            var snapshot = BuildAdminStatisticsSnapshot();
            RaiseNetworkEvent(new AdminStatisticsResponseMessage(snapshot), session.Channel);
        }

        private AdminStatisticsSnapshot BuildAdminStatisticsSnapshot()
        {
            var connectedSessions = _playerManager.Sessions
                .Where(session => session.Status is SessionStatus.Connected or SessionStatus.InGame)
                .ToArray();

            var onlinePlayers = connectedSessions.Length;
            var averagePingMs = onlinePlayers == 0
                ? 0
                : (int) Math.Round(connectedSessions.Average(session => (double) session.Ping));
            var maxPingMs = onlinePlayers == 0 ? 0 : connectedSessions.Max(session => (int) session.Ping);
            var highPingPlayers = connectedSessions.Count(session => session.Ping >= 200);

            var roleSlots = BuildRoleSlotStatistics();
            var antags = BuildAntagList(connectedSessions);

            return new AdminStatisticsSnapshot(
                onlinePlayers,
                averagePingMs,
                maxPingMs,
                highPingPlayers,
                _gameTicker.RoundId,
                _gameTicker.RunLevel.ToString(),
                _gameTicker.RoundDuration(),
                _timing.CurTime,
                roleSlots,
                antags);
        }

        private AdminStatisticsRoleInfo[] BuildRoleSlotStatistics()
        {
            var totals = new Dictionary<ProtoId<JobPrototype>, (int Taken, int? Open)>();

            foreach (var stationUid in _station.GetStations())
            {
                if (!TryComp<StationJobsComponent>(stationUid, out var stationJobs))
                    continue;

                IReadOnlyDictionary<ProtoId<JobPrototype>, int?> currentJobs;
                IReadOnlyDictionary<ProtoId<JobPrototype>, int?> roundStartJobs;

                try
                {
                    currentJobs = _stationJobs.GetJobs(stationUid, stationJobs);
                    roundStartJobs = _stationJobs.GetRoundStartJobs(stationUid);
                }
                catch (ArgumentException)
                {
                    // Some entities can be tagged as stations without valid jobs data.
                    // Skip them so admin stats remain available.
                    continue;
                }

                foreach (var (jobId, maxSlots) in roundStartJobs)
                {
                    if (!currentJobs.TryGetValue(jobId, out var currentOpen))
                        continue;

                    var trackedTaken = CountTrackedAssignments(stationUid, jobId);

                    int taken;
                    int? open;

                    if (maxSlots == null || currentOpen == null)
                    {
                        taken = trackedTaken;
                        open = null;
                    }
                    else
                    {
                        open = Math.Max(currentOpen.Value, 0);
                        taken = Math.Max(maxSlots.Value - open.Value, 0);
                    }

                    if (!totals.TryGetValue(jobId, out var existing))
                    {
                        totals[jobId] = (taken, open);
                        continue;
                    }

                    totals[jobId] = (
                        existing.Taken + taken,
                        existing.Open == null || open == null ? null : existing.Open.Value + open.Value);
                }
            }

            return totals
                .Select(pair => new AdminStatisticsRoleInfo(
                    GetRoleName(pair.Key),
                    pair.Value.Taken,
                    pair.Value.Open))
                .OrderBy(role => role.RoleName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private int CountTrackedAssignments(EntityUid station, ProtoId<JobPrototype> jobId)
        {
            var total = 0;

            foreach (var session in _playerManager.Sessions)
            {
                if (!_stationJobs.TryGetPlayerJobs(station, session.UserId, out var assignedJobs))
                    continue;

                total += assignedJobs.Count(role => role == jobId);
            }

            return total;
        }

        private string GetRoleName(ProtoId<JobPrototype> jobId)
        {
            if (_prototype.TryIndex<JobPrototype>(jobId, out var prototype))
                return prototype.LocalizedName;

            return jobId;
        }

        private string[] BuildAntagList(ICommonSession[] connectedSessions)
        {
            var roleSystem = EntityManager.System<SharedRoleSystem>();
            var antags = new List<string>();

            foreach (var session in connectedSessions)
            {
                if (!_minds.TryGetMind(session, out var mindId, out _))
                    continue;

                if (!roleSystem.MindIsAntagonist(mindId))
                    continue;

                var characterName = _minds.GetCharacterName(session.UserId);
                var displayName = string.IsNullOrWhiteSpace(characterName) ||
                                  string.Equals(characterName, session.Name, StringComparison.OrdinalIgnoreCase)
                    ? session.Name
                    : $"{session.Name} ({characterName})";

                antags.Add(displayName);
            }

            antags.Sort(StringComparer.OrdinalIgnoreCase);
            return antags.ToArray();
        }

        private void OnRequestPlayerBankInfo(RequestPlayerBankInfoMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new PlayerBankInfoResponseMessage(message.OwnerUserId, 0, "not-authorized"), session.Channel);
                return;
            }

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey) || !TryParseUserId(ownerKey, out var guid))
            {
                RaiseNetworkEvent(new PlayerBankInfoResponseMessage(message.OwnerUserId ?? string.Empty, 0, "invalid-user"), session.Channel);
                return;
            }

            var netId = new NetUserId(guid);
            if (!_playerManager.TryGetSessionById(netId, out var targetSession))
            {
                RaiseNetworkEvent(new PlayerBankInfoResponseMessage(ownerKey, 0, "user-offline"), session.Channel);
                return;
            }

            if (!targetSession.AttachedEntity.HasValue)
            {
                RaiseNetworkEvent(new PlayerBankInfoResponseMessage(ownerKey, 0, "user-no-entity"), session.Channel);
                return;
            }

            if (!TryComp<Content.Shared._NF.Bank.Components.BankAccountComponent>(targetSession.AttachedEntity.Value, out var bankAccount))
            {
                RaiseNetworkEvent(new PlayerBankInfoResponseMessage(ownerKey, 0, "no-bank-account"), session.Channel);
                return;
            }

            var balance = bankAccount.Balance;
            RaiseNetworkEvent(new PlayerBankInfoResponseMessage(ownerKey, balance), session.Channel);
        }

        private void OnRequestModifyPlayerBank(RequestModifyPlayerBankMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new ModifyPlayerBankResponseMessage(message.OwnerUserId, 0, "not-authorized"), session.Channel);
                return;
            }

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey) || !TryParseUserId(ownerKey, out var guid))
            {
                RaiseNetworkEvent(new ModifyPlayerBankResponseMessage(message.OwnerUserId ?? string.Empty, 0, "invalid-user"), session.Channel);
                return;
            }

            var netId = new NetUserId(guid);
            if (!_playerManager.TryGetSessionById(netId, out var targetSession))
            {
                RaiseNetworkEvent(new ModifyPlayerBankResponseMessage(ownerKey, 0, "user-offline"), session.Channel);
                return;
            }

            if (!targetSession.AttachedEntity.HasValue)
            {
                RaiseNetworkEvent(new ModifyPlayerBankResponseMessage(ownerKey, 0, "user-no-entity"), session.Channel);
                return;
            }

            var attached = targetSession.AttachedEntity.Value;
            if (!TryComp<Content.Shared._NF.Bank.Components.BankAccountComponent>(attached, out var bankAccount))
            {
                RaiseNetworkEvent(new ModifyPlayerBankResponseMessage(ownerKey, 0, "no-bank-account"), session.Channel);
                return;
            }

            if (message.Amount == 0)
            {
                RaiseNetworkEvent(new ModifyPlayerBankResponseMessage(ownerKey, bankAccount.Balance, "invalid-amount"), session.Channel);
                return;
            }

            // Use BankSystem APIs to ensure both the component AND the player profile are updated.
            // This is critical because bank balances are persisted via the player's character profile.
            bool success;
            if (message.Amount > 0)
            {
                success = _bank.TryBankDeposit(attached, message.Amount);
            }
            else
            {
                // For withdrawals, only deduct what's available unless they have at least the requested amount.
                var withdrawAmount = -message.Amount;
                success = _bank.TryBankWithdraw(attached, withdrawAmount);
            }

            // Re-read balance after operation
            var newBalance = TryComp<Content.Shared._NF.Bank.Components.BankAccountComponent>(attached, out var updatedBank)
                ? updatedBank.Balance
                : 0;

            if (!success)
            {
                _sawmill.Warning($"Admin {session.Name} ({session.UserId}) failed to modify {targetSession.Name}'s bank balance by {message.Amount} (reason: {message.Reason}). Current balance: {newBalance}");
                RaiseNetworkEvent(new ModifyPlayerBankResponseMessage(ownerKey, newBalance, "transaction-failed"), session.Channel);
                return;
            }

            // Log the admin action with full audit trail
            _sawmill.Info($"Admin {session.Name} ({session.UserId}) modified {targetSession.Name}'s bank balance by {message.Amount} (reason: {message.Reason}). New balance: {newBalance}");

            RaiseNetworkEvent(new ModifyPlayerBankResponseMessage(ownerKey, newBalance), session.Channel);

            // Drop an admin-only note in the affected player's ahelp channel so the
            // adjustment shows up in admin chat history and is relayed to Discord
            // attributed to the acting admin (for note/audit purposes).
            try
            {
                var auditAmount = Math.Abs(message.Amount);
                var auditReason = string.IsNullOrWhiteSpace(message.Reason)
                    ? Loc.GetString("bwoink-banking-audit-no-reason")
                    : message.Reason;
                string auditKey;
                if (message.Amount > 0)
                    auditKey = "bwoink-banking-audit-add";
                else if (newBalance == 0)
                    auditKey = "bwoink-banking-audit-confiscate";
                else
                    auditKey = "bwoink-banking-audit-remove";

                var auditBody = Loc.GetString(auditKey,
                    ("amount", auditAmount),
                    ("balance", newBalance),
                    ("reason", auditReason));

                // Pre-styled in-game text: gold "🏦 Banking" tag + red admin name + body.
                // We build the markup ourselves and dispatch directly to admins so the
                // color/emoji aren't escaped by OnBwoinkInternal.
                var senderAdmin = _adminManager.GetAdminData(session);
                var senderColor = senderAdmin is not null && senderAdmin.Flags == AdminFlags.Adminhelp
                    ? "purple"
                    : "red";
                var adminPrefix = "";
                if (_config.GetCVar(CCVars.AhelpAdminPrefix) && senderAdmin?.Title is not null)
                    adminPrefix = $"[bold]\\[{senderAdmin.Title}\\][/bold] ";

                var styledText =
                    $"{Loc.GetString("bwoink-message-admin-only")} " +
                    $"[color=gold][bold]\\[\U0001F3E6 Banking\\][/bold][/color] " +
                    $"[color={senderColor}]{adminPrefix}{session.Name}[/color]: {auditBody}";

                _activeConversations[netId] = DateTime.Now;

                var auditMsg = new BwoinkTextMessage(netId, session.UserId, styledText, playSound: false, adminOnly: true);
                LogBwoink(auditMsg);

                foreach (var adminChannel in GetTargetAdmins())
                    RaiseNetworkEvent(auditMsg, adminChannel);

                // Discord relay: enqueue a plain-text variant for the webhook (admin-only).
                if (_webhookUrl != string.Empty)
                {
                    if (!_messageQueues.ContainsKey(netId))
                        _messageQueues[netId] = new Queue<DiscordRelayedData>();

                    var nonAfkAdmins = GetNonAfkAdmins();
                    var discordParams = new AHelpMessageParams(
                        session.Name,
                        auditBody,
                        isAdmin: true,
                        _gameTicker.RoundDuration().ToString("hh\\:mm\\:ss"),
                        _gameTicker.RunLevel,
                        playedSound: false,
                        isDiscord: false,
                        adminOnly: true,
                        noReceivers: nonAfkAdmins.Count == 0,
                        icon: ":bank:");
                    _messageQueues[netId].Enqueue(GenerateAHelpMessage(discordParams));
                }
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Failed to emit banking audit bwoink: {ex}");
            }
        }

        private void OnRequestUnstickPlayerShip(RequestUnstickPlayerShipMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new UnstickPlayerShipResponseMessage(message.OwnerUserId, false, "not-authorized"), session.Channel);
                return;
            }

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey))
            {
                RaiseNetworkEvent(new UnstickPlayerShipResponseMessage(message.OwnerUserId ?? string.Empty, false, "empty-owner"), session.Channel);
                return;
            }

            // Find the first ship grid owned by this player.
            EntityUid? targetUid = null;
            ShuttleDeedComponent? targetDeed = null;
            ShuttleComponent? targetShuttle = null;
            var query = EntityQueryEnumerator<ShuttleDeedComponent, ShuttleComponent>();
            while (query.MoveNext(out var uid, out var deed, out var shuttle))
            {
                if (!string.Equals(deed.OwnerUserId, ownerKey, StringComparison.Ordinal))
                    continue;
                targetUid = uid;
                targetDeed = deed;
                targetShuttle = shuttle;
                break;
            }

            if (targetUid is not { } shipUid || targetShuttle == null || targetDeed == null)
            {
                RaiseNetworkEvent(new UnstickPlayerShipResponseMessage(ownerKey, false, "no-ship"), session.Channel);
                return;
            }

            // Already FTLing — refuse rather than stomping the in-flight transition.
            if (TryComp<FTLComponent>(shipUid, out var ftl) && ftl.State != FTLState.Available)
            {
                RaiseNetworkEvent(new UnstickPlayerShipResponseMessage(ownerKey, false, "in-ftl"), session.Channel);
                return;
            }

            if (!TryComp<TransformComponent>(shipUid, out var xform) || xform.MapUid is not { } mapUid)
            {
                RaiseNetworkEvent(new UnstickPlayerShipResponseMessage(ownerKey, false, "no-position"), session.Channel);
                return;
            }

            var transformSystem = EntityManager.System<SharedTransformSystem>();
            var origin = transformSystem.GetWorldPosition(xform);
            var mapCoords = new MapCoordinates(origin, xform.MapID);

            // Try random offsets around the ship — admin override ignores FTL range
            // restrictions so this works even on unpowered ships. We do our own
            // grid-clearance check (same logic as FTLFree) without the range gate.
            const int Attempts = 80;
            const float AdminMaxDist = 1000f;  // search up to 1 km
            var ftlBuffer = _shuttle.GetFTLBufferRange(shipUid);
            var minDist = ftlBuffer + 20f;     // just past the ship's own footprint
            Vector2? chosen = null;
            for (var i = 0; i < Attempts; i++)
            {
                var angle = _random.NextFloat(0f, MathF.Tau);
                var dist = minDist + (AdminMaxDist - minDist) * (i / (float)Attempts);
                var candidate = origin + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist;
                if (IsAdminFTLPositionClear(shipUid, xform.MapID, candidate, ftlBuffer))
                {
                    chosen = candidate;
                    break;
                }
            }

            if (chosen is not { } target)
            {
                RaiseNetworkEvent(new UnstickPlayerShipResponseMessage(ownerKey, false, "no-clear-spot"), session.Channel);
                return;
            }

            var shipName = string.IsNullOrWhiteSpace(targetDeed.ShuttleName)
                ? "Unknown"
                : targetDeed.ShuttleName!;

            try
            {
                _shuttle.FTLToCoordinates(shipUid, targetShuttle, new EntityCoordinates(mapUid, target), Angle.Zero);
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Unstick FTL failed for {shipName} (admin {session.Name}): {ex}");
                RaiseNetworkEvent(new UnstickPlayerShipResponseMessage(ownerKey, false, "ftl-failed", shipName), session.Channel);
                return;
            }

            _sawmill.Info($"Admin {session.Name} ({session.UserId}) unstuck {shipName} ({GetNetEntity(shipUid)}) to {target} for owner {ownerKey}");
            RaiseNetworkEvent(new UnstickPlayerShipResponseMessage(ownerKey, true, null, shipName, target), session.Channel);

            // Emit an admin-only audit ahelp into the affected player's channel,
            // mirroring the banking-audit pattern (pre-styled markup, plain Discord).
            if (TryParseUserId(ownerKey, out var ownerGuid))
            {
                try
                {
                    var ownerNetId = new NetUserId(ownerGuid);
                    var auditBody = Loc.GetString("bwoink-unstick-audit",
                        ("ship", shipName),
                        ("x", (int)target.X),
                        ("y", (int)target.Y));

                    var senderAdmin = _adminManager.GetAdminData(session);
                    var senderColor = senderAdmin is not null && senderAdmin.Flags == AdminFlags.Adminhelp
                        ? "purple"
                        : "red";
                    var adminPrefix = "";
                    if (_config.GetCVar(CCVars.AhelpAdminPrefix) && senderAdmin?.Title is not null)
                        adminPrefix = $"[bold]\\[{senderAdmin.Title}\\][/bold] ";

                    var styledText =
                        $"{Loc.GetString("bwoink-message-admin-only")} " +
                        $"[color=#5cc8ff][bold]\\[\U0001F6F8 Unstick\\][/bold][/color] " +
                        $"[color={senderColor}]{adminPrefix}{session.Name}[/color]: {auditBody}";

                    _activeConversations[ownerNetId] = DateTime.Now;
                    var auditMsg = new BwoinkTextMessage(ownerNetId, session.UserId, styledText, playSound: false, adminOnly: true);
                    LogBwoink(auditMsg);

                    foreach (var adminChannel in GetTargetAdmins())
                        RaiseNetworkEvent(auditMsg, adminChannel);

                    if (_webhookUrl != string.Empty)
                    {
                        if (!_messageQueues.ContainsKey(ownerNetId))
                            _messageQueues[ownerNetId] = new Queue<DiscordRelayedData>();
                        var nonAfkAdmins = GetNonAfkAdmins();
                        var discordParams = new AHelpMessageParams(
                            session.Name,
                            auditBody,
                            isAdmin: true,
                            _gameTicker.RoundDuration().ToString("hh\\:mm\\:ss"),
                            _gameTicker.RunLevel,
                            playedSound: false,
                            isDiscord: false,
                            adminOnly: true,
                            noReceivers: nonAfkAdmins.Count == 0,
                            icon: ":rocket:");
                        _messageQueues[ownerNetId].Enqueue(GenerateAHelpMessage(discordParams));
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Warning($"Failed to emit unstick audit bwoink: {ex}");
                }
            }
        }

        private void OnRequestUnstickPlayerShipPreview(RequestUnstickPlayerShipPreviewMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new UnstickPlayerShipPreviewResponseMessage(message.OwnerUserId, false, "not-authorized"), session.Channel);
                return;
            }

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey))
            {
                RaiseNetworkEvent(new UnstickPlayerShipPreviewResponseMessage(message.OwnerUserId ?? string.Empty, false, "empty-owner"), session.Channel);
                return;
            }

            ShuttleDeedComponent? targetDeed = null;
            EntityUid? shipUid = null;
            var query = EntityQueryEnumerator<ShuttleDeedComponent, ShuttleComponent>();
            while (query.MoveNext(out var uid, out var deed, out _))
            {
                if (!string.Equals(deed.OwnerUserId, ownerKey, StringComparison.Ordinal))
                    continue;

                shipUid = uid;
                targetDeed = deed;
                break;
            }

            if (shipUid == null || targetDeed == null)
            {
                RaiseNetworkEvent(new UnstickPlayerShipPreviewResponseMessage(ownerKey, false, "no-ship"), session.Channel);
                return;
            }

            var shipName = string.IsNullOrWhiteSpace(targetDeed.ShuttleName)
                ? "Unknown"
                : targetDeed.ShuttleName!;

            RaiseNetworkEvent(new UnstickPlayerShipPreviewResponseMessage(ownerKey, true, null, shipName), session.Channel);
        }

        private void OnRequestSaveShipPreview(RequestSaveShipPreviewMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new SaveShipPreviewResponseMessage(message.OwnerUserId, false, "not-authorized"), session.Channel);
                return;
            }

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey))
            {
                RaiseNetworkEvent(new SaveShipPreviewResponseMessage(message.OwnerUserId ?? string.Empty, false, "empty-owner"), session.Channel);
                return;
            }

            if (!TryFindOwnerShip(ownerKey, out _, out _, out var shipName))
            {
                RaiseNetworkEvent(new SaveShipPreviewResponseMessage(ownerKey, false, "no-ship"), session.Channel);
                return;
            }

            RaiseNetworkEvent(new SaveShipPreviewResponseMessage(ownerKey, true, null, shipName), session.Channel);
        }

        private void OnRequestSaveShip(RequestSaveShipMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new SaveShipResponseMessage(message.OwnerUserId, false, "not-authorized"), session.Channel);
                return;
            }

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey) || !TryParseUserId(ownerKey, out var ownerGuid))
            {
                RaiseNetworkEvent(new SaveShipResponseMessage(message.OwnerUserId ?? string.Empty, false, "invalid-owner"), session.Channel);
                return;
            }

            if (!TryFindOwnerShip(ownerKey, out var shipUid, out var deedUid, out var shipName))
            {
                RaiseNetworkEvent(new SaveShipResponseMessage(ownerKey, false, "no-ship"), session.Channel);
                return;
            }

            var ownerNetId = new NetUserId(ownerGuid);
            if (!_playerManager.TryGetSessionById(ownerNetId, out var ownerSession))
            {
                RaiseNetworkEvent(new SaveShipResponseMessage(ownerKey, false, "owner-offline", shipName), session.Channel);
                return;
            }

            // 1) Evict everyone aboard the ship to a medbay rescue beacon.
            EntityCoordinates? evictTarget = null;
            string? evictDestName = null;

            // Prefer the rescue beacon on the same station the ship is currently docked to (if any).
            var shipStation = _station.GetOwningStation(shipUid);
            if (shipStation is { } dockedStation && TryGetStationMedicalFultonBeaconCoordinates(dockedStation, out var dockedBeacon))
            {
                evictTarget = dockedBeacon;
                evictDestName = "Medbay Rescue Beacon";
            }
            else if (TryGetAnyMedicalFultonBeaconCoordinates(out var anyBeacon))
            {
                evictTarget = anyBeacon;
                evictDestName = "Medbay Rescue Beacon";
            }

            var transformSystem = EntityManager.System<SharedTransformSystem>();
            var evicted = 0;

            if (evictTarget is { } target)
            {
                foreach (var playerSession in _playerManager.Sessions)
                {
                    if (playerSession.AttachedEntity is not { } attached)
                        continue;

                    if (!TryComp<TransformComponent>(attached, out var attachedXform))
                        continue;

                    if (attachedXform.GridUid != shipUid)
                        continue;

                    try
                    {
                        transformSystem.SetCoordinates(attached, target);
                        transformSystem.AttachToGridOrMap(attached);
                        evicted++;
                    }
                    catch (Exception ex)
                    {
                        _sawmill.Warning($"Save Ship: failed to evict {playerSession.Name} from {shipName}: {ex}");
                    }
                }
            }
            else
            {
                _sawmill.Warning($"Save Ship: no medbay rescue beacon found, skipping eviction for {shipName}.");
            }

            // 2) Force a save to the owner's client.
            try
            {
                _shipSave.RequestSaveShip(deedUid, ownerSession);
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Save Ship: RequestSaveShip threw for {shipName} owner {ownerKey}: {ex}");
                RaiseNetworkEvent(new SaveShipResponseMessage(ownerKey, false, "save-failed", shipName, evicted), session.Channel);
                return;
            }

            _sawmill.Info(
                $"Admin {session.Name} ({session.UserId}) saved ship {shipName} ({GetNetEntity(shipUid)}) for owner {ownerKey}; evicted {evicted} player(s) to {evictDestName ?? "(no beacon)"}.");

            RaiseNetworkEvent(new SaveShipResponseMessage(ownerKey, true, null, shipName, evicted), session.Channel);
        }

        /// <summary>
        ///     Admin-only FTL position check: verifies no other grid overlaps the
        ///     candidate point (same grid-clearance logic as FTLFree) without
        ///     enforcing the ship's powered FTL range limit.
        /// </summary>
        private bool IsAdminFTLPositionClear(EntityUid shuttleUid, MapId mapId, Vector2 position, float ftlBuffer)
        {
            var mapUid = _mapSystem.GetMap(mapId);
            if (!mapUid.IsValid())
                return false;

            var grids = new List<Entity<MapGridComponent>>();
            var circle = new PhysShapeCircle(ftlBuffer + SharedShuttleSystem.FTLBufferRange, position);
            _mapManager.FindGridsIntersecting(mapId, circle, Robust.Shared.Physics.Transform.Empty,
                ref grids, includeMap: false);

            foreach (var grid in grids)
            {
                if (grid.Owner == shuttleUid)
                    continue;
                return false;
            }

            return true;
        }

        private bool TryFindOwnerShip(string ownerKey, out EntityUid shipUid, out EntityUid deedUid, out string shipName)
        {
            shipUid = default;
            deedUid = default;
            shipName = "Unknown";

            // Mirrors the Unstick path: the deed and the shuttle grid live on the same entity here.
            var query = EntityQueryEnumerator<ShuttleDeedComponent, ShuttleComponent>();
            while (query.MoveNext(out var uid, out var deed, out _))
            {
                if (!string.Equals(deed.OwnerUserId, ownerKey, StringComparison.Ordinal))
                    continue;

                shipUid = uid;
                deedUid = uid;
                shipName = string.IsNullOrWhiteSpace(deed.ShuttleName) ? "Unknown" : deed.ShuttleName!;
                return true;
            }

            return false;
        }

        private bool TryGetAnyMedicalFultonBeaconCoordinates(out EntityCoordinates coords)
        {
            coords = EntityCoordinates.Invalid;
            var possiblePositions = new List<EntityCoordinates>();

            var beacons = EntityQueryEnumerator<RescueBeaconComponent, TransformComponent>();
            while (beacons.MoveNext(out _, out _, out var xform))
            {
                possiblePositions.Add(xform.Coordinates);
            }

            if (possiblePositions.Count == 0)
                return false;

            coords = possiblePositions[_random.Next(possiblePositions.Count)];
            return true;
        }

        private void OnRequestTeleportPlayerToStation(RequestTeleportPlayerToStationMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new TeleportPlayerToStationResponseMessage(message.OwnerUserId, false, "not-authorized"), session.Channel);
                return;
            }

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey) || !TryParseUserId(ownerKey, out var ownerGuid))
            {
                RaiseNetworkEvent(new TeleportPlayerToStationResponseMessage(message.OwnerUserId ?? string.Empty, false, "invalid-owner"), session.Channel);
                return;
            }

            var netId = new NetUserId(ownerGuid);
            if (!_playerManager.TryGetSessionById(netId, out var targetSession))
            {
                RaiseNetworkEvent(new TeleportPlayerToStationResponseMessage(ownerKey, false, "offline"), session.Channel);
                return;
            }

            if (!targetSession.AttachedEntity.HasValue)
            {
                RaiseNetworkEvent(new TeleportPlayerToStationResponseMessage(ownerKey, false, "no-attached-entity"), session.Channel);
                return;
            }

            var attached = targetSession.AttachedEntity.Value;
            if (!TryGetMainStation(out var station))
            {
                RaiseNetworkEvent(new TeleportPlayerToStationResponseMessage(ownerKey, false, "no-station"), session.Channel);
                return;
            }

            EntityCoordinates target;
            var destinationName = "Station";

            if (TryGetStationMedicalFultonBeaconCoordinates(station, out var medicalBeacon))
            {
                target = medicalBeacon;
                destinationName = "Medbay Rescue Beacon";
            }
            else if (TryGetStationPassengerSpawnCoordinates(station, out var passengerSpawn))
            {
                target = passengerSpawn;
                destinationName = "Passenger Spawn";
            }
            else if (TryGetStationLateJoinSpawnCoordinates(station, out var lateJoinSpawn))
            {
                target = lateJoinSpawn;
                destinationName = "Station Arrivals";
            }
            else if (TryFindStationFallbackPosition(station, out var stationFallback))
            {
                target = stationFallback;
                destinationName = "Station Safe Tile";
            }
            else
            {
                RaiseNetworkEvent(new TeleportPlayerToStationResponseMessage(ownerKey, false, "no-arrivals"), session.Channel);
                return;
            }

            var transformSystem = EntityManager.System<SharedTransformSystem>();
            transformSystem.SetCoordinates(attached, target);
            transformSystem.AttachToGridOrMap(attached);

            RaiseNetworkEvent(new TeleportPlayerToStationResponseMessage(ownerKey, true, null, destinationName, target.Position), session.Channel);
        }

        private void OnRequestTeleportPlayerToShip(RequestTeleportPlayerToShipMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new TeleportPlayerToShipResponseMessage(message.OwnerUserId ?? string.Empty, false, "not-authorized"), session.Channel);
                return;
            }

            var ownerKey = message.OwnerUserId?.Trim();
            if (string.IsNullOrEmpty(ownerKey) || !TryParseUserId(ownerKey, out var ownerGuid))
            {
                RaiseNetworkEvent(new TeleportPlayerToShipResponseMessage(message.OwnerUserId ?? string.Empty, false, "invalid-owner"), session.Channel);
                return;
            }

            var netId = new NetUserId(ownerGuid);
            if (!_playerManager.TryGetSessionById(netId, out var targetSession))
            {
                RaiseNetworkEvent(new TeleportPlayerToShipResponseMessage(ownerKey, false, "offline"), session.Channel);
                return;
            }

            if (!targetSession.AttachedEntity.HasValue)
            {
                RaiseNetworkEvent(new TeleportPlayerToShipResponseMessage(ownerKey, false, "no-attached-entity"), session.Channel);
                return;
            }

            if (!TryFindOwnerShip(ownerKey, out var shipUid, out _, out var shipName))
            {
                RaiseNetworkEvent(new TeleportPlayerToShipResponseMessage(ownerKey, false, "no-ship"), session.Channel);
                return;
            }

            // Refuse if the ship is actively FTL-ing to avoid placing the player in limbo.
            if (TryComp<FTLComponent>(shipUid, out var ftl) && ftl.State != FTLState.Available)
            {
                RaiseNetworkEvent(new TeleportPlayerToShipResponseMessage(ownerKey, false, "in-ftl", shipName), session.Channel);
                return;
            }

            if (!TryFindGridFallbackPosition(shipUid, out var target))
            {
                RaiseNetworkEvent(new TeleportPlayerToShipResponseMessage(ownerKey, false, "no-safe-tile", shipName), session.Channel);
                return;
            }

            var attached = targetSession.AttachedEntity.Value;
            var transformSystem = EntityManager.System<SharedTransformSystem>();
            transformSystem.SetCoordinates(attached, target);
            transformSystem.AttachToGridOrMap(attached);

            _sawmill.Info($"Admin {session.Name} ({session.UserId}) teleported {ownerKey} to their ship {shipName} ({GetNetEntity(shipUid)}) at {target.Position}.");
            RaiseNetworkEvent(new TeleportPlayerToShipResponseMessage(ownerKey, true, null, shipName, target.Position), session.Channel);
        }

        private void OnRequestShipDeedList(RequestShipDeedListMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
                return;

            var targetKey = message.TargetUserId?.Trim();
            if (string.IsNullOrEmpty(targetKey))
                return;

            var ships = new List<ShipDeedEntry>();
            var query = EntityQueryEnumerator<ShuttleDeedComponent, ShuttleComponent>();
            while (query.MoveNext(out var shipUid, out var deed, out _))
            {
                var name = string.IsNullOrWhiteSpace(deed.ShuttleName) ? "Unknown" : deed.ShuttleName!;
                if (!string.IsNullOrWhiteSpace(deed.ShuttleNameSuffix))
                    name = $"{name}-{deed.ShuttleNameSuffix}";

                ships.Add(new ShipDeedEntry(
                    GetNetEntity(shipUid),
                    name,
                    deed.ShuttleOwner,
                    deed.OwnerUserId));
            }

            ships.Sort((a, b) => string.Compare(a.ShipName, b.ShipName, StringComparison.OrdinalIgnoreCase));
            RaiseNetworkEvent(new ShipDeedListResponseMessage(targetKey, ships.ToArray()), session.Channel);
        }

        private void OnRequestAssignShipDeed(RequestAssignShipDeedMessage message, EntitySessionEventArgs args)
        {
            var session = args.SenderSession;
            if (session == null)
                return;

            if (!_adminManager.HasAdminFlag(session, AdminFlags.Adminhelp))
            {
                RaiseNetworkEvent(new AssignShipDeedResponseMessage(message.TargetUserId ?? string.Empty, false, "not-authorized"), session.Channel);
                return;
            }

            var targetKey = message.TargetUserId?.Trim();
            if (string.IsNullOrEmpty(targetKey) || !TryParseUserId(targetKey, out var targetGuid))
            {
                RaiseNetworkEvent(new AssignShipDeedResponseMessage(message.TargetUserId ?? string.Empty, false, "invalid-owner"), session.Channel);
                return;
            }

            var targetNetId = new NetUserId(targetGuid);
            if (!_playerManager.TryGetSessionById(targetNetId, out var targetSession))
            {
                RaiseNetworkEvent(new AssignShipDeedResponseMessage(targetKey, false, "offline"), session.Channel);
                return;
            }

            if (!targetSession.AttachedEntity.HasValue)
            {
                RaiseNetworkEvent(new AssignShipDeedResponseMessage(targetKey, false, "no-attached-entity"), session.Channel);
                return;
            }

            // Find the ship entity from the net entity the client selected.
            var shipUid = GetEntity(message.ShipNetEntity);
            if (!TryComp<ShuttleDeedComponent>(shipUid, out var shipDeed) || !TryComp<ShuttleComponent>(shipUid, out _))
            {
                RaiseNetworkEvent(new AssignShipDeedResponseMessage(targetKey, false, "no-ship"), session.Channel);
                return;
            }

            var shipName = string.IsNullOrWhiteSpace(shipDeed.ShuttleName) ? "Unknown" : shipDeed.ShuttleName!;
            if (!string.IsNullOrWhiteSpace(shipDeed.ShuttleNameSuffix))
                shipName = $"{shipName}-{shipDeed.ShuttleNameSuffix}";

            // Locate the target player's ID card — checks hands, inventory slot, and PDA.
            if (!_idCard.TryFindIdCard(targetSession.AttachedEntity.Value, out var idCard))
            {
                RaiseNetworkEvent(new AssignShipDeedResponseMessage(targetKey, false, "no-id-card"), session.Channel);
                return;
            }

            // Write the deed onto the ID card, mirroring how the shipyard grants deeds.
            var cardDeed = EnsureComp<ShuttleDeedComponent>(idCard.Owner);
            cardDeed.ShuttleUid = GetNetEntity(shipUid);
            cardDeed.ShuttleName = shipDeed.ShuttleName;
            cardDeed.ShuttleNameSuffix = shipDeed.ShuttleNameSuffix;
            cardDeed.ShuttleOwner = targetSession.Name;
            cardDeed.OwnerUserId = targetKey;
            Dirty(idCard.Owner, cardDeed);

            // Update the ship's own deed to reflect the new owner.
            shipDeed.ShuttleOwner = targetSession.Name;
            shipDeed.OwnerUserId = targetKey;
            Dirty(shipUid, shipDeed);

            _sawmill.Info($"Admin {session.Name} ({session.UserId}) assigned deed for ship {shipName} ({GetNetEntity(shipUid)}) to player {targetKey} ({targetSession.Name}).");
            RaiseNetworkEvent(new AssignShipDeedResponseMessage(targetKey, true, null, shipName), session.Channel);
        }

        private bool TryGetMainStation(out EntityUid station)
        {
            station = _station.GetStations().FirstOrDefault();
            return station != EntityUid.Invalid;
        }

        private bool TryGetStationMedicalFultonBeaconCoordinates(EntityUid station, out EntityCoordinates coords)
        {
            coords = EntityCoordinates.Invalid;
            var possiblePositions = new List<EntityCoordinates>();

            var beacons = EntityQueryEnumerator<RescueBeaconComponent, TransformComponent>();
            while (beacons.MoveNext(out var uid, out _, out var xform))
            {
                if (_station.GetOwningStation(uid, xform) != station)
                    continue;

                possiblePositions.Add(xform.Coordinates);
            }

            if (possiblePositions.Count == 0)
                return false;

            coords = possiblePositions[_random.Next(possiblePositions.Count)];
            return true;
        }

        private bool TryGetStationPassengerSpawnCoordinates(EntityUid station, out EntityCoordinates coords)
        {
            coords = EntityCoordinates.Invalid;
            var possiblePositions = new List<EntityCoordinates>();

            var points = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
            while (points.MoveNext(out var uid, out var spawnPoint, out var xform))
            {
                if (spawnPoint.SpawnType != SpawnPointType.Job || spawnPoint.Job != "Passenger")
                    continue;

                if (_station.GetOwningStation(uid, xform) != station)
                    continue;

                possiblePositions.Add(xform.Coordinates);
            }

            if (possiblePositions.Count == 0)
                return false;

            coords = possiblePositions[_random.Next(possiblePositions.Count)];
            return true;
        }

        private bool TryGetStationLateJoinSpawnCoordinates(EntityUid station, out EntityCoordinates coords)
        {
            coords = EntityCoordinates.Invalid;
            var possiblePositions = new List<EntityCoordinates>();

            var points = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
            while (points.MoveNext(out var uid, out var spawnPoint, out var xform))
            {
                if (spawnPoint.SpawnType != SpawnPointType.LateJoin)
                    continue;

                if (_station.GetOwningStation(uid, xform) != station)
                    continue;

                possiblePositions.Add(xform.Coordinates);
            }

            if (possiblePositions.Count == 0)
                return false;

            coords = possiblePositions[_random.Next(possiblePositions.Count)];
            return true;
        }

        private bool TryFindStationFallbackPosition(EntityUid station, out EntityCoordinates coords)
        {
            coords = EntityCoordinates.Invalid;

            if (!TryComp<StationDataComponent>(station, out var stationData))
                return false;

            var largestGrid = _station.GetLargestGrid(stationData);
            if (largestGrid is { } gridUid && TryFindGridFallbackPosition(gridUid, out coords))
                return true;

            foreach (var memberGrid in stationData.Grids)
            {
                if (memberGrid == largestGrid)
                    continue;

                if (TryFindGridFallbackPosition(memberGrid, out coords))
                    return true;
            }

            return false;
        }

        private bool TryFindGridFallbackPosition(EntityUid gridUid, out EntityCoordinates coords)
        {
            coords = EntityCoordinates.Invalid;

            if (!TryComp<MapGridComponent>(gridUid, out var grid))
                return false;

            var candidateTiles = new List<TileRef>();
            foreach (var tile in _mapSystem.GetAllTiles(gridUid, grid))
            {
                if (tile.Tile.IsEmpty || _turf.IsTileBlocked(tile, CollisionGroup.MobMask))
                    continue;

                candidateTiles.Add(tile);
            }

            if (candidateTiles.Count == 0)
                return false;

            coords = _turf.GetTileCenter(candidateTiles[_random.Next(candidateTiles.Count)]);
            return true;
        }

        private void OnAddOrRestoreAhelpCategory(AddOrRestoreAhelpCategoryMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            if (!TryAddOrRestoreAhelpCategory(message.Category, message.Template, message.Keywords))
                return;

            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnRemoveAhelpCategory(RemoveAhelpCategoryMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            if (!TryRemoveAhelpCategory(message.Category))
                return;

            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnSetAhelpAutoReplyTemplate(SetAhelpAutoReplyTemplateMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            if (!TrySetAutoReplyTemplate(message.Category, message.Template))
                return;

            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnResetAhelpAutoReplyTemplate(ResetAhelpAutoReplyTemplateMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            if (!TrySetAutoReplyTemplate(message.Category, null))
                return;

            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnSetAhelpTriageKeywords(SetAhelpTriageKeywordsMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            if (!TrySetTriageRuleKeywords(message.Category, message.Keywords))
                return;

            BroadcastAhelpAdminStateToAdmins();
        }

        private void OnResetAhelpTriageKeywords(ResetAhelpTriageKeywordsMessage message, EntitySessionEventArgs args)
        {
            if (!CanManageAhelpConfig(args.SenderSession))
                return;

            if (!TrySetTriageRuleKeywords(message.Category, null))
                return;

            BroadcastAhelpAdminStateToAdmins();
        }

        private DiscordRelayedData GenerateAHelpMessage(AHelpMessageParams parameters)
        {
            var stringbuilder = new StringBuilder();

            if (parameters.Icon != null)
                stringbuilder.Append(parameters.Icon);
            else if (parameters.IsAdmin)
                stringbuilder.Append(":outbox_tray:");
            else if (parameters.NoReceivers)
                stringbuilder.Append(":sos:");
            else
                stringbuilder.Append(":inbox_tray:");

            if (parameters.RoundTime != string.Empty && parameters.RoundState == GameRunLevel.InRound)
                stringbuilder.Append($" **{parameters.RoundTime}**");
            if (!parameters.PlayedSound)
                stringbuilder.Append($" **{(parameters.AdminOnly ? Loc.GetString("bwoink-message-admin-only") : Loc.GetString("bwoink-message-silent"))}**");
            if (parameters.IsDiscord) // Frontier - Discord Indicator
                stringbuilder.Append($" **{Loc.GetString("bwoink-message-discord")}**"); // Frontier - Discord Indicator
            if (parameters.Icon == null)
                stringbuilder.Append($" **{parameters.Username}:** ");
            else
                stringbuilder.Append($" **{parameters.Username}** ");

            if (!string.IsNullOrWhiteSpace(parameters.TriageCategory))
                stringbuilder.Append($"**[TRIAGE: {parameters.TriageCategory}]** ");

            stringbuilder.Append(parameters.Message);

            return new DiscordRelayedData()
            {
                Receivers = !parameters.NoReceivers,
                Message = stringbuilder.ToString(),
            };
        }

        private record struct DiscordRelayedData
        {
            /// <summary>
            /// Was anyone online to receive it.
            /// </summary>
            public bool Receivers;

            /// <summary>
            /// What's the payload to send to discord.
            /// </summary>
            public string Message;
        }

        /// <summary>
        ///  Class specifically for holding information regarding existing Discord embeds
        /// </summary>
        private sealed class DiscordRelayInteraction
        {
            public string? Id;

            public string Username = String.Empty;

            public string? CharacterName;

            /// <summary>
            /// Contents for the discord message.
            /// </summary>
            public string Description = string.Empty;

            /// <summary>
            /// Run level of the last interaction. If different we'll link to the last Id.
            /// </summary>
            public GameRunLevel LastRunLevel;

            /// <summary>
            /// Did we relay this interaction to OnCall previously.
            /// </summary>
            public bool OnCall;
        }
    }

    public sealed class AHelpMessageParams
    {
        public string Username { get; set; }
        public string Message { get; set; }
        public bool IsAdmin { get; set; }
        public string RoundTime { get; set; }
        public GameRunLevel RoundState { get; set; }
        public bool PlayedSound { get; set; }
        public readonly bool AdminOnly;
        public bool NoReceivers { get; set; }
        public bool IsDiscord { get; set; } // Frontier
        public string? Icon { get; set; }
        public string? TriageCategory { get; set; }

        public AHelpMessageParams(
            string username,
            string message,
            bool isAdmin,
            string roundTime,
            GameRunLevel roundState,
            bool playedSound,
            bool isDiscord = false, // Frontier
            bool adminOnly = false,
            bool noReceivers = false,
            string? icon = null,
            string? triageCategory = null)
        {
            Username = username;
            Message = message;
            IsAdmin = isAdmin;
            RoundTime = roundTime;
            RoundState = roundState;
            IsDiscord = isDiscord; // Frontier
            PlayedSound = playedSound;
            AdminOnly = adminOnly;
            NoReceivers = noReceivers;
            Icon = icon;
            TriageCategory = triageCategory;
        }
    }

    public enum PlayerStatusType
    {
        Connected,
        Disconnected,
        Banned,
    }
}
