using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Recognition;
using BeamCheck.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace BeamCheck.Core.Tests
{
    /// <summary>On a real, dense scaffold the set of used elements must stay local to the selected standards.</summary>
    public class UsedCountProbe
    {
        private readonly ITestOutputHelper _out;

        public UsedCountProbe(ITestOutputHelper o)
        {
            _out = o;
        }

        [DataContract] private sealed class Fx { [DataMember(Name = "beam")] public string Beam; [DataMember(Name = "parts")] public List<P> Parts; }
        [DataContract] private sealed class P { [DataMember(Name = "h")] public string H; [DataMember(Name = "s")] public List<double[]> S; }

        private BeamLoadSession Run(string file, bool ledgerOnLedger, out List<ScaffoldElement> all, out List<ScaffoldElement> picked)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(UsedCountProbe).Assembly.Location), "Fixtures", file);
            Fx f;
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
                f = (Fx)new DataContractJsonSerializer(typeof(Fx)).ReadObject(gz);
            var settings = new BeamCheckSettings { LedgerOnLedgerEnabled = ledgerOnLedger };
            var shape = new ShapeAnalyzer(settings);
            all = new List<ScaffoldElement>();
            ScaffoldElement beam = null;
            foreach (var p in f.Parts)
            {
                var segs = p.S.Select(s => new Segment(new Vec3(s[0], s[1], s[2]), new Vec3(s[3], s[4], s[5]))).ToList();
                var kind = p.H == f.Beam ? ElementKind.Beam : shape.Guess(segs);
                if (kind == ElementKind.Unknown) continue;
                var e = shape.Build(segs, kind);
                e.Id = p.H;
                e.WeightKg = 5;
                if (kind == ElementKind.Beam) beam = e; else all.Add(e);
            }

            var b = beam;
            picked = all.Where(e => e.Kind == ElementKind.Standard).Where(e =>
            {
                double t = PlanGeometry.ProjectXY(b.Start, b.End, e.Start, out double off);
                return t >= 0 && t <= 1 && off < 150 && e.ZMin > b.ZMax - 100 && e.ZMin < b.ZMax + 600;
            }).ToList();
            return new BeamLoadSession(settings, new AnalysisInput { Beam = beam, SelectedStandards = picked, Candidates = all });
        }

        [Theory]
        [InlineData("peri24_beam_33625.json.gz")]
        [InlineData("peri24_beam_34084.json.gz")]
        public void Ledger_on_ledger_routing_does_not_blow_up_the_set_of_used_elements(string file)
        {
            var off = Run(file, false, out var all, out _);
            var on = Run(file, true, out _, out _);
            int Count(BeamLoadSession s, ElementKind k) => all.Count(e => e.Kind == k && s.Topology.UsedElementIds.Contains(e.Id));
            _out.WriteLine($"{file}: without — ledgers {Count(off, ElementKind.Ledger)}, decks {Count(off, ElementKind.Deck)}; with — ledgers {Count(on, ElementKind.Ledger)}, decks {Count(on, ElementKind.Deck)}");

            var added = all.Where(e => e.Kind == ElementKind.Ledger && on.Topology.UsedElementIds.Contains(e.Id) && !off.Topology.UsedElementIds.Contains(e.Id)).ToList();
            foreach (var l in added)
            {
                double dmin = on.Topology.Columns.Min(c => Vec3.DistanceXY(c.Axis, (l.Start + l.End) / 2));
                bool endOnStand = on.Topology.Columns.Any(c => Vec3.DistanceXY(c.Axis, l.Start) < 150 || Vec3.DistanceXY(c.Axis, l.End) < 150);
                _out.WriteLine($"  + ledger {l.Id} L={l.PlanLength:0} Ztop={l.ZMax:0} dist-to-col={dmin:0} endOnSelectedStand={endOnStand} start={l.Start} end={l.End}");
            }
            var addedDecks = all.Where(e => e.Kind == ElementKind.Deck && on.Topology.UsedElementIds.Contains(e.Id) && !off.Topology.UsedElementIds.Contains(e.Id)).ToList();
            var dd = addedDecks.Select(d => on.Topology.Columns.Min(c => Vec3.DistanceXY(c.Axis, (d.Start + d.End) / 2))).OrderBy(x => x).ToList();
            if (dd.Count > 0)
                _out.WriteLine($"  added decks: {dd.Count}, distance to nearest selected column: min {dd.First():0} / median {dd[dd.Count / 2]:0} / max {dd.Last():0} mm; contribs {on.Topology.Contributions.Count(c => addedDecks.Any(d => d.Id == c.Source.Id))}, area on columns {on.Topology.Contributions.Where(c => c.Type == ContributionType.Deck && addedDecks.Any(d => d.Id == c.Source.Id)).Sum(c => c.AreaM2):0.000} m2, deck area total {addedDecks.Sum(d => d.AreaM2):0.00} m2");
            foreach (var c in on.Topology.Columns) _out.WriteLine($"  col {c.Name} {c.Axis} Z {c.ZBottom:0}..{c.ZTop:0}");

            // Everything the routing adds stays in the neighbourhood of the selected standards and carries load.
            Assert.All(added, l => Assert.True(on.Topology.Columns.Min(c => Vec3.DistanceXY(c.Axis, (l.Start + l.End) / 2)) <= 3000));
            Assert.All(addedDecks, d =>
            {
                Assert.True(on.Topology.Columns.Min(c => Vec3.DistanceXY(c.Axis, (d.Start + d.End) / 2)) <= 3000);
                Assert.Contains(on.Topology.Contributions, c => c.Source.Id == d.Id && c.AreaM2 > 0);
            });
            // And never more than the structure around the columns: a few dozen elements, not hundreds.
            Assert.True(added.Count < 60 && addedDecks.Count < 120);
        }
    }
}
