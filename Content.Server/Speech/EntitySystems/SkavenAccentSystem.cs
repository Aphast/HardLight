using System.Linq;
using System.Text.RegularExpressions;
using Content.Server.Speech.Components;
using Robust.Shared.Random;

namespace Content.Server.Speech.EntitySystems;

public sealed class SkavenAccentSystem : EntitySystem
{
    private static readonly Regex RegexLastPunctuation = new(@"([.!?]+$)(?!.*[.!?])|(?<![.!?])$");
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SkavenAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    public string Accentuate(string message, SkavenAccentComponent component)
    {
        // Order:
        // Do text manipulations first
        // Then prefix/suffix funnyies

        // direct word replacements
        var msg = _replacement.ApplyReplacements(message, "skaven");



        // Sanitize capital again, in case we substituted a word that should be capitalized
        msg = msg[0].ToString().ToUpper() + msg.Remove(0, 1);
        return msg;
    }
    private void OnAccentGet(EntityUid uid, SkavenAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}
