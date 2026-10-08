using BeamCheck.Core.Geometry;

namespace BeamCheck.Core.Model
{
    /// <summary>
    /// CAD-independent description of one scaffold component.
    /// All lengths in mm, weights in kg.
    /// </summary>
    public sealed class ScaffoldElement
    {
        /// <summary>CAD handle (unique per drawing).</summary>
        public string Id { get; set; }

        public ElementKind Kind { get; set; }

        public string Article { get; set; }

        public string Description { get; set; }

        /// <summary>
        /// Axis start. Standards: bottom centre. Ledgers/beams: one end of the bar axis.
        /// Decks: centre of one bearing edge (decks span Start→End).
        /// </summary>
        public Vec3 Start { get; set; }

        /// <summary>Axis end (see <see cref="Start"/>).</summary>
        public Vec3 End { get; set; }

        /// <summary>Width across the axis (decks, beams).</summary>
        public double Width { get; set; }

        public double ZMin { get; set; }

        public double ZMax { get; set; }

        public double WeightKg { get; set; }

        /// <summary>True when the weight came from default per-metre values instead of article data.</summary>
        public bool WeightIsEstimated { get; set; }

        public double Length => (End - Start).Length;

        public double PlanLength => (End - Start).LengthXY;

        /// <summary>Deck area in m² (span × width).</summary>
        public double AreaM2 => PlanLength * Width / 1e6;

        public string DisplayName =>
            string.IsNullOrEmpty(Article) ? (Description ?? Id) : Article + (string.IsNullOrEmpty(Description) ? "" : " " + Description);

        public override string ToString() => $"{Kind} {DisplayName} [{Id}]";
    }
}
