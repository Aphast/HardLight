using Robust.Shared.GameObjects;

namespace Content.Server.Procedural;

/// <summary>
/// Dungeon atlas template entities now persist until explicitly cleaned up.
/// </summary>
public sealed class DungeonAtlasTemplateDespawnSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
    }
}
