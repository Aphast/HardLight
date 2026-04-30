using System.Linq;
using Content.Server.Administration;
using Content.Server.Shuttles.Systems;
using Content.Server.Weather;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Systems;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Debug)]
public sealed class WeatherHereCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public string Command => "weatherhere";
    public string Description => "Sets weather on the map you are currently on. Use 'none' to clear it.";
    public string Help => "weatherhere <weatherPrototype|none>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError("Expected 1 argument: <weatherPrototype|none>");
            return;
        }

        var player = shell.Player;
        if (player == null || player.Status != SessionStatus.InGame || player.AttachedEntity is not { Valid: true } attached)
        {
            shell.WriteError("You must be in-game to use weatherhere.");
            return;
        }

        var mapId = _entManager.GetComponent<TransformComponent>(attached).MapID;
        if (mapId == MapId.Nullspace)
        {
            shell.WriteError("You are not on a valid map.");
            return;
        }

        if (!WeatherCommandHelpers.TryResolveWeather(_proto, args[0], out var weatherId, out var error))
        {
            shell.WriteError(error);
            return;
        }

        var weather = _entManager.System<WeatherSystem>();
        if (!weather.TrySetWeather(mapId, weatherId, out _))
        {
            shell.WriteError($"Failed to set weather on map {mapId}.");
            return;
        }

        shell.WriteLine(weatherId == null
            ? $"Cleared weather on map {mapId}."
            : $"Set weather on map {mapId} to {weatherId}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? CompletionResult.FromHint("weatherPrototype or 'none'")
            : CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class WeatherMapCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IMapManager _maps = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public string Command => "weathermap";
    public string Description => "Sets weather on a specific map ID. Use 'none' to clear it.";
    public string Help => "weathermap <mapId> <weatherPrototype|none>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError("Expected 2 arguments: <mapId> <weatherPrototype|none>");
            return;
        }

        if (!int.TryParse(args[0], out var mapInt))
        {
            shell.WriteError($"Invalid map id '{args[0]}'.");
            return;
        }

        var mapId = new MapId(mapInt);
        if (!_maps.MapExists(mapId))
        {
            shell.WriteError($"Map {mapId} does not exist.");
            return;
        }

        if (!WeatherCommandHelpers.TryResolveWeather(_proto, args[1], out var weatherId, out var error))
        {
            shell.WriteError(error);
            return;
        }

        var weather = _entManager.System<WeatherSystem>();
        if (!weather.TrySetWeather(mapId, weatherId, out _))
        {
            shell.WriteError($"Failed to set weather on map {mapId}.");
            return;
        }

        shell.WriteLine(weatherId == null
            ? $"Cleared weather on map {mapId}."
            : $"Set weather on map {mapId} to {weatherId}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHintOptions(CompletionHelper.MapIds(_entManager), "Map ID"),
            2 => CompletionResult.FromHint("weatherPrototype or 'none'"),
            _ => CompletionResult.Empty,
        };
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class WeatherPlanetCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public string Command => "weatherplanet";
    public string Description => "Sets weather on a persistent sector layer: planet type id, space, ftl, or colcomm.";
    public string Help => "weatherplanet <planetTypeId|space|ftl|colcomm> <weatherPrototype|none>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError("Expected 2 arguments: <planetTypeId|space|ftl|colcomm> <weatherPrototype|none>");
            return;
        }

        var sectorWorld = _entManager.System<SectorWorldSystem>();
        if (!WeatherCommandHelpers.TryResolvePersistentMap(_entManager, sectorWorld, args[0], out var mapUid, out var targetLabel, out var targetError))
        {
            shell.WriteError(targetError);
            return;
        }

        if (!WeatherCommandHelpers.TryResolveWeather(_proto, args[1], out var weatherId, out var error))
        {
            shell.WriteError(error);
            return;
        }

        var weather = _entManager.System<WeatherSystem>();
        if (!_entManager.TryGetComponent<MapComponent>(mapUid, out var mapComp) || !weather.TrySetWeather(mapComp.MapId, weatherId, out _))
        {
            shell.WriteError($"Failed to set weather for target '{targetLabel}'.");
            return;
        }

        shell.WriteLine(weatherId == null
            ? $"Cleared weather on {targetLabel} (map {mapComp.MapId})."
            : $"Set weather on {targetLabel} (map {mapComp.MapId}) to {weatherId}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var sectorWorld = _entManager.System<SectorWorldSystem>();
            var options = WeatherCommandHelpers.GetPersistentMapTargets(_entManager, sectorWorld);
            return CompletionResult.FromOptions(options);
        }

        return args.Length == 2
            ? CompletionResult.FromHint("weatherPrototype or 'none'")
            : CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class ListPlanetMapsCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public string Command => "listplanetmaps";
    public string Description => "Lists persistent sector layer targets and their map IDs.";
    public string Help => "listplanetmaps";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError("This command takes no arguments.");
            return;
        }

        var sectorWorld = _entManager.System<SectorWorldSystem>();
        if (!sectorWorld.TryGetDefaultSectorMap(out var sectorMap, out var sector))
        {
            shell.WriteError("Default sector map is not available.");
            return;
        }

        shell.WriteLine($"sector: map {GetMapIdText(sectorMap)}");

        if (sector.SpaceMap is { } spaceMap)
            shell.WriteLine($"space: map {GetMapIdText(spaceMap)}");

        if (sector.FtlMap is { } ftlMap)
            shell.WriteLine($"ftl: map {GetMapIdText(ftlMap)}");

        if (WeatherCommandHelpers.TryGetColcommMap(_entManager, out var colCommMap))
            shell.WriteLine($"colcomm: map {GetMapIdText(colCommMap)}");

        foreach (var planetType in sector.PlanetTypes)
        {
            if (!sector.PlanetTypeMaps.TryGetValue(planetType.Id, out var mapUid))
                continue;

            shell.WriteLine($"{planetType.Id}: map {GetMapIdText(mapUid)} ({planetType.Name})");
        }
    }

    private string GetMapIdText(EntityUid mapUid)
    {
        return _entManager.TryGetComponent<MapComponent>(mapUid, out var mapComp)
            ? mapComp.MapId.ToString()
            : "unknown";
    }
}

internal static class WeatherCommandHelpers
{
    public static bool TryResolveWeather(IPrototypeManager proto, string input, out string? weatherId, out string error)
    {
        error = string.Empty;
        weatherId = null;

        if (string.Equals(input, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "clear", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "off", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (proto.HasIndex<EntityPrototype>(input))
        {
            weatherId = input;
            return true;
        }

        var prefixed = $"Weather{input}";
        if (proto.HasIndex<EntityPrototype>(prefixed))
        {
            weatherId = prefixed;
            return true;
        }

        error = $"Unknown weather prototype '{input}'. Try an entity id like WeatherRain, or use 'none' to clear.";
        return false;
    }

    public static bool TryResolvePersistentMap(IEntityManager entManager, SectorWorldSystem sectorWorld, string target, out EntityUid mapUid, out string targetLabel, out string error)
    {
        mapUid = EntityUid.Invalid;
        targetLabel = target;
        error = string.Empty;

        if (!sectorWorld.TryGetDefaultSectorMap(out var sectorMap, out var sector))
        {
            error = "Default sector map is not available.";
            return false;
        }

        switch (target.ToLowerInvariant())
        {
            case "sector":
                mapUid = sectorMap;
                targetLabel = "sector";
                return true;
            case "space":
                mapUid = sector.SpaceMap ?? sectorMap;
                targetLabel = "space";
                return true;
            case "ftl" when sector.FtlMap is { } ftlMap:
                mapUid = ftlMap;
                targetLabel = "ftl";
                return true;
            case "colcomm" when TryGetColcommMap(entManager, out var colCommMap):
                mapUid = colCommMap;
                targetLabel = "colcomm";
                return true;
        }

        if (sector.PlanetTypeMaps.TryGetValue(target, out var planetMap))
        {
            mapUid = planetMap;
            targetLabel = target;
            return true;
        }

        error = $"Unknown persistent map target '{target}'. Use listplanetmaps to see valid planet targets.";
        return false;
    }

    public static List<CompletionOption> GetPersistentMapTargets(IEntityManager entManager, SectorWorldSystem sectorWorld)
    {
        var options = new List<CompletionOption>();
        if (!sectorWorld.TryGetDefaultSectorMap(out var sectorMap, out var sector))
            return options;

        options.Add(new CompletionOption("sector", DescribeMap(entManager, sectorMap)));
        options.Add(new CompletionOption("space", DescribeMap(entManager, sector.SpaceMap ?? sectorMap)));

        if (sector.FtlMap is { } ftlMap)
            options.Add(new CompletionOption("ftl", DescribeMap(entManager, ftlMap)));

        if (TryGetColcommMap(entManager, out var colCommMap))
            options.Add(new CompletionOption("colcomm", DescribeMap(entManager, colCommMap)));

        foreach (var planetType in sector.PlanetTypes.Where(pt => sector.PlanetTypeMaps.ContainsKey(pt.Id)))
        {
            sector.PlanetTypeMaps.TryGetValue(planetType.Id, out var mapUid);
            options.Add(new CompletionOption(planetType.Id, $"{planetType.Name} - {DescribeMap(entManager, mapUid)}"));
        }

        return options;
    }

    private static string DescribeMap(IEntityManager entManager, EntityUid mapUid)
    {
        return entManager.TryGetComponent<MapComponent>(mapUid, out var mapComp)
            ? $"Map {mapComp.MapId}"
            : "Map unknown";
    }

    public static bool TryGetColcommMap(IEntityManager entManager, out EntityUid mapUid)
    {
        mapUid = EntityUid.Invalid;

        var emergencyShuttle = entManager.System<EmergencyShuttleSystem>();
        var colcommMaps = emergencyShuttle.GetColcommMaps();
        if (colcommMaps.Count == 0)
            return false;

        mapUid = colcommMaps.First();
        return true;
    }
}