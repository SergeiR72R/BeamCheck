using System;
using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;

namespace BeamCheck.Core.Recognition
{
    /// <summary>Straight 3D segment, mm.</summary>
    public readonly struct Segment
    {
        public readonly Vec3 A;
        public readonly Vec3 B;

        public Segment(Vec3 a, Vec3 b)
        {
            A = a;
            B = b;
        }

        public double Length => (B - A).Length;

        public double PlanLength => (B - A).LengthXY;

        public double Dz => Math.Abs(B.Z - A.Z);

        public bool IsHorizontal(double tol = 1) => Dz <= tol;

        public bool IsVertical(double tol = 1) => PlanLength <= tol;
    }

    /// <summary>
    /// Recognises a scaffold component from its axis geometry and builds its
    /// <see cref="ScaffoldElement"/> geometry. Works on whatever lines the CAD layer
    /// collects (PERI "_Line" display blocks, exploded graphics or block bounding-box edges),
    /// so any beam and any PERI article is handled without a catalogue:
    ///   vertical lines                      → standard (incl. base jacks);
    ///   a long horizontal line, thin, flat  → ledger;
    ///   a flat rectangle 150–800 mm wide    → deck;
    ///   a long inclined line                → diagonal.
    /// </summary>
    public sealed class ShapeAnalyzer
    {
        private readonly BeamCheckSettings _s;

        public ShapeAnalyzer(BeamCheckSettings settings)
        {
            _s = settings;
        }

        public ElementKind Guess(IList<Segment> segs)
        {
            if (segs == null || segs.Count == 0)
                return ElementKind.Unknown;

            var box = new Box(segs);
            var longest = segs.OrderByDescending(s => s.Length).First();
            if (longest.Length < _s.ShapeMinLength)
                return ElementKind.Unknown;

            if (box.PlanDiagonal <= _s.ShapeStandardMaxPlan && box.Dz >= _s.ShapeMinLength)
                return ElementKind.Standard;

            var horizontal = LongestHorizontal(segs);
            if (horizontal.HasValue && horizontal.Value.Length >= _s.ShapeMinLength)
            {
                var plan = PlanExtents(segs, horizontal.Value);
                if (box.Dz <= _s.ShapeDeckMaxDz && plan.Width >= _s.ShapeDeckMinWidth && plan.Width <= _s.ShapeDeckMaxWidth)
                    return ElementKind.Deck;
                if (box.Dz <= _s.ShapeLedgerMaxDz && plan.Width <= _s.ShapeLedgerMaxWidth)
                    return ElementKind.Ledger;
            }

            if (longest.Dz >= _s.ShapeMinLength && longest.PlanLength >= _s.ShapeMinLength)
                return ElementKind.Diagonal;

            return ElementKind.Accessory;
        }

        /// <summary>Builds axis/width/Z of an element of the given kind from its segments (null if impossible).</summary>
        public ScaffoldElement Build(IList<Segment> segs, ElementKind kind)
        {
            if (segs == null || segs.Count == 0)
                return null;

            var box = new Box(segs);
            var e = new ScaffoldElement { Kind = kind, ZMin = box.Min.Z, ZMax = box.Max.Z };

            if (kind == ElementKind.Standard)
            {
                // Axis = the vertical lines; ignore small plan details (rosettes, wedges).
                var verticals = segs.Where(s => s.IsVertical(5) && s.Dz > 1).ToList();
                double cx, cy;
                if (verticals.Count > 0)
                {
                    var main = verticals.OrderByDescending(s => s.Dz).First();
                    cx = (main.A.X + main.B.X) / 2;
                    cy = (main.A.Y + main.B.Y) / 2;
                }
                else
                {
                    cx = (box.Min.X + box.Max.X) / 2;
                    cy = (box.Min.Y + box.Max.Y) / 2;
                }

                e.Start = new Vec3(cx, cy, box.Min.Z);
                e.End = new Vec3(cx, cy, box.Max.Z);
                e.Width = box.PlanDiagonal;
                return e;
            }

            if (kind == ElementKind.Diagonal)
            {
                var longest = segs.OrderByDescending(s => s.Length).First();
                e.Start = longest.A;
                e.End = longest.B;
                return e;
            }

            var axisSeg = LongestHorizontal(segs) ?? segs.OrderByDescending(s => s.PlanLength).First();
            if (axisSeg.PlanLength < 1e-6)
                return null;
            var plan = PlanExtents(segs, axisSeg);
            double z = kind == ElementKind.Ledger
                ? (axisSeg.A.Z + axisSeg.B.Z) / 2   // the ledger bar, not its hooks
                : (box.Min.Z + box.Max.Z) / 2;

            // Axis through the middle of the plan footprint, from end to end
            // (plan point = Dir·u + Perp·v).
            var across = plan.Perp * ((plan.VMin + plan.VMax) / 2);
            e.Start = (across + plan.Dir * plan.UMin).WithZ(z);
            e.End = (across + plan.Dir * plan.UMax).WithZ(z);
            e.Width = plan.Width;
            return e;
        }

        private static Segment? LongestHorizontal(IList<Segment> segs)
        {
            Segment? best = null;
            foreach (var s in segs)
                if (s.IsHorizontal(1) && (!best.HasValue || s.Length > best.Value.Length))
                    best = s;
            return best;
        }

        private struct PlanFrame
        {
            public Vec3 Dir;
            public Vec3 Perp;
            public double UMin, UMax, VMin, VMax;

            public double Width => VMax - VMin;
        }

        /// <summary>Extents of all segment end points in the plan frame aligned with <paramref name="axis"/>.</summary>
        private static PlanFrame PlanExtents(IList<Segment> segs, Segment axis)
        {
            var dir = (axis.B - axis.A).PlanDirection();
            var perp = new Vec3(-dir.Y, dir.X, 0);
            var f = new PlanFrame
            {
                Dir = dir, Perp = perp,
                UMin = double.MaxValue, UMax = double.MinValue, VMin = double.MaxValue, VMax = double.MinValue,
            };
            foreach (var s in segs)
            {
                foreach (var p in new[] { s.A, s.B })
                {
                    double u = p.DotXY(dir), v = p.DotXY(perp);
                    f.UMin = Math.Min(f.UMin, u);
                    f.UMax = Math.Max(f.UMax, u);
                    f.VMin = Math.Min(f.VMin, v);
                    f.VMax = Math.Max(f.VMax, v);
                }
            }

            return f;
        }

        private sealed class Box
        {
            public Box(IEnumerable<Segment> segs)
            {
                double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
                double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
                foreach (var s in segs)
                {
                    foreach (var p in new[] { s.A, s.B })
                    {
                        x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                        x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
                    }
                }

                Min = new Vec3(x0, y0, z0);
                Max = new Vec3(x1, y1, z1);
            }

            public Vec3 Min { get; }
            public Vec3 Max { get; }
            public double Dz => Max.Z - Min.Z;
            public double PlanDiagonal => (Max - Min).LengthXY;
        }

        /// <summary>The 12 edges of a box given by 8 corners (index bits: x, y, z).</summary>
        public static List<Segment> BoxEdges(IList<Vec3> c)
        {
            var edges = new List<Segment>();
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit <= 4; bit <<= 1)
                    if ((i & bit) == 0)
                        edges.Add(new Segment(c[i], c[i | bit]));
            return edges;
        }
    }
}
