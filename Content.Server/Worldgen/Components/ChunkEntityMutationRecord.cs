using System.Numerics;

namespace Content.Server.Worldgen.Components;

public sealed record ChunkEntityMutationRecord(
    Vector2 LocalPosition,
    string PrototypeId,
    double Rotation,
    bool Anchored);

public static class ChunkEntityMutationRules
{
    public static bool ShouldAnchor(string prototypeId)
    {
        return prototypeId.StartsWith("Wall", StringComparison.OrdinalIgnoreCase)
            || prototypeId.StartsWith("NFWall", StringComparison.OrdinalIgnoreCase)
            || prototypeId.Contains("Door", StringComparison.OrdinalIgnoreCase)
            || prototypeId.Contains("Airlock", StringComparison.OrdinalIgnoreCase)
            || prototypeId.Contains("Window", StringComparison.OrdinalIgnoreCase)
            || prototypeId.Contains("Mineral", StringComparison.OrdinalIgnoreCase)
            || (prototypeId.Contains("Cable", StringComparison.OrdinalIgnoreCase)
                && !prototypeId.Contains("Stack", StringComparison.OrdinalIgnoreCase)
                && !prototypeId.Contains("Placer", StringComparison.OrdinalIgnoreCase))
            || string.Equals(prototypeId, "Grille", StringComparison.OrdinalIgnoreCase);
    }
}