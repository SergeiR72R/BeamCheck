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
    /// Test3.dwg (PERI CAD 24, axis lines): the front ledger 523 (z≈0) carries 10 decks and, on a UH Zapfen pin, a standard at x=6316.
    /// That standard carries a UCM 75 console (z 2060) with 3 decks and a standard on its tip, which carries decks on ledgers
    /// and a UCM 50 console with 2 more decks (z 4060).
    /// </summary>
    public class Test3Tests
    {
        private readonly ITestOutputHelper _out;
        public Test3Tests(ITestOutputHelper o) { _out = o; }

        [DataContract] private sealed class Fixture
        {
            [DataMember(Name = "beam")] public string Beam { get; set; }
            [DataMember(Name = "parts")] public List<Part> Parts { get; set; }
        }

        [DataContract] private sealed class Part
        {
            [DataMember(Name = "h")] public string Handle { get; set; }
            [DataMember(Name = "s")] public List<double[]> Segments { get; set; }
            [DataMember(Name = "art")] public string Art { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
        }

        private BeamLoadSession Run(BeamCheckSettings settings, bool directDecks, string beamId = "523", params string[] picked)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Test3Tests).Assembly.Location), "Fixtures", "peri24_test3_ledger_523.json.gz");
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
                var sig = new ElementSignature { EntityType = "Part", Layer = "0" };
                if (!string.IsNullOrEmpty(p.Name))
                {
                    sig.Add("PART:ArtNr", p.Art);
                    sig.Add("PART:Name", p.Name);
                }

                var cls = classifier.Classify(sig);
                var kind = p.Handle == beamId ? ElementKind.Beam : cls.Kind != ElementKind.Unknown ? cls.Kind : shape.Guess(segs);
                if (kind == ElementKind.Unknown) continue;
                var e = shape.Build(segs, kind);
                if (e == null) continue;
                e.Id = p.Handle;
                e.Kind = kind;
                e.Article = cls.Article;
                e.Description = cls.Description;
                e.WeightKg = classifier.DefaultWeightKg(kind, kind == ElementKind.Standard ? e.ZMax - e.ZMin : e.Length, e.Width);
                if (p.Handle == beamId) beam = e; else elements.Add(e);
            }

            var pick = elements.Where(e => picked.Contains(e.Id)).ToList();
            var session = new BeamLoadSession(settings, new AnalysisInput { Beam = beam, SelectedStandards = pick, Candidates = elements, DirectDecks = directDecks });
            _out.WriteLine("kinds: " + string.Join(", ", elements.GroupBy(e => e.Kind).Select(g => g.Key + "=" + g.Count())));
            _out.WriteLine($"member {beam.Id}: ({beam.Start.X:0},{beam.Start.Y:0})→({beam.End.X:0},{beam.End.Y:0}) Z {beam.ZMin:0}..{beam.ZMax:0} L={beam.PlanLength:0}");
            foreach (var e in elements.Where(e => e.Kind == ElementKind.Console))
                _out.WriteLine($"console {e.Id}: ({e.Start.X:0},{e.Start.Y:0})→({e.End.X:0},{e.End.Y:0}) Z {e.ZMin:0}..{e.ZMax:0}");
            foreach (var c in session.Topology.Columns)
                _out.WriteLine($"col {c.Name}: pos={c.Position:0} sections={c.Sections.Count} Z {c.ZBottom:0}..{c.ZTop:0}");
            foreach (var u in session.Topology.UpperBeams)
                _out.WriteLine($"link {u.Beam.Id} {u.Beam.Kind}: supports=[{string.Join(",", u.Supports.Select(x => x.Position.ToString("0") + (x.Column == null ? "(x)" : "(" + x.Column.Name + ")")))}] loaded=[{string.Join(",", u.Loaded.Select(x => x.Name))}]");
            foreach (var l in session.Topology.Levels)
                _out.WriteLine($"level {l.Index} Z={l.Z:0} area {session.LevelArea(l):0.000}");
            foreach (var p in session.Result.Points)
                _out.WriteLine($"{p.Name} X={p.Position:0} A={p.AreaM2:0.000} Gk={p.Gk:0.00} Qk={p.Qk:0.00} transfer={p.TransferGk:0.00}+{p.TransferQk:0.00}");
            foreach (var w in session.Result.Warnings.Distinct()) _out.WriteLine("! " + w);
            _out.WriteLine("used: " + string.Join(",", session.Topology.UsedElementIds.OrderBy(x => x)));
            return session;
        }

        [Fact]
        public void Consoles_and_the_pin_are_recognised_by_name()
        {
            var session = Run(new BeamCheckSettings { LevelMode = LevelModes.AllLevels }, false, "523", "5FF");
            var cls = new ElementClassifier(new BeamCheckSettings());
            var pin = new ElementSignature { EntityType = "Part" };
            pin.Add("PART:Name", "UH Zapfen");
            Assert.Equal(ElementKind.Accessory, cls.Classify(pin).Kind);
            var ucm = new ElementSignature { EntityType = "Part" };
            ucm.Add("PART:Name", "UCM 75 mit Zapfen");
            Assert.Equal(ElementKind.Console, cls.Classify(ucm).Kind);
            Assert.Equal(2, session.Topology.UpperBeams.Count(l => l.Beam.Kind == ElementKind.Console));
        }

        [Fact]
        public void The_stand_on_the_ledger_carries_the_decks_on_its_consoles_down_the_chain()
        {
            var s = Run(new BeamCheckSettings { LevelMode = LevelModes.AllLevels }, false, "523", "5FF");

            var p = Assert.Single(s.Result.Points);
            // Level z≈2076: 3 decks 0.245 x 1.95 m, half of each on the console at y=5535 (the other half on the other console).
            // Level z≈4076: 3 decks on ledgers (half) + 2 decks on the UCM 50 console (half); the ledger ends and the console
            // are on the stand standing on the UCM 75 tip, whose load goes down the UCM 75 console to the checked stand.
            double deck = 0.245 * 1.95;
            Assert.Equal(0.5 * deck * (3 + 3 + 2), p.AreaM2, 2);
            Assert.Equal(2, s.Topology.Levels.Count);
            Assert.True(p.TransferQk > 0 && p.TransferGk > 0);
        }

        [Fact]
        public void Decks_lying_on_the_checked_ledger_are_separate_points_when_asked()
        {
            var s = Run(new BeamCheckSettings { LevelMode = LevelModes.AllLevels }, true, "523", "5FF");

            var direct = s.Result.Points.Where(p => p.Name.StartsWith("D")).ToList();
            Assert.Equal(10, direct.Count);
            Assert.All(direct, p => Assert.Equal(0.5 * 0.245 * 1.95, p.AreaM2, 3));
            Assert.Equal(new[] { "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D10" }, direct.Select(p => p.Name).ToArray());
            // The stand keeps its own point in between.
            Assert.Equal(11, s.Result.Points.Count);
            Assert.Equal(direct.OrderBy(p => p.Position).Select(p => p.Name), direct.Select(p => p.Name));
        }

        [Fact]
        public void A_console_whose_standard_is_not_part_of_the_check_adds_nothing()
        {
            // The check is on the other ledger (y=7535, 699) with its own stand: the y=5535 consoles carry nothing for it.
            var s = Run(new BeamCheckSettings { LevelMode = LevelModes.AllLevels }, false, "699", "69B");
            var p = Assert.Single(s.Result.Points);
            Assert.Equal(0.5 * 0.245 * 1.95 * 8, p.AreaM2, 2);
        }
    }
}
