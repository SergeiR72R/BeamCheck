using System.Collections.Generic;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;

namespace BeamCheck.Core.Analysis
{
    /// <summary>A vertical stack of standard sections standing on the beam.</summary>
    public sealed class Column
    {
        public string Name { get; set; }

        /// <summary>Plan position of the standard axis; Z = column bottom.</summary>
        public Vec3 Axis { get; set; }

        public double ZBottom { get; set; }

        public double ZTop { get; set; }

        public List<ScaffoldElement> Sections { get; } = new List<ScaffoldElement>();

        /// <summary>Distance from the beam start along the beam axis, mm.</summary>
        public double Position { get; set; }

        /// <summary>Plan distance of the standard axis from the beam axis, mm.</summary>
        public double OffsetFromBeamAxis { get; set; }

        public bool OnBeam { get; set; }

        public override string ToString() => $"{Name} @ {Position:0} mm";
    }

    /// <summary>A deck level (all decks with about the same Z).</summary>
    public sealed class Level
    {
        /// <summary>1 = lowest level above the beam.</summary>
        public int Index { get; set; }

        /// <summary>Deck bottom elevation, mm.</summary>
        public double Z { get; set; }

        public override string ToString() => $"Level {Index} (Z={Z:0})";
    }

    public enum ContributionType
    {
        /// <summary>Own weight of a standard section of the column.</summary>
        StandardSelfWeight,
        /// <summary>Own weight of a ledger connected to the column (½ per end).</summary>
        LedgerSelfWeight,
        /// <summary>Own weight of a diagonal or accessory connected to the column.</summary>
        OtherSelfWeight,
        /// <summary>Deck: own weight and service-load area, transferred via a ledger.</summary>
        Deck,
    }

    /// <summary>A share of one element's weight (and, for decks, area) arriving at one column.</summary>
    public sealed class Contribution
    {
        public Column Column { get; set; }

        public ScaffoldElement Source { get; set; }

        /// <summary>The ledger through which a deck load reaches the column.</summary>
        public ScaffoldElement Via { get; set; }

        public ContributionType Type { get; set; }

        /// <summary>Only set for decks.</summary>
        public Level Level { get; set; }

        /// <summary>Fraction (0..1) of the source element carried by this column.</summary>
        public double Share { get; set; }

        public double WeightKg => Source.WeightKg * Share;

        /// <summary>Tributary deck area, m² (decks only).</summary>
        public double AreaM2 => Type == ContributionType.Deck ? Source.AreaM2 * Share : 0;
    }

    /// <summary>Geometry/topology result. Independent of load class and level factors.</summary>
    public sealed class Topology
    {
        public ScaffoldElement Beam { get; set; }

        public double BeamLength => Beam.PlanLength;

        public List<Column> Columns { get; } = new List<Column>();

        public List<Level> Levels { get; } = new List<Level>();

        public List<Contribution> Contributions { get; } = new List<Contribution>();

        public List<string> Warnings { get; } = new List<string>();

        /// <summary>IDs of all elements that contribute (for highlighting in CAD).</summary>
        public HashSet<string> UsedElementIds { get; } = new HashSet<string>();
    }
}
