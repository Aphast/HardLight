using System.Linq;
using System.Text.Json;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Humanoid.Markings
{
    [DataDefinition]
    [Serializable, NetSerializable]
    public sealed partial class Marking : IEquatable<Marking>, IComparable<Marking>, IComparable<string>
    {
        [DataField("markingColor")]
        private List<Color> _markingColors = new();

        [DataField]
        public bool IsGlowing = false; //starlight

        private Marking()
        {
        }

        public Marking(string markingId,
            List<Color> markingColors, bool isGlowing) //starlight, glowing
        {
            MarkingId = markingId;
            _markingColors = markingColors;
            IsGlowing = isGlowing; //starlight
        }

        public Marking(string markingId,
            List<Color> markingColors,
            bool isGlowing,
            MarkingCategories category)
            : this(markingId, markingColors.Count, isGlowing, category)
        {
            _markingColors = markingColors;
        }

        public Marking(string markingId,
            IReadOnlyList<Color> markingColors, bool isGlowing) //starlight, glowing
            : this(markingId, new List<Color>(markingColors), isGlowing)
        {
        }

        public Marking(string markingId, int colorCount)
        {
            MarkingId = markingId;
            List<Color> colors = new();
            for (int i = 0; i < colorCount; i++)
                colors.Add(Color.White);
            _markingColors = colors;
        }

        public Marking(string markingId, int colorCount, MarkingCategories category)
            : this(markingId, colorCount)
        {
            // Coyote marking improvements
            ToggleDataInitialized = true;
            if (category == MarkingCategories.UndergarmentBottom || category == MarkingCategories.UndergarmentTop)
            {
                CanToggleVisible = true;
                OtherCanToggleVisible = true;
            }
            else if (category == MarkingCategories.Genital)
            {
                ShowAtStart = false;
                CanToggleVisible = true;
                OtherCanToggleVisible = false;
                PutOnVerb = "show";
                PutOnVerb2p = "shows";
                TakeOffVerb = "hide";
                TakeOffVerb2p = "hides";
            }
            else
            {
                PutOnVerb = "show";
                PutOnVerb2p = "shows";
                TakeOffVerb = "hide";
                TakeOffVerb2p = "hides";
            }
        }

        // HardLight - integrating the glows into this has been a pain
        public Marking(string markingId, int colorCount, bool isGlowing, MarkingCategories category)
            : this(markingId, colorCount, category)
        {
            IsGlowing = isGlowing;
        }

        public Marking(Marking marking, IReadOnlyList<Color> markingColors, bool isGlowing)
            : this(marking, markingColors)
        {
            IsGlowing = isGlowing;
        }

        public Marking(Marking marking, IReadOnlyList<Color> markingColors)
            : this(marking)
        {
            _markingColors = new(markingColors);
        }

        public Marking(Marking marking, int colorCount)
            : this(marking)
        {
            List<Color> colors = new();
            for (int i = 0; i < colorCount; i++)
                colors.Add(Color.White);
            _markingColors = colors;
        }

        public Marking(Marking other)
        {
            MarkingId = other.MarkingId;
            _markingColors = new(other.MarkingColors);
            Visible = other.Visible;
            Forced = other.Forced;
            IsGlowing = other.IsGlowing; //starlight

            // Coyote marking improvements
            CustomName = other.CustomName;
            ToggleDataInitialized = other.ToggleDataInitialized;
            ShowAtStart = other.ShowAtStart;
            CanToggleVisible = other.CanToggleVisible;
            OtherCanToggleVisible = other.OtherCanToggleVisible;
            PutOnVerb = other.PutOnVerb;
            PutOnVerb2p = other.PutOnVerb2p;
            TakeOffVerb = other.TakeOffVerb;
            TakeOffVerb2p = other.TakeOffVerb2p;
        }

        /// <summary>
        ///     ID of the marking prototype.
        /// </summary>
        [DataField("markingId", required: true)]
        public string MarkingId { get; private set; } = default!;

        /// <summary>
        ///     All colors currently on this marking.
        /// </summary>
        [ViewVariables]
        public IReadOnlyList<Color> MarkingColors => _markingColors;

        /// <summary>
        ///     If this marking is currently visible.
        /// </summary>
        [DataField("visible")]
        public bool Visible = true;

        /// <summary>
        ///     Optional display name used by the in-game marking toggle verbs.
        /// </summary>
        // Coyote marking improvements
        [DataField("customName")]
        public string? CustomName;

        /// <summary>
        ///     Whether the in-game toggle metadata has been initialized from prototype/category defaults.
        ///     Older DB rows do not carry this, so profile load can safely hydrate their defaults.
        /// </summary>
        [DataField("toggleDataInitialized")]
        public bool ToggleDataInitialized;

        /// <summary>
        ///     Whether this marking should start visible after profile load.
        /// </summary>
        [DataField("showAtStart")]
        public bool ShowAtStart = true;

        /// <summary>
        ///     Whether this marking can be toggled by its wearer in-game.
        /// </summary>
        [DataField("canToggleVisible")]
        public bool CanToggleVisible;

        /// <summary>
        ///     Whether this marking can be toggled by other players in-game.
        /// </summary>
        [DataField("otherCanToggleVisible")]
        public bool OtherCanToggleVisible;

        [DataField("putOnVerb")]
        public string PutOnVerb = "put on";

        [DataField("takeOffVerb")]
        public string TakeOffVerb = "take off";

        [DataField("putOnVerb2p")]
        public string PutOnVerb2p = "puts on";

        [DataField("takeOffVerb2p")]
        public string TakeOffVerb2p = "takes off";

        /// <summary>
        ///     If this marking should be forcefully applied, regardless of points.
        /// </summary>
        [ViewVariables]
        public bool Forced;

        public void SetColor(int colorIndex, Color color) =>
            _markingColors[colorIndex] = color;

        public void SetColor(Color color)
        {
            for (int i = 0; i < _markingColors.Count; i++)
            {
                _markingColors[i] = color;
            }
        }

        public int CompareTo(Marking? marking)
        {
            if (marking == null)
            {
                return 1;
            }

            return string.Compare(MarkingId, marking.MarkingId, StringComparison.Ordinal);
        }

        public int CompareTo(string? markingId)
        {
            if (markingId == null)
                return 1;

            return string.Compare(MarkingId, markingId, StringComparison.Ordinal);
        }

        public bool Equals(Marking? other)
        {
            if (other == null)
            {
                return false;
            }
            return MarkingId.Equals(other.MarkingId)
                && _markingColors.SequenceEqual(other._markingColors)
                && Visible.Equals(other.Visible)
                && Forced.Equals(other.Forced)
                && IsGlowing.Equals(other.IsGlowing) //starlight
                && CustomName == other.CustomName
                && ToggleDataInitialized == other.ToggleDataInitialized
                && ShowAtStart == other.ShowAtStart
                && CanToggleVisible == other.CanToggleVisible
                && OtherCanToggleVisible == other.OtherCanToggleVisible
                && PutOnVerb == other.PutOnVerb
                && PutOnVerb2p == other.PutOnVerb2p
                && TakeOffVerb == other.TakeOffVerb
                && TakeOffVerb2p == other.TakeOffVerb2p;
        }
    }
}
