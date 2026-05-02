
using Robust.Shared.Console;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Server.Power.Components;
using Content.Shared._Crescent.ShipShields;


namespace Content.Server._Crescent.ShipShields;
public partial class ShipShieldsSystem
{
    [Dependency] private readonly IConsoleHost _conHost = default!;

    public void InitializeCommands()
    {
        _conHost.RegisterCommand("shieldentity", "Create a shield around an entity", "shieldentity <uid>",
            ShieldEntityCmd);
        _conHost.RegisterCommand("unshieldentity", "Remove a shield from an entity", "unshieldentity <uid>",
            UnshieldEntityCmd);
        _conHost.RegisterCommand("shieldstatus", "Print shield emitter state", "shieldstatus <emitterUid>",
            ShieldStatusCmd);
    }

    [AdminCommand(AdminFlags.Debug)]
    public void ShieldEntityCmd(IConsoleShell shell, string argstr, string[] args)
    {
        // HardLight start
        if (args.Length < 1)
        {
            shell.WriteError("Usage: shieldentity <uid>");
            return;
        }
        // HardLight end

        if (!EntityUid.TryParse(args[0], out var uid))
        {
            shell.WriteError("Couldn't parse entity.");
            return;
        }

        var shield = ShieldEntity(uid);

        shell.WriteLine("Created shield " + shield);
    }

    [AdminCommand(AdminFlags.Debug)]
    public void UnshieldEntityCmd(IConsoleShell shell, string argstr, string[] args)
    {
        // HardLight start
        if (args.Length < 1)
        {
            shell.WriteError("Usage: unshieldentity <uid>");
            return;
        }
        // HardLight end

        if (!EntityUid.TryParse(args[0], out var uid))
        {
            shell.WriteError("Couldn't parse entity.");
            return;
        }

        var unshielded = UnshieldEntity(uid);

        if (unshielded)
            shell.WriteLine("Removed shield from " + uid);
        else
            shell.WriteError("No shield to remove from " + uid);
    }

    [AdminCommand(AdminFlags.Debug)]
    public void ShieldStatusCmd(IConsoleShell shell, string argstr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError("Usage: shieldstatus <emitterUid>");
            return;
        }

        if (!EntityUid.TryParse(args[0], out var uid))
        {
            shell.WriteError("Couldn't parse entity.");
            return;
        }

        if (!TryComp<ShipShieldEmitterComponent>(uid, out var emitter))
        {
            shell.WriteError($"{uid} is not a ShipShieldEmitter.");
            return;
        }

        var xform = Transform(uid);
        var gridUid = xform.GridUid;
        var hasPower = TryComp<ApcPowerReceiverComponent>(uid, out var power);

        shell.WriteLine($"Emitter: {uid}");
        shell.WriteLine($"Grid: {(gridUid?.ToString() ?? "<none>")}");
        shell.WriteLine($"Powered: {(hasPower ? power!.Powered.ToString() : "<no receiver>")}");
        shell.WriteLine($"Damage: {emitter.Damage:0.##}/{emitter.DamageLimit:0.##}");
        shell.WriteLine($"Recharging: {emitter.Recharging}");
        shell.WriteLine($"OverloadAccumulator: {emitter.OverloadAccumulator:0.##}");
        shell.WriteLine($"Emitter.Shield: {(emitter.Shield?.ToString() ?? "<null>")}");
        shell.WriteLine($"Emitter.Shield exists: {(emitter.Shield is { } s && Exists(s))}");

        if (gridUid is { } grid && TryComp<ShipShieldedComponent>(grid, out var shielded))
        {
            shell.WriteLine($"Grid marker source: {(shielded.Source?.ToString() ?? "<null>")}");
            shell.WriteLine($"Grid marker shield: {shielded.Shield}");
            shell.WriteLine($"Grid marker valid: {IsValidShieldEntity(shielded.Shield, uid, grid)}");
        }
        else
        {
            shell.WriteLine("Grid marker: <none>");
        }

        if (emitter.Shield is { } shieldUid && Exists(shieldUid) && TryComp<ShipShieldComponent>(shieldUid, out var shieldComp))
        {
            shell.WriteLine($"Shield.Source: {(shieldComp.Source?.ToString() ?? "<null>")}");
            shell.WriteLine($"Shield.Shielded: {shieldComp.Shielded}");
            shell.WriteLine($"Shield valid for emitter/grid: {gridUid is { } g && IsValidShieldEntity(shieldUid, uid, g)}");
        }
    }
}
