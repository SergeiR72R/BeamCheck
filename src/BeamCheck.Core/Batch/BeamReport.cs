using System.Collections.Generic;
using System.Linq;

namespace BeamCheck.Core.Batch
{
    public enum BeamEventKind
    {
        Support,
        Load,
    }

    /// <summary>One support or one load on a beam, positioned from the beam's left edge.</summary>
    public sealed class BeamEvent
    {
        public BeamEventKind Kind { get; set; }

        /// <summary>S1, S2… for supports, A, B… for loads (left to right).</summary>
        public string Label { get; set; }

        /// <summary>Distance from the left edge of the beam, mm.</summary>
        public double Position { get; set; }

        /// <summary>Distance from the previous support or load along the beam (from the left edge for the first), mm.</summary>
        public double Delta { get; set; }

        /// <summary>Loads: permanent / service / total characteristic and design value, kN (supports carry no values).</summary>
        public double Gk { get; set; }

        public double Qk { get; set; }

        public double Fk => Gk + Qk;

        public double Fd { get; set; }

        /// <summary>Loads: tributary deck area, m².</summary>
        public double AreaM2 { get; set; }

        /// <summary>Diagnostics for the log: where it comes from (stack heights, plan offset from the beam axis, parts).</summary>
        public string Detail { get; set; }

        /// <summary>Loads: name of the standard it comes from (S1 of the single-beam check).</summary>
        public string Source { get; set; }
    }

    /// <summary>Everything the batch run found out about one beam. Positions are measured from its left edge.</summary>
    public sealed class BeamReport
    {
        public string Name { get; set; }

        /// <summary>CAD handle of the beam (hex), for finding it in the model. For beams joined by connectors: the first part.</summary>
        public string Id { get; set; }

        /// <summary>Handles of all parts that make up the beam (several when beams are joined by a connector).</summary>
        public List<string> PartIds { get; } = new List<string>();

        public string Article { get; set; }

        public string Description { get; set; }

        public double Length { get; set; }

        /// <summary>Beam axis ends in the drawing (mm), as read from the model (start → end, not mirrored).</summary>
        public double StartX { get; set; }

        public double StartY { get; set; }

        public double EndX { get; set; }

        public double EndY { get; set; }

        public double CenterX => (StartX + EndX) / 2;

        public double CenterY => (StartY + EndY) / 2;

        public double CenterZ => (ZMin + ZMax) / 2;

        public double ZMin { get; set; }

        public double ZMax { get; set; }

        /// <summary>Profile height, mm (for the illustration).</summary>
        public double Height { get; set; }

        public double WeightKg { get; set; }

        /// <summary>True when the beam's own start is its right-hand end (the report was mirrored to read left to right).</summary>
        public bool Reversed { get; set; }

        /// <summary>No standard under the beam: it is taken to lie on a firm, flat surface along its whole length.</summary>
        public bool ContinuousSupport { get; set; }

        public int LoadClass { get; set; }

        public double GammaG { get; set; } = 1.5;

        public double GammaQ { get; set; } = 1.5;

        public List<BeamEvent> Supports { get; } = new List<BeamEvent>();

        public List<BeamEvent> Loads { get; } = new List<BeamEvent>();

        public List<string> Notes { get; } = new List<string>();

        /// <summary>Ids of the elements that went into this beam's check (for showing them in the model).</summary>
        public HashSet<string> Related { get; } = new HashSet<string>();

        /// <summary>Own weight of the beam, kN (spread evenly; included in the reactions).</summary>
        public double SelfWeightKn { get; set; }

        /// <summary>Supports and loads together, left to right.</summary>
        public IEnumerable<BeamEvent> Events => Supports.Concat(Loads).OrderBy(e => e.Position).ThenBy(e => e.Kind);

        public double TotalLoadFk => Loads.Sum(l => l.Fk);

        public double MaxLoadFk => Loads.Count == 0 ? 0 : Loads.Max(l => l.Fk);

    }
}
