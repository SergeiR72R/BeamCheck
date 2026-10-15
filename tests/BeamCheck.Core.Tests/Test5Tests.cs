using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BeamCheck.Core.Batch;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Recognition;
using BeamCheck.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace BeamCheck.Core.Tests
{
    /// <summary>Test5.dwg (axis lines): beam RCS 148 + RCS 498 joined by a connector, standards above and below it, UVA head pieces.</summary>
    public class Test5Tests
    {
        private readonly ITestOutputHelper _out;
        public Test5Tests(ITestOutputHelper o) { _out = o; }

        [DataContract] private sealed class Part
        {
            [DataMember(Name = "h")] public string H { get; set; }
            [DataMember(Name = "s")] public List<double[]> S { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "kind")] public string Kind { get; set; }
        }

        public static List<ScaffoldElement> Load(BeamCheckSettings settings, string fixture = "peri24_test5.json.gz")
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Test5Tests).Assembly.Location), "Fixtures", fixture);
            List<Part> parts;
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
                parts = (List<Part>)new DataContractJsonSerializer(typeof(List<Part>)).ReadObject(gz);
            var shape = new ShapeAnalyzer(settings);
            var classifier = new ElementClassifier(settings);
            var list = new List<ScaffoldElement>();
            foreach (var p in parts)
            {
                var segs = p.S.Select(s => new Segment(new Vec3(s[0], s[1], s[2]), new Vec3(s[3], s[4], s[5]))).ToList();
                var kind = !string.IsNullOrEmpty(p.Kind) ? (ElementKind)Enum.Parse(typeof(ElementKind), p.Kind) : shape.Guess(segs);
                if (kind == ElementKind.Unknown) continue;
                var e = shape.Build(segs, kind);
                if (e == null) continue;
                e.Id = p.H; e.Kind = kind; e.Description = p.Name;
                e.WeightKg = classifier.DefaultWeightKg(kind, kind == ElementKind.Standard ? e.ZMax - e.ZMin : e.Length, e.Width);
                list.Add(e);
            }

            return list;
        }

        [Fact]
        public void Only_stacks_that_reach_the_beam_are_supports_and_every_stand_on_it_is_a_load()
        {
            var s = new BeamCheckSettings();
            var reports = BatchAnalyzer.Run(Load(s), s, new BatchOptions());

            var r = Assert.Single(reports);
            Assert.Equal(6480, r.Length, 0);
            // Real supports: the stacks under the beam that reach it (stack top inside the beam's profile): S1 553, S3 4302, S5 5803.
            // The short piece at 5553 ends 36 mm below the beam and carries nothing; the head piece at 2302 has no stack below.
            Assert.Equal(3, r.Supports.Count);
            foreach (var (pos, want) in r.Supports.Select(x => x.Position).Zip(new[] { 553.0, 4303, 5803 }, (a, b) => (a, b)))
                Assert.InRange(pos, want - 3, want + 3);
            // Loads A..F: every stand on the beam, also those above supports.
            Assert.Equal(6, r.Loads.Count);
            foreach (var (pos, want) in r.Loads.Select(x => x.Position).Zip(new[] { 554.0, 2303, 3801, 4303, 4802, 5801 }, (a, b) => (a, b)))
                Assert.InRange(pos, want - 3, want + 3);
            Assert.Equal("ABCDEF", string.Concat(r.Loads.Select(x => x.Label)));
        }

        [Fact]
        public void A_ledger_fixed_to_the_tip_of_a_console_passes_its_deck_and_own_weight_to_the_console_root()
        {
            // In this model the ledger starts 42 mm beyond the console tip, so the default of 10 mm is widened here.
            var s = new BeamCheckSettings { ConsoleLedgerTolerance = 50 };
            var reports = BatchAnalyzer.Run(Load(s, "peri24_test5b.json.gz"), s, new BatchOptions());
            foreach (var r in reports)
            {
                _out.WriteLine(r.Name + " L=" + r.Length.ToString("0") + " supports " + string.Join(",", r.Supports.Select(x => x.Position.ToString("0"))));
                foreach (var l in r.Loads)
                    _out.WriteLine("  " + l.Label + " @" + l.Position.ToString("0") + " Gk=" + l.Gk.ToString("0.00") + " Qk=" + l.Qk.ToString("0.00") + " A=" + l.AreaM2.ToString("0.000") + " | " + l.Detail);
            }

            // Left beam: its standards carry the consoles, and the ledgers fixed to the console tips with the decks on them.
            // Right beam: the standards at the other ends of the same ledgers. By symmetry both take the same deck area, half of it each.
            var left = reports.OrderBy(x => x.StartX).First();
            var right = reports.OrderBy(x => x.StartX).Last();
            Assert.Equal(2, left.Loads.Count);
            Assert.Equal(2, right.Loads.Count);
            foreach (var l in left.Loads.Concat(right.Loads))
                Assert.Equal(0.233, l.AreaM2, 2);
            Assert.True(left.Loads[0].Gk > right.Loads[0].Gk, "the console adds its own weight on the left");
        }
    }
}
