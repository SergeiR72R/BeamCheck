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

        public static List<ScaffoldElement> Load(BeamCheckSettings settings)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Test5Tests).Assembly.Location), "Fixtures", "peri24_test5.json.gz");
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
    }
}
