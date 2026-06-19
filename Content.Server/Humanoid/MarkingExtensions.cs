//starlight maintained file, this is needed for the DB rework of markings
using System.Text.Json;
using Content.Shared.Humanoid.Markings;

namespace Content.Server.Humanoid.Markings.Extensions;

public static class MarkingExtensions
{
        //refactored all this shit code to properly use json to pass things around instead of what it was doing before
        //should be much more stable for future wizden changes and whatever else we want to do
        public static string ToDBString(this Marking Marking)
        {
            // reserved character
            string sanitizedName = Marking.MarkingId.Replace('@', '_');
            List<string> colorStringList = new();
            foreach (Color color in Marking.MarkingColors)
                colorStringList.Add(color.ToHex());

            var json = JsonSerializer.Serialize(new MarkingDbString
            {
                MarkingId = sanitizedName,
                Colors = colorStringList,
                IsGlowing = Marking.IsGlowing,

                // Coyote marking improvements
                ToggleDataInitialized = Marking.ToggleDataInitialized,
                CustomName = Marking.CustomName,
                ShowAtStart = Marking.ShowAtStart,
                CanToggleVisible = Marking.CanToggleVisible,
                OtherCanToggleVisible = Marking.OtherCanToggleVisible,
                PutOnVerb = Marking.PutOnVerb,
                PutOnVerb2p = Marking.PutOnVerb2p,
                TakeOffVerb = Marking.TakeOffVerb,
                TakeOffVerb2p = Marking.TakeOffVerb2p
            });

            return json;

            //return $"{sanitizedName}@{String.Join(',', colorStringList)}@{IsGlowing}";
        }

        //dummy object definition to pass around
        private sealed class MarkingDbString
        {
            public string MarkingId { get; set; } = default!;
            public List<string> Colors { get; set; } = new();
            public bool IsGlowing { get; set; } = false;

            // Coyote marking improvements
            public bool? ToggleDataInitialized { get; set; }
            public string? CustomName { get; set; }
            public bool? ShowAtStart { get; set; }
            public bool? CanToggleVisible { get; set; }
            public bool? OtherCanToggleVisible { get; set; }
            public string? PutOnVerb { get; set; }
            public string? TakeOffVerb { get; set; }
            public string? PutOnVerb2p { get; set; }
            public string? TakeOffVerb2p { get; set; }
        }

        public static Marking? ParseFromDbString(string input)
        {
            List<Color> colorList;

            //first we need to decide if this string is in the old format or not
            //so try to parse it as a json object first
            if (IsJsonValid(input))
            {
                var json = JsonSerializer.Deserialize<MarkingDbString>(input);

                if (json == null) return null;

                colorList = new();
                foreach (string color in json.Colors)
                    colorList.Add(Color.FromHex(color));

                var marking = new Marking(json.MarkingId, colorList, json.IsGlowing);

                // Coyote marking improvements
                marking.CustomName = json.CustomName;
                marking.ShowAtStart = json.ShowAtStart ?? marking.ShowAtStart;
                marking.CanToggleVisible = json.CanToggleVisible ?? marking.CanToggleVisible;
                marking.OtherCanToggleVisible = json.OtherCanToggleVisible ?? marking.OtherCanToggleVisible;
                marking.PutOnVerb = json.PutOnVerb ?? marking.PutOnVerb;
                marking.PutOnVerb2p = json.PutOnVerb2p ?? marking.PutOnVerb2p;
                marking.TakeOffVerb = json.TakeOffVerb ?? marking.TakeOffVerb;
                marking.TakeOffVerb2p = json.TakeOffVerb2p ?? marking.TakeOffVerb2p;
                marking.ToggleDataInitialized = json.ToggleDataInitialized
                    ?? (json.ShowAtStart != null
                        || json.CanToggleVisible != null
                        || json.OtherCanToggleVisible != null
                        || json.CustomName != null
                        || json.PutOnVerb != null
                        || json.PutOnVerb2p != null
                        || json.TakeOffVerb != null
                        || json.TakeOffVerb2p != null);

                return marking;
            }

            if (input.Length == 0) return null;
            var split = input.Split('@');
            if (split.Length < 2) return null;
            colorList = new();
            foreach (string color in split[1].Split(','))
                colorList.Add(Color.FromHex(color));

            return new Marking(split[0], colorList, false);
        }

        public static bool IsJsonValid(string txt)
        {
            try { return JsonDocument.Parse(txt) != null; } catch { return false; }
        }
}
