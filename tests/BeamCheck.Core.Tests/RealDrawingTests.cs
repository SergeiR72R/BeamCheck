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
    /// <summary>
    /// Real PERI CAD 24 geometry (axis lines of every part, mm) exported from a customer drawing
    /// around two beams. Exercises shape recognition and the whole load path on genuine data.
    /// </summary>
    public class RealDrawingTests
    {
        private readonly ITestOutputHelper _out;

        public RealDrawingTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [DataContract]
        private sealed class Fixture
        {
            [DataMember(Name = "beam")] public string Beam { get; set; }
            [DataMember(Name = "parts")] public List<Part> Parts { get; set; }
        }

        [DataContract]
        private sealed class Part
        {
            [DataMember(Name = "h")] public string Handle { get; set; }
            [DataMember(Name = "s")] public List<double[]> Segments { get; set; }
        }

        private static Fixture Load(string name)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(RealDrawingTests).Assembly.Location), "Fixtures", name);
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
                return (Fixture)new DataContractJsonSerializer(typeof(Fixture)).ReadObject(gz);
        }

        private static List<Segment> Segs(Part p) =>
            p.Segments.Select(s => new Segment(new Vec3(s[0], s[1], s[2]), new Vec3(s[3], s[4], s[5]))).ToList();

        /// <summary>Simulates BEAMLOAD: recognise everything, then "select" the standards standing on the beam.</summary>
        private BeamLoadSession Run(string fixture, BeamCheckSettings settings)
        {
            var f = Load(fixture);
            var shape = new ShapeAnalyzer(settings);
            var classifier = new ElementClassifier(settings);
            var elements = new List<ScaffoldElement>();
            ScaffoldElement beam = null;
            foreach (var p in f.Parts)
            {
                var segs = Segs(p);
                var kind = p.Handle == f.Beam ? ElementKind.Beam : shape.Guess(segs);
                if (kind == ElementKind.Unknown)
                    continue;
                var e = shape.Build(segs, kind);
                e.Id = p.Handle;
                e.WeightKg = classifier.DefaultWeightKg(kind, kind == ElementKind.Standard ? e.ZMax - e.ZMin : e.Length, e.Width);
                e.WeightIsEstimated = true;
                if (kind == ElementKind.Beam)
                    beam = e;
                else
                    elements.Add(e);
            }

            _out.WriteLine("kinds: " + string.Join(", ", elements.GroupBy(e => e.Kind).Select(g => g.Key + "=" + g.Count())));
            _out.WriteLine($"beam: {beam.Start} → {beam.End}, L={beam.PlanLength:0}, width {beam.Width:0}, top {beam.ZMax:0}");

            // The user's window selection: standards whose foot is on the beam footprint.
            var picked = elements.Where(e => e.Kind == ElementKind.Standard).Where(e =>
            {
                double t = PlanGeometry.ProjectXY(beam.Start, beam.End, e.Start, out double off);
                return t >= 0 && t <= 1 && off < 150 && e.ZMin > beam.ZMax - 100 && e.ZMin < beam.ZMax + 600;
            }).ToList();

            var session = new BeamLoadSession(settings, new AnalysisInput { Beam = beam, SelectedStandards = picked, Candidates = elements });
            var r = session.Result;
            foreach (var c in session.Topology.Columns)
                _out.WriteLine($"{c.Name}: X={c.Position:0} sections={c.Sections.Count} Z {c.ZBottom:0}..{c.ZTop:0}");
            foreach (var l in session.Topology.Levels)
                _out.WriteLine($"level {l.Index}: Z={l.Z:0}, area on standards {session.LevelArea(l):0.000} m²");
            foreach (var p in r.Points)
                _out.WriteLine($"{p.Name} X={p.Position:0} A={p.AreaM2:0.000} Gk={p.Gk:0.00} Qk={p.Qk:0.00} Fk={p.Fk:0.00} | " +
                               string.Join("; ", p.Levels.Select(l => $"L{l.Level.Index} {l.AreaM2:0.000}m²")));
            _out.WriteLine("chain: " + string.Join(" / ", r.DimensionChain().Select(x => x.ToString("0"))));
            foreach (var w in r.Warnings.Distinct())
                _out.WriteLine("! " + w);
            return session;
        }

        [Fact]
        public void Beam_5472_with_four_standards()
        {
            var s = Run("peri24_beam_33625.json.gz", new BeamCheckSettings { LevelMode = LevelModes.AllLevels });

            Assert.Equal(5472, s.Topology.BeamLength, 0);
            Assert.Equal(4, s.Topology.Columns.Count);
            Assert.All(s.Topology.Columns, c => Assert.True(c.OnBeam));
            Assert.True(s.Topology.Levels.Count >= 1);
            Assert.True(s.Result.Points.Sum(p => p.AreaM2) > 0.5);
        }

        [Fact]
        public void Beam_3472_with_standards()
        {
            var s = Run("peri24_beam_34084.json.gz", new BeamCheckSettings { LevelMode = LevelModes.AllLevels });

            Assert.Equal(3472, s.Topology.BeamLength, 0);
            Assert.True(s.Topology.Columns.Count >= 3);
            Assert.True(s.Result.Points.Sum(p => p.AreaM2) > 0.5);
        }
    }
}
