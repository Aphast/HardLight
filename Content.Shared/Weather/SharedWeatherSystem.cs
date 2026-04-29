using System.Diagnostics.CodeAnalysis;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Maps;
using Content.Shared.StatusEffectNew;
using Content.Shared.StatusEffectNew.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared.Weather;

public abstract class SharedWeatherSystem : EntitySystem
{
    [Dependency] protected readonly IGameTiming Timing = default!;
    [Dependency] protected readonly IPrototypeManager ProtoMan = default!;
    [Dependency] protected readonly SharedAudioSystem Audio = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedRoofSystem _roof = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;

    private EntityQuery<BlockWeatherComponent> _blockQuery;
    private EntityQuery<WeatherStatusEffectComponent> _weatherQuery;

    public static readonly TimeSpan StartupTime = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ShutdownTime = TimeSpan.FromSeconds(15);

    public override void Initialize()
    {
        base.Initialize();

        _blockQuery = GetEntityQuery<BlockWeatherComponent>();
        _weatherQuery = GetEntityQuery<WeatherStatusEffectComponent>();
    }

    public bool CanWeatherAffect(Entity<MapGridComponent?, RoofComponent?> ent, TileRef tileRef)
    {
        if (tileRef.Tile.IsEmpty)
            return true;

        if (!Resolve(ent, ref ent.Comp1))
            return false;

        if (Resolve(ent, ref ent.Comp2, false) && _roof.IsRooved((ent, ent.Comp1, ent.Comp2), tileRef.GridIndices))
            return false;

        var tileDef = (ContentTileDefinition)_tileDefManager[tileRef.Tile.TypeId];

        if (!tileDef.Weather)
            return false;

        var anchoredEntities = _mapSystem.GetAnchoredEntitiesEnumerator(ent, ent.Comp1, tileRef.GridIndices);

        while (anchoredEntities.MoveNext(out var anchored))
        {
            if (_blockQuery.HasComponent(anchored.Value))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Calculates the current “strength” of the specified weather based on the duration of the status effect.
    /// Between 0 and 1.
    /// </summary>
    public float GetWeatherPercent(Entity<StatusEffectComponent> ent)
    {
        var elapsed = Timing.CurTime - ent.Comp.StartEffectTime;
        var duration = ent.Comp.Duration;
        var remaining = duration - elapsed;

        if (remaining < ShutdownTime)
            return (float)(remaining / ShutdownTime);
        else if (elapsed < StartupTime)
            return (float)(elapsed / StartupTime);
        else
            return 1f;
    }

    public bool TryAddWeather(MapId mapId, EntProtoId weatherProto, [NotNullWhen(true)] out EntityUid? weatherEnt, TimeSpan? duration = null)
    {
        weatherEnt = null;

        if (!_mapSystem.TryGetMap(mapId, out var mapUid))
            return false;

        return TryAddWeather(mapUid.Value, weatherProto, out weatherEnt, duration);
    }

    public bool TryAddWeather(EntityUid mapUid, EntProtoId weatherProto, [NotNullWhen(true)] out EntityUid? weatherEnt, TimeSpan? duration = null)
    {
        return _statusEffects.TrySetStatusEffectDuration(mapUid, weatherProto, out weatherEnt, duration);
    }

    public bool HasWeather(MapId mapId, EntProtoId weatherProto)
    {
        if (!_mapSystem.TryGetMap(mapId, out var mapUid))
            return false;

        return _statusEffects.TryGetStatusEffect(mapUid.Value, weatherProto, out _);
    }

    public bool TryRemoveWeather(MapId mapId, EntProtoId weatherProto)
    {
        if (!_mapSystem.TryGetMap(mapId, out var mapUid))
            return false;

        return TryRemoveWeather(mapUid.Value, weatherProto);
    }

    public bool TryRemoveWeather(EntityUid mapUid, EntProtoId weatherProto)
    {
        if (!_statusEffects.TryGetStatusEffect(mapUid, weatherProto, out var weatherEnt))
            return false;

        if (!_weatherQuery.HasComp(weatherEnt))
            return false;

        return _statusEffects.TrySetStatusEffectDuration(mapUid, weatherProto, ShutdownTime);
    }

    public bool TrySetWeather(MapId mapId, EntProtoId? weatherProto, out EntityUid? weatherEnt, TimeSpan? duration = null)
    {
        weatherEnt = null;
        if (!_mapSystem.TryGetMap(mapId, out var mapUid))
            return false;

        if (_statusEffects.TryEffectsWithComp<WeatherStatusEffectComponent>(mapUid, out var effects))
        {
            foreach (var effect in effects)
            {
                var effectProto = Prototype(effect);
                if (effectProto is null)
                    continue;

                if (effectProto != weatherProto)
                {
                    TryRemoveWeather(mapUid.Value, effectProto);
                }
                else
                {
                    weatherEnt = effect;
                }
            }
        }

        if (weatherProto is null)
            return true;

        if (weatherEnt != null)
        {
            TryAddWeather(mapUid.Value, weatherProto.Value, out weatherEnt, duration);
            return true;
        }

        return TryAddWeather(mapUid.Value, weatherProto.Value, out weatherEnt, duration);
    }
}

*** Add File: f:\Floofdev\HardLight\Content.Client\Overlays\StencilOverlaySystem.cs
using Content.Client.Parallax;
using Content.Client.Weather;
using Content.Shared.StatusEffectNew;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;

namespace Content.Client.Overlays;

public sealed class StencilOverlaySystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlay = default!;
    [Dependency] private readonly ParallaxSystem _parallax = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly WeatherSystem _weather = default!;
    [Dependency] private readonly StatusEffectsSystem _status = default!;

    public override void Initialize()
    {
        base.Initialize();
        _overlay.AddOverlay(new StencilOverlay(_parallax, _transform, _map, _sprite, _weather, _status));
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlay.RemoveOverlay<StencilOverlay>();
    }
}
*** Add File: f:\Floofdev\HardLight\Content.Client\Weather\WeatherSystem.cs
using System.Numerics;
using Content.Shared.Light.Components;
using Content.Shared.StatusEffectNew.Components;
using Content.Shared.Weather;
using Robust.Client.Audio;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.Client.Weather;

public sealed class WeatherSystem : SharedWeatherSystem
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly MapSystem _mapSystem = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private EntityQuery<AudioComponent> _audioQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<RoofComponent> _roofQuery;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<WeatherStatusEffectComponent, ComponentShutdown>(OnComponentShutdown);

        _audioQuery = GetEntityQuery<AudioComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _roofQuery = GetEntityQuery<RoofComponent>();
    }

    private void OnComponentShutdown(Entity<WeatherStatusEffectComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Stream = _audio.Stop(ent.Comp.Stream);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!Timing.IsFirstTimePredicted)
            return;

        var player = _playerManager.LocalEntity;

        if (player == null)
            return;

        var playerXform = Transform(player.Value);

        var query = EntityQueryEnumerator<WeatherStatusEffectComponent, StatusEffectComponent>();
        while (query.MoveNext(out var uid, out var weather, out var status))
        {
            if (weather.Sound == null || status.AppliedTo != playerXform.MapUid)
            {
                weather.Stream = _audio.Stop(weather.Stream);
                return;
            }

            weather.Stream ??= _audio.PlayGlobal(weather.Sound, Filter.Local(), true)?.Entity;

            if (!_audioQuery.TryComp(weather.Stream, out var audio))
                return;

            var occlusion = 0f;

            if (_gridQuery.TryComp(playerXform.GridUid, out var grid))
            {
                _roofQuery.TryComp(playerXform.GridUid, out var roofComp);
                var gridId = playerXform.GridUid.Value;
                var seed = _mapSystem.GetTileRef(gridId, grid, playerXform.Coordinates);
                var frontier = new Queue<TileRef>();
                frontier.Enqueue(seed);
                EntityCoordinates? nearestNode = null;
                var visited = new HashSet<Vector2i>();

                while (frontier.TryDequeue(out var node))
                {
                    if (!visited.Add(node.GridIndices))
                        continue;

                    if (!CanWeatherAffect((playerXform.GridUid.Value, grid, roofComp), node))
                    {
                        for (var x = -1; x <= 1; x++)
                        {
                            for (var y = -1; y <= 1; y++)
                            {
                                if (Math.Abs(x) == 1 && Math.Abs(y) == 1 ||
                                    x == 0 && y == 0 ||
                                    (new Vector2(x, y) + node.GridIndices - seed.GridIndices).Length() > 3)
                                {
                                    continue;
                                }

                                frontier.Enqueue(_mapSystem.GetTileRef(gridId, grid, new Vector2i(x, y) + node.GridIndices));
                            }
                        }

                        continue;
                    }

                    nearestNode = new EntityCoordinates(playerXform.GridUid.Value,
                        node.GridIndices + grid.TileSizeHalfVector);
                    break;
                }

                if (nearestNode != null)
                {
                    var entPos = _transform.GetMapCoordinates(playerXform);
                    var nodePosition = _transform.ToMapCoordinates(nearestNode.Value).Position;
                    var delta = nodePosition - entPos.Position;
                    var distance = delta.Length();
                    occlusion = _audio.GetOcclusion(entPos, delta, distance);
                }
                else
                {
                    occlusion = 3f;
                }
            }

            var alpha = GetWeatherPercent((uid, status));
            alpha *= SharedAudioSystem.VolumeToGain(weather.Sound.Params.Volume);
            _audio.SetGain(weather.Stream, alpha, audio);
            audio.Occlusion = occlusion;
        }
    }
}
*** Add File: f:\Floofdev\HardLight\Content.Server\Weather\Commands\WeatherAddCommand.cs
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Prototypes;
using Content.Shared.Weather;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.Weather.Commands;

/// <summary>
/// Add specific weather to map.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class WeatherAddCommand : LocalizedEntityCommands
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly WeatherSystem _weather = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;

    public override string Command => "weatheradd";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-no-arguments"));
            return;
        }

        if (!int.TryParse(args[0], out var mapInt))
            return;

        var mapId = new MapId(mapInt);

        if (!_map.MapExists(mapId))
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-wrong-map", ("id", mapId.ToString())));
            return;
        }

        EntProtoId weatherProto = args[1];
        if (!_proto.TryIndex(weatherProto, out _))
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-unknown-proto"));
            return;
        }

        TimeSpan? duration = null;
        if (args.Length == 3)
        {
            if (int.TryParse(args[2], out var durationInt))
                duration = TimeSpan.FromSeconds(durationInt);
            else
                shell.WriteError(Loc.GetString("cmd-weather-error-wrong-time"));
        }

        _weather.TryAddWeather(mapId, weatherProto, out _, duration);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(CompletionHelper.MapIds(EntityManager), Loc.GetString("cmd-weather-hint-map-id"));

        if (args.Length == 2)
        {
            var opts = new List<CompletionOption>();
            foreach (var proto in _proto.EnumeratePrototypes<EntityPrototype>())
            {
                if (!proto.HasComponent<WeatherStatusEffectComponent>(_compFactory))
                    continue;

                opts.Add(new CompletionOption(proto.ID, proto.Name));
            }
            return CompletionResult.FromHintOptions(opts, Loc.GetString("cmd-weather-hint-prototype"));
        }

        if (args.Length == 3)
            return CompletionResult.FromHint(Loc.GetString("cmd-weather-hint-time"));

        return CompletionResult.Empty;
    }
}
*** Add File: f:\Floofdev\HardLight\Content.Server\Weather\Commands\WeatherRemoveCommand.cs
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Prototypes;
using Content.Shared.Weather;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.Weather.Commands;

/// <summary>
/// Remove specific weather from map.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class WeatherRemoveCommand : LocalizedEntityCommands
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly WeatherSystem _weather = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;

    public override string Command => "weatherremove";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-no-arguments"));
            return;
        }

        if (!int.TryParse(args[0], out var mapInt))
            return;

        var mapId = new MapId(mapInt);

        if (!_map.MapExists(mapId))
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-wrong-map", ("id", mapId.ToString())));
            return;
        }

        EntProtoId weatherProto = args[1];
        if (!_proto.TryIndex(weatherProto, out _))
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-unknown-proto"));
            return;
        }

        if (!_weather.HasWeather(mapId, weatherProto))
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-no-weather"));
            return;
        }

        _weather.TryRemoveWeather(mapId, weatherProto);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(CompletionHelper.MapIds(EntityManager), Loc.GetString("cmd-weather-hint-map-id"));

        if (args.Length == 2)
        {
            var opts = new List<CompletionOption>();
            foreach (var proto in _proto.EnumeratePrototypes<EntityPrototype>())
            {
                if (!proto.HasComponent<WeatherStatusEffectComponent>(_compFactory))
                    continue;

                opts.Add(new CompletionOption(proto.ID, proto.Name));
            }
            return CompletionResult.FromHintOptions(opts, Loc.GetString("cmd-weather-hint-prototype"));
        }

        return CompletionResult.Empty;
    }
}
*** Add File: f:\Floofdev\HardLight\Content.Server\Weather\Commands\WeatherSetCommand.cs
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Prototypes;
using Content.Shared.Weather;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.Weather.Commands;

/// <summary>
/// Removes all weather except the specified one. If the specified weather does not exist on the map, it adds it.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class WeatherSetCommand : LocalizedEntityCommands
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly WeatherSystem _weather = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;

    public override string Command => "weatherset";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-no-arguments"));
            return;
        }

        if (!int.TryParse(args[0], out var mapInt))
            return;

        var mapId = new MapId(mapInt);

        if (!_map.MapExists(mapId))
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-wrong-map", ("id", mapId.ToString())));
            return;
        }

        EntProtoId? weatherProto = args[1];
        if (args[1] == "null")
            weatherProto = null;
        else if (!_proto.TryIndex(weatherProto, out _))
        {
            shell.WriteError(Loc.GetString("cmd-weather-error-unknown-proto"));
            return;
        }

        TimeSpan? duration = null;
        if (args.Length == 3)
        {
            if (int.TryParse(args[2], out var durationInt))
                duration = TimeSpan.FromSeconds(durationInt);
            else
                shell.WriteError(Loc.GetString("cmd-weather-error-wrong-time"));
        }

        _weather.TrySetWeather(mapId, weatherProto, out _, duration);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(CompletionHelper.MapIds(EntityManager), Loc.GetString("cmd-weather-hint-map-id"));

        if (args.Length == 2)
        {
            var opts = new List<CompletionOption>();
            foreach (var proto in _proto.EnumeratePrototypes<EntityPrototype>())
            {
                if (!proto.HasComponent<WeatherStatusEffectComponent>(_compFactory))
                    continue;

                opts.Add(new CompletionOption(proto.ID, proto.Name));
            }
            return CompletionResult.FromHintOptions(opts, Loc.GetString("cmd-weather-hint-prototype"));
        }

        if (args.Length == 3)
            return CompletionResult.FromHint(Loc.GetString("cmd-weather-hint-time"));

        return CompletionResult.Empty;
    }
}
*** Add File: f:\Floofdev\HardLight\Content.Shared\StatusEffectNew\Components\StatusEffectComponent.cs
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared.StatusEffectNew.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true), AutoGenerateComponentPause]
public sealed partial class StatusEffectComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntityUid? AppliedTo;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField, AutoNetworkedField]
    public TimeSpan StartEffectTime;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField, AutoNetworkedField]
    public TimeSpan? EndEffectTime;

    [ViewVariables]
    public TimeSpan Duration => EndEffectTime == null ? TimeSpan.MaxValue : EndEffectTime.Value - StartEffectTime;
}
*** Add File: f:\Floofdev\HardLight\Content.Shared\StatusEffectNew\Components\StatusEffectContainerComponent.cs
using Robust.Shared.Containers;

namespace Content.Shared.StatusEffectNew.Components;

[RegisterComponent]
public sealed partial class StatusEffectContainerComponent : Component
{
    public const string ContainerId = "status-effects";

    [ViewVariables]
    public Container? ActiveStatusEffects;
}
*** Add File: f:\Floofdev\HardLight\Content.Shared\StatusEffectNew\StatusEffectsSystem.cs
using System.Diagnostics.CodeAnalysis;
using Content.Shared.StatusEffectNew.Components;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared.StatusEffectNew;

public sealed class StatusEffectsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    private EntityQuery<StatusEffectContainerComponent> _containerQuery;
    private EntityQuery<StatusEffectComponent> _statusQuery;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<StatusEffectContainerComponent, ComponentInit>(OnContainerInit);
        SubscribeLocalEvent<StatusEffectContainerComponent, ComponentShutdown>(OnContainerShutdown);

        _containerQuery = GetEntityQuery<StatusEffectContainerComponent>();
        _statusQuery = GetEntityQuery<StatusEffectComponent>();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<StatusEffectComponent>();
        while (query.MoveNext(out var uid, out var status))
        {
            if (status.EndEffectTime is not { } endTime)
                continue;

            if (_timing.CurTime < endTime)
                continue;

            PredictedQueueDel(uid);
        }
    }

    private void OnContainerInit(Entity<StatusEffectContainerComponent> ent, ref ComponentInit args)
    {
        ent.Comp.ActiveStatusEffects = _container.EnsureContainer<Container>(ent, StatusEffectContainerComponent.ContainerId);
        ent.Comp.ActiveStatusEffects.ShowContents = true;
        ent.Comp.ActiveStatusEffects.OccludesLight = false;
    }

    private void OnContainerShutdown(Entity<StatusEffectContainerComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.ActiveStatusEffects is { } container)
            _container.ShutdownContainer(container);
    }

    public bool TryGetStatusEffect(EntityUid target, EntProtoId effectProto, [NotNullWhen(true)] out EntityUid? statusEffect)
    {
        statusEffect = null;

        if (!_containerQuery.TryComp(target, out var containerComp) || containerComp.ActiveStatusEffects == null)
            return false;

        foreach (var contained in containerComp.ActiveStatusEffects.ContainedEntities)
        {
            if (!_statusQuery.TryComp(contained, out var status) || status.AppliedTo != target)
                continue;

            if (Prototype(contained) != effectProto)
                continue;

            statusEffect = contained;
            return true;
        }

        return false;
    }

    public bool TrySetStatusEffectDuration(EntityUid target, EntProtoId effectProto, TimeSpan? duration)
    {
        return TrySetStatusEffectDuration(target, effectProto, out _, duration);
    }

    public bool TrySetStatusEffectDuration(EntityUid target, EntProtoId effectProto, [NotNullWhen(true)] out EntityUid? statusEffect, TimeSpan? duration = null)
    {
        statusEffect = null;

        if (TryGetStatusEffect(target, effectProto, out statusEffect))
        {
            if (!_statusQuery.TryComp(statusEffect.Value, out var existing))
                return false;

            existing.AppliedTo = target;
            existing.StartEffectTime = _timing.CurTime;
            existing.EndEffectTime = duration == null ? null : _timing.CurTime + duration.Value;
            Dirty(statusEffect.Value, existing);
            return true;
        }

        EnsureComp<StatusEffectContainerComponent>(target);

        if (!PredictedTrySpawnInContainer(effectProto, target, StatusEffectContainerComponent.ContainerId, out var spawned))
            return false;

        if (!_statusQuery.TryComp(spawned, out var status))
            return false;

        status.AppliedTo = target;
        status.StartEffectTime = _timing.CurTime;
        status.EndEffectTime = duration == null ? null : _timing.CurTime + duration.Value;
        Dirty(spawned.Value, status);

        statusEffect = spawned;
        return true;
    }

    public bool TryEffectsWithComp<T>(EntityUid target, [NotNullWhen(true)] out HashSet<Entity<T, StatusEffectComponent>>? effects)
        where T : IComponent
    {
        effects = null;

        if (!_containerQuery.TryComp(target, out var containerComp) || containerComp.ActiveStatusEffects == null)
            return false;

        var set = new HashSet<Entity<T, StatusEffectComponent>>();
        foreach (var contained in containerComp.ActiveStatusEffects.ContainedEntities)
        {
            if (!TryComp<T>(contained, out var comp) || !_statusQuery.TryComp(contained, out var status))
                continue;

            if (status.AppliedTo != target)
                continue;

            set.Add((contained, comp, status));
        }

        if (set.Count == 0)
            return false;

        effects = set;
        return true;
    }
}
*** Add File: f:\Floofdev\HardLight\Content.Shared\Weather\WeatherStatusEffectComponent.cs
using System.Numerics;
using Content.Shared.StatusEffectNew.Components;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Utility;

namespace Content.Shared.Weather;

/// <summary>
/// Used only in conjure with <see cref="StatusEffectComponent"/> for status effects applied to map entities.
/// Contains basic information about all types of weather effects.
/// </summary>
[RegisterComponent, NetworkedComponent, Access(typeof(SharedWeatherSystem))]
public sealed partial class WeatherStatusEffectComponent : Component
{
    [DataField(required: true)]
    public SpriteSpecifier Sprite = default!;

    [DataField]
    public Color? Color;

    [DataField]
    public Vector2? Scrolling;

    [DataField]
    public SoundSpecifier? Sound;

    [ViewVariables]
    public EntityUid? Stream;
}

