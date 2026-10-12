using System;
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
    /// <summary>Test2.dwg (PERI CAD 24): beam DC9 (5472) with one standard, a beam on top of the stack, short posts on that beam.</summary>
    public class Test2Tests
    {
        private readonly ITestOutputHelper _out;
        public Test2Tests(ITestOutputHelper o) { _out = o; }

        [DataContract] private sealed class Fixture
        {
            [DataMember(Name = "beam")] public string Beam { get; set; }
            [DataMember(Name = "parts")] public List<Part> Parts { get; set; }
        }
        [DataContract] private sealed class Part
        {
            [DataMember(Name = "h")] public string Handle { get; set; }
            [DataMember(Name = "s")] public List<double[]> Segments { get; set; }
        }

        private BeamLoadSession Run(BeamCheckSettings settings, params string[] upperBeamHandles)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Test2Tests).Assembly.Location), "Fixtures", "peri24_test2_beam_DC9.json.gz");
            Fixture f;
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
                f = (Fixture)new DataContractJsonSerializer(typeof(Fixture)).ReadObject(gz);
            var shape = new ShapeAnalyzer(settings);
            var classifier = new ElementClassifier(settings);
            var elements = new List<ScaffoldElement>();
            ScaffoldElement beam = null;
            foreach (var p in f.Parts)
            {
                var segs = p.Segments.Select(s => new Segment(new Vec3(s[0], s[1], s[2]), new Vec3(s[3], s[4], s[5]))).ToList();
                var kind = p.Handle == f.Beam || upperBeamHandles.Contains(p.Handle) ? ElementKind.Beam : shape.Guess(segs);
                if (kind == ElementKind.Unknown) continue;
                var e = shape.Build(segs, kind);
                e.Id = p.Handle;
                e.WeightKg = classifier.DefaultWeightKg(kind, kind == ElementKind.Standard ? e.ZMax - e.ZMin : e.Length, e.Width);
                if (p.Handle == f.Beam) beam = e; else elements.Add(e);
            }
            var picked = elements.Where(e => e.Kind == ElementKind.Standard && Math.Abs(e.Start.Y - 320125) < 100 && Math.Abs(e.Start.X + 801721) < 100).ToList();
            var session = new BeamLoadSession(settings, new AnalysisInput { Beam = beam, SelectedStandards = picked, Candidates = elements });
            foreach (var c in session.Topology.Columns)
                _out.WriteLine($"col {c.Name}: X={c.Position:0} sections={c.Sections.Count} Z {c.ZBottom:0}..{c.ZTop:0}");
            foreach (var b in session.Topology.UpperBeams)
                _out.WriteLine($"upper beam {b.Beam.Id}: supports={b.Supports.Count} loaded={b.Loaded.Count}");
            foreach (var l in session.Topology.Levels)
                _out.WriteLine($"level {l.Index}: Z={l.Z:0}, area {session.LevelArea(l):0.000}");
            foreach (var pt in session.Result.Points)
                _out.WriteLine($"{pt.Name} A={pt.AreaM2:0.000} Gk={pt.Gk:0.00} Qk={pt.Qk:0.00} transfer={pt.TransferGk:0.00}+{pt.TransferQk:0.00}");
            foreach (var w in session.Result.Warnings.Distinct()) _out.WriteLine("! " + w);
            _out.WriteLine("used: " + string.Join(",", session.Topology.UsedElementIds.OrderBy(x => x)));
            return session;
        }

        [Fact]
        public void A_profile_the_name_rules_do_not_know_is_an_upper_beam_when_a_column_passes_through_it()
        {
            var s = Run(new BeamCheckSettings());

            var link = Assert.Single(s.Topology.UpperBeams);
            Assert.Equal("3515", link.Beam.Id);
            // The short post 3496 stands on the beam; it is not a support under it.
            Assert.Single(link.Supports);
            Assert.Single(link.Loaded);
            var p = Assert.Single(s.Result.Points);
            Assert.True(p.AreaM2 > 3.0, "area " + p.AreaM2);
            Assert.True(p.TransferGk > 0.3 && p.TransferQk > 3.0, $"transfer {p.TransferGk} / {p.TransferQk}");
        }

        [Fact]
        public void Picking_the_upper_beam_by_hand_gives_the_same_load()
        {
            var auto = Run(new BeamCheckSettings()).Result.Points.Single();
            var manual = Run(new BeamCheckSettings(), "3515").Result.Points.Single();
            Assert.Equal(auto.AreaM2, manual.AreaM2, 6);
            Assert.Equal(auto.Qk, manual.Qk, 6);
        }

        [Fact]
        public void Probe()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Test2Tests).Assembly.Location), "Fixtures", "peri24_test2_beam_DC9.json.gz");
            Fixture f;
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
                f = (Fixture)new DataContractJsonSerializer(typeof(Fixture)).ReadObject(gz);
            var settings = new BeamCheckSettings();
            var shape = new ShapeAnalyzer(settings);
            foreach (var p in f.Parts)
            {
                var segs = p.Segments.Select(s => new Segment(new Vec3(s[0], s[1], s[2]), new Vec3(s[3], s[4], s[5]))).ToList();
                var k = shape.Guess(segs);
                var e = shape.Build(segs, k);
                _out.WriteLine($"{p.Handle} {k} start=({e.Start.X:0},{e.Start.Y:0}) end=({e.End.X:0},{e.End.Y:0}) Z {e.ZMin:0}..{e.ZMax:0} L={e.PlanLength:0} W={e.Width:0}");
            }
        }
    }
}
