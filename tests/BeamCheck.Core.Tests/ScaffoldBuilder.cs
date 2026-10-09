using System.Collections.Generic;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;

namespace BeamCheck.Core.Tests
{
    /// <summary>
    /// Synthetic scaffold matching the example drawing: beam L = 4472 along X (top at Z = 0),
    /// standards on the beam at X = 1423 / 2423 / 3923, a second (inner) row at Y = 732,
    /// decks spanning along X between transverse ledgers at every level.
    /// </summary>
    internal sealed class ScaffoldBuilder
    {
        /// <summary>Distance between the two standard rows (Y), mm.</summary>
        public double Width = 732;

        /// <summary>Standard positions along the beam (X), mm.</summary>
        public double[] Xs = { 1423, 2423, 3923 };

        public double DeckWidth => Width / 3;

        private int _id;

        public ScaffoldElement Beam { get; private set; }
        public List<ScaffoldElement> Elements { get; } = new List<ScaffoldElement>();
        public List<ScaffoldElement> BeamRowStandards { get; } = new List<ScaffoldElement>();

        public double StandardKg = 10;
        public double DeckKg = 5;
        public double TransverseLedgerKg = 4;

        /// <summary>Ledger ends stop this far from the standard axis (as modelled wedge heads may).</summary>
        public double LedgerGap = 0;

        public static ScaffoldBuilder Example(params double[] levelZ) => Build(new ScaffoldBuilder(), levelZ);

        /// <summary>Regular scaffold: standards at <paramref name="xs"/>, two rows <paramref name="width"/> apart.</summary>
        public static ScaffoldBuilder Custom(double[] xs, double width, params double[] levelZ) =>
            Build(new ScaffoldBuilder { Xs = xs, Width = width }, levelZ);

        private static ScaffoldBuilder Build(ScaffoldBuilder b, double[] levelZ)
        {
            b.Beam = new ScaffoldElement
            {
                Id = "BEAM", Kind = ElementKind.Beam, Article = "HEB200",
                Start = new Vec3(0, 0, -100), End = new Vec3(b.Xs[b.Xs.Length - 1] + 549, 0, -100), Width = 200, ZMin = -200, ZMax = 0,
            };
            double top = levelZ.Length == 0 ? 2000 : levelZ[levelZ.Length - 1] + 1000;
            foreach (double x in b.Xs)
            {
                foreach (double y in new[] { 0.0, b.Width })
                {
                    for (double z = 0; z < top; z += 2000)
                    {
                        var st = b.Add(ElementKind.Standard, new Vec3(x, y, z), new Vec3(x, y, z + 2000), 0, b.StandardKg);
                        if (y == 0)
                            b.BeamRowStandards.Add(st);
                    }
                }
            }

            foreach (double z in levelZ)
                b.AddLevel(z);
            return b;
        }

        public void AddLevel(double z)
        {
            for (int i = 0; i < Xs.Length; i++)
            {
                AddLedger(new Vec3(Xs[i], 0, z), new Vec3(Xs[i], Width, z), TransverseLedgerKg);
                if (i == 0)
                    continue;
                double span = Xs[i] - Xs[i - 1];
                AddLedger(new Vec3(Xs[i - 1], 0, z), new Vec3(Xs[i], 0, z), span / 200);
                AddLedger(new Vec3(Xs[i - 1], Width, z), new Vec3(Xs[i], Width, z), span / 200);
                for (int d = 0; d < 3; d++)
                {
                    double yc = DeckWidth / 2 + d * DeckWidth;
                    Add(ElementKind.Deck, new Vec3(Xs[i - 1], yc, z + 30), new Vec3(Xs[i], yc, z + 30), DeckWidth, DeckKg, z + 10, z + 60);
                }
            }
        }

        public ScaffoldElement AddLedger(Vec3 a, Vec3 b, double kg)
        {
            var dir = (b - a).Normalized();
            return Add(ElementKind.Ledger, a + dir * LedgerGap, b - dir * LedgerGap, 0, kg);
        }

        public ScaffoldElement Add(ElementKind kind, Vec3 a, Vec3 b, double width, double kg, double? zMin = null, double? zMax = null)
        {
            var e = new ScaffoldElement
            {
                Id = kind + "#" + (++_id), Kind = kind, Start = a, End = b, Width = width, WeightKg = kg,
                ZMin = zMin ?? System.Math.Min(a.Z, b.Z), ZMax = zMax ?? System.Math.Max(a.Z, b.Z),
            };
            Elements.Add(e);
            return e;
        }

        public AnalysisInput Input(IEnumerable<ScaffoldElement> selected = null) => new AnalysisInput
        {
            Beam = Beam,
            SelectedStandards = new List<ScaffoldElement>(selected ?? BeamRowStandards),
            Candidates = new List<ScaffoldElement>(Elements),
        };
    }
}
