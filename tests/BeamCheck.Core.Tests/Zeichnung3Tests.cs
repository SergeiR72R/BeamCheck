using System.Collections.Generic;
using System.Linq;
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
    /// Zeichnung3.dwg (PERI CAD 24): one standard on a steel beam, a 1916 ledger and a 666 ledger and 23 steel decks
    /// (UDG 25 x 125 / 250) lying on them. PERI_AEC_DB_BAUTEIL parts are read through their bounding boxes, so every
    /// part reaches the analyzer as 12 box edges (deck top at 133914.2, 63 mm thick).
    /// </summary>
    public class Zeichnung3Tests
    {
        private readonly ITestOutputHelper _out;

        public Zeichnung3Tests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static List<Segment> Box(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            var c = new List<Vec3>();
            for (int i = 0; i < 8; i++)
                c.Add(new Vec3((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1));
            return ShapeAnalyzer.BoxEdges(c);
        }

        private int _n;

        private ScaffoldElement Element(List<Segment> segs, ElementKind? forced = null)
        {
            var shape = new ShapeAnalyzer(new BeamCheckSettings());
            var kind = forced ?? shape.Guess(segs);
            var e = shape.Build(segs, kind);
            e.Id = "P" + (++_n);
            e.Kind = kind;
            e.WeightKg = 10;
            return e;
        }

        private (BeamLoadSession session, List<ScaffoldElement> all, ScaffoldElement stand) Build(BeamCheckSettings settings)
        {
            const double Y = -583729;
            var beam = Element(Box(254704, -583766, 132994, 257684, -583684, 133194), ElementKind.Beam);
            var stand = Element(Box(255426, Y - 80, 133458, 255586, Y + 80, 134618));
            var jack = Element(Box(255466, Y - 40, 133188, 255546, Y + 40, 133618), ElementKind.Standard);
            var all = new List<ScaffoldElement> { stand, jack };
            all.Add(Element(Box(255548, Y - 17, 133718, 257464, Y + 17, 133868)));          // ledger 1916
            all.Add(Element(Box(255547, -581246, 133778, 256213, -581212, 133868)));         // ledger 666
            // 1200 mm decks on the near side (x of the left edge), 2450 mm decks towards the far ledger.
            foreach (double x in new[] { 255509.0, 255759, 256009, 256259, 256509, 256759, 257009 })
                all.Add(Element(Box(x, -584954, 133851.2, x + 245, -583754, 133914.2)));
            foreach (double x in new[] { 255509.0, 255759, 256009, 256259, 256509, 256759, 257009, 257259 })
                all.Add(Element(Box(x, -583704, 133851.2, x + 245, -581254, 133914.2)));

            var input = new AnalysisInput { Beam = beam, SelectedStandards = new List<ScaffoldElement> { stand }, Candidates = all };
            return (new BeamLoadSession(settings, input), all, stand);
        }

        [Fact]
        public void Parts_are_classified_from_their_boxes()
        {
            var (_, all, _) = Build(new BeamCheckSettings());
            _out.WriteLine(string.Join(", ", all.GroupBy(e => e.Kind).Select(g => g.Key + "=" + g.Count())));

            Assert.Equal(15, all.Count(e => e.Kind == ElementKind.Deck));
            Assert.Equal(2, all.Count(e => e.Kind == ElementKind.Ledger));
            Assert.Equal(2, all.Count(e => e.Kind == ElementKind.Standard));
        }

        [Fact]
        public void Every_deck_resting_on_a_ledger_is_found_whichever_ledger_it_lies_on()
        {
            var (s, all, _) = Build(new BeamCheckSettings { DeckSnapEnabled = false });
            foreach (var w in s.Result.Warnings) _out.WriteLine(w);
            foreach (var p in s.Result.Points) _out.WriteLine($"{p.Name} A={p.AreaM2:0.000} G={p.Gk:0.00} Q={p.Qk:0.00}");

            Assert.Single(s.Topology.Columns);
            Assert.Contains(s.Result.Warnings, w => w.Contains("46 mm"));
            // All 15 decks have at least one bearing on a ledger (the far ends of the 1200 decks lie on nothing).
            var used = all.Where(e => e.Kind == ElementKind.Deck && s.Topology.UsedElementIds.Contains(e.Id)).ToList();
            Assert.Equal(15, used.Count);
        }

        [Fact]
        public void The_neighbourhood_summary_names_what_was_recognised_around_the_standard()
        {
            var (s, _, _) = Build(new BeamCheckSettings());
            var line = s.Result.Warnings.Single(w => w.StartsWith("ℹ Rund um die Ständer"));
            _out.WriteLine(line);

            Assert.Contains("Deck×15", line);
            Assert.Contains("Ledger×2", line);
        }

        private static Segment L(double x0, double y0, double z0, double x1, double y1, double z1) => new Segment(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));

        /// <summary>The same drawing read the other way: the PERI "_Line" axis lines (flat deck outlines, bar and hooks of the ledgers).</summary>
        [Fact]
        public void Reading_the_parts_from_their_axis_lines_gives_the_same_decks_and_the_same_seat()
        {
            const double Y = -583729;
            var shape = new ShapeAnalyzer(new BeamCheckSettings());
            ScaffoldElement E(List<Segment> segs, ElementKind? k = null)
            {
                var kind = k ?? shape.Guess(segs);
                var e = shape.Build(segs, kind);
                e.Id = "L" + (++_n);
                e.Kind = kind;
                e.WeightKg = 10;
                return e;
            }

            var beam = E(Box(254704, -583766, 132994, 257684, -583684, 133194), ElementKind.Beam);
            var stand = E(new List<Segment> { L(255506, Y, 133458, 255506, Y, 134618) });
            var all = new List<ScaffoldElement>
            {
                stand,
                // ledger 1916: bar at 133868, hooks down, lower chord
                E(new List<Segment>
                {
                    L(255548, Y, 133868, 257464, Y, 133868), L(255548, Y, 133868, 255548, Y, 133778), L(257464, Y, 133868, 257464, Y, 133778),
                    L(255590, Y, 133718, 255590, Y, 133868), L(257422, Y, 133718, 255590, Y, 133718), L(257422, Y, 133868, 257422, Y, 133718),
                }),
                E(new List<Segment> { L(255547, -581229, 133868, 256213, -581229, 133868), L(255547, -581229, 133868, 255547, -581229, 133778), L(256213, -581229, 133868, 256213, -581229, 133778) }),
            };
            foreach (double x in new[] { 255509.0, 255759, 256009, 256259, 256509, 256759, 257009 })
                all.Add(E(Rect(x, -584954, x + 245, -583754)));
            foreach (double x in new[] { 255509.0, 255759, 256009, 256259, 256509, 256759, 257009, 257259 })
                all.Add(E(Rect(x, -583704, x + 245, -581254)));

            var s = new BeamLoadSession(new BeamCheckSettings { DeckSnapEnabled = false },
                new AnalysisInput { Beam = beam, SelectedStandards = new List<ScaffoldElement> { stand }, Candidates = all });
            foreach (var w in s.Result.Warnings) _out.WriteLine(w);

            Assert.Equal(15, all.Count(e => e.Kind == ElementKind.Deck));
            Assert.Contains(s.Result.Warnings, w => w.Contains("46 mm"));
            Assert.Equal(15, all.Count(e => e.Kind == ElementKind.Deck && s.Topology.UsedElementIds.Contains(e.Id)));
        }

        private static List<Segment> Rect(double x0, double y0, double x1, double y1)
        {
            const double Z = 133914.2;
            return new List<Segment> { L(x0, y0, Z, x0, y1, Z), L(x0, y1, Z, x1, y1, Z), L(x1, y1, Z, x1, y0, Z), L(x1, y0, Z, x0, y0, Z) };
        }

        /// <summary>A reinforced ledger is taller; its decks lie on its top face, i.e. higher. The ledger top is the reference.</summary>
        [Fact]
        public void Decks_follow_the_top_of_a_taller_reinforced_ledger()
        {
            const double Y1 = -583729, Y2 = -581229;
            var beam = Element(Box(254704, -583766, 132994, 257684, -583684, 133194), ElementKind.Beam);
            var stand = Element(Box(255426, Y1 - 80, 133458, 255586, Y1 + 80, 134618));
            var all = new List<ScaffoldElement>
            {
                stand,
                Element(Box(255548, Y1 - 17, 133718, 257464, Y1 + 17, 133938)),   // reinforced, top 133938
                Element(Box(255548, Y2 - 17, 133718, 257464, Y2 + 17, 133938)),   // reinforced, top 133938
                Element(Box(255548, -587000, 133778, 257464, -586966, 133868)),    // normal, top 133868 (far away)
                Element(Box(255548, -585000, 133778, 257464, -584966, 133868)),    // normal, top 133868
            };
            // Decks on the reinforced pair sit 70 mm higher than decks on the normal pair.
            foreach (double x in new[] { 255509.0, 255759, 256009 })
                all.Add(Element(Box(x, Y1 + 25, 133921.2, x + 245, Y2 - 25, 133984.2)));
            foreach (double x in new[] { 255509.0, 255759, 256009 })
                all.Add(Element(Box(x, -586975, 133851.2, x + 245, -584975, 133914.2)));

            var s = new BeamLoadSession(new BeamCheckSettings { DeckSnapEnabled = false },
                new AnalysisInput { Beam = beam, SelectedStandards = new List<ScaffoldElement> { stand }, Candidates = all });
            foreach (var w in s.Result.Warnings) _out.WriteLine(w);

            Assert.Contains(s.Result.Warnings, w => w.Contains("um 46 mm"));
            Assert.Equal(3, all.Count(e => e.Kind == ElementKind.Deck && e.ZMax > 133950 && s.Topology.UsedElementIds.Contains(e.Id)));
        }

        [Fact]
        public void A_reinforced_ledger_taller_than_a_normal_one_is_still_a_ledger()
        {
            var shape = new ShapeAnalyzer(new BeamCheckSettings());
            Assert.Equal(ElementKind.Ledger, shape.Guess(Box(255548, -583746, 133718, 257464, -583712, 133868)));   // 150 high
            Assert.Equal(ElementKind.Ledger, shape.Guess(Box(255548, -583746, 133658, 257464, -583712, 133898)));   // 240 high, reinforced
            Assert.NotEqual(ElementKind.Ledger, shape.Guess(Box(255548, -583746, 133000, 257464, -583712, 135000))); // a wall of 2 m is not
        }
    }
}

