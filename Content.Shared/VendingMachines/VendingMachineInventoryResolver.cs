using System.Linq;
using Robust.Shared.Prototypes;

namespace Content.Shared.VendingMachines;

public static class VendingMachineInventoryResolver
{
    public static Dictionary<string, uint> ResolveRegular(
        IPrototypeManager prototypes,
        VendingMachineInventoryPrototype prototype)
    {
        var resolved = new Dictionary<string, uint>();
        var seen = new HashSet<string>();
        Resolve(prototypes, prototype, p => p.StartingInventory, resolved, seen);
        return resolved;
    }

    public static Dictionary<string, uint> ResolveEmagged(
        IPrototypeManager prototypes,
        VendingMachineInventoryPrototype prototype)
    {
        var resolved = new Dictionary<string, uint>();
        var seen = new HashSet<string>();
        Resolve(prototypes, prototype, p => p.EmaggedInventory, resolved, seen);
        return resolved;
    }

    public static Dictionary<string, uint> ResolveContraband(
        IPrototypeManager prototypes,
        VendingMachineInventoryPrototype prototype)
    {
        var resolved = new Dictionary<string, uint>();
        var seen = new HashSet<string>();
        Resolve(prototypes, prototype, p => p.ContrabandInventory, resolved, seen);
        return resolved;
    }

    private static void Resolve(
        IPrototypeManager prototypes,
        VendingMachineInventoryPrototype prototype,
        Func<VendingMachineInventoryPrototype, Dictionary<string, uint>?> selector,
        Dictionary<string, uint> resolved,
        HashSet<string> seen)
    {
        if (!seen.Add(prototype.ID))
            throw new InvalidOperationException($"Circular vending inventory inheritance detected at '{prototype.ID}'.");

        if (prototype.Inherits != null && prototypes.TryIndex(prototype.Inherits, out VendingMachineInventoryPrototype? parent))
            Resolve(prototypes, parent, selector, resolved, seen);

        if (prototype.InheritAsUnlimited)
        {
            var keys = resolved.Keys.ToArray();
            foreach (var key in keys)
            {
                resolved[key] = uint.MaxValue;
            }
        }

        var entries = selector(prototype);
        if (entries != null)
        {
            foreach (var (id, count) in entries)
            {
                resolved[id] = count;
            }
        }

        seen.Remove(prototype.ID);
    }
}