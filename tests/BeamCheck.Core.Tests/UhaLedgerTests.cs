using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    /// <summary>
    /// Zeichnung3.dwg (second version): beam along X, our standard A at (803, 1225), a UHV ledger A→B along X,
    /// crossbar ledgers at X = 1803 and X = 2803 carrying 10 decks (950 x 245) between them. The crossbar at
    /// X = 1803 starts on a UHA connector in the middle of the UHV ledger (no standard there) and ends on a
    /// standard; the one at X = 2803 stands on standards at both ends. Coordinates are relative, Z absolute.
    /// </summary>
    public class UhaLedgerTests
    {
        private int _n;

        private ScaffoldElement Std(double x, double y) => new ScaffoldElement
        {
            Id = "S" + (++_n), Kind = ElementKind.Standard, Start = new Vec3(x, y, 133458), End = new Vec3(x, y, 134618),
            ZMin = 133458, ZMax = 134618, WeightKg = 0,
        };

        private ScaffoldElement Ledger(Vec3 a, Vec3 b, double zMin = 133778) => new ScaffoldElement
        {
            Id = "L" + (++_n), Kind = ElementKind.Ledger, Start = a, End = b, ZMin = zMin, ZMax = 133868, WeightKg = 0,
        };

        private (BeamLoadSession session, ScaffoldElement standA, List<ScaffoldElement> decks) Build(bool uha = true, double deckZ = 133914.2)
        {
            var beam = new ScaffoldElement
            {
                Id = "BEAM", Kind = ElementKind.Beam, Start = new Vec3(0, 1228, 133094), End = new Vec3(2980, 1228, 133094),
                Width = 82, ZMin = 132994, ZMax = 133194,
            };
            var a = Std(803, 1225);
            var b = Std(2803, 1225);
            var c = Std(1803, 3725);
            var d = Std(2803, 3725);
            var all = new List<ScaffoldElement> { a, b, c, d };
            all.Add(Ledger(new Vec3(845, 1225, 133868), new Vec3(2761, 1225, 133868), 133718));                 // UHV 1916 A → B
            all.Add(Ledger(new Vec3(1803, 1267, 133868), new Vec3(1803, 3683, 133868)));                         // hangs on the UHV at 1803
            all.Add(Ledger(new Vec3(2803, 1267, 133868), new Vec3(2803, 3683, 133868)));                         // between B and D
            var decks = new List<ScaffoldElement>();
            for (int i = 0; i < 10; i++)
            {
                double y = 1350 + 250 * i;
                decks.Add(new ScaffoldElement
                {
                    Id = "D" + i, Kind = ElementKind.Deck, Start = new Vec3(1828, y, deckZ), End = new Vec3(2778, y, deckZ),
                    Width = 245, ZMin = deckZ - 63, ZMax = deckZ, WeightKg = 10,
                });
            }

            all.AddRange(decks);
            var input = new AnalysisInput { Beam = beam, SelectedStandards = new List<ScaffoldElement> { a }, Candidates = all };
            var s = new BeamLoadSession(new BeamCheckSettings { LedgerOnLedgerEnabled = uha, DeckSnapEnabled = false }, input);
            return (s, a, decks);
        }

        private const double DeckArea = 10 * 0.950 * 0.245;

        [Fact]
        public void Without_the_connector_rule_the_decks_over_the_hanging_ledger_are_lost()
        {
            var (s, _, decks) = Build(uha: false);

            Assert.Equal(0, s.Result.Points.Sum(p => p.AreaM2), 9);
            Assert.DoesNotContain(decks, d => s.Topology.UsedElementIds.Contains(d.Id));
        }

        [Fact]
        public void The_hanging_ledgers_share_reaches_our_standard_through_the_supporting_ledger()
        {
            var (s, _, decks) = Build();

            // Half of every deck rests on the hanging crossbar; of that, the end on the UHV gets (by position along
            // the crossbar) a quarter of the total deck area in all; it sits mid-span of the UHV, so our standard
            // takes half of it: 1/8 of the deck area.
            Assert.Equal(DeckArea / 8, s.Result.Points.Single().AreaM2, 6);
            Assert.All(decks, d => Assert.Contains(d.Id, s.Topology.UsedElementIds));
        }

        [Fact]
        public void A_connector_closer_to_our_standard_gives_it_more()
        {
            var (s, a, _) = Build();
            double mid = s.Result.Points.Single().AreaM2;

            // Same drawing, hanging crossbar moved to 1/4 of the UHV span: A takes 3/4 of that corner's load.
            var input = s.Input;
            foreach (var l in input.Candidates.Where(e => e.Kind == ElementKind.Ledger && e.Start.X == 1803).ToList())
            {
                l.Start = new Vec3(1303, l.Start.Y, l.Start.Z);
                l.End = new Vec3(1303, l.End.Y, l.End.Z);
            }

            foreach (var d in input.Candidates.Where(e => e.Kind == ElementKind.Deck))
                d.Start = new Vec3(1328, d.Start.Y, d.Start.Z);
            s.Reanalyze();

            Assert.True(s.Result.Points.Single().AreaM2 > mid * 1.3);
        }

        [Fact]
        public void A_ledger_end_close_to_the_end_of_another_ledger_is_a_corner_not_a_connector()
        {
            var (s, _, decks) = Build();
            foreach (var l in s.Input.Candidates.Where(e => e.Kind == ElementKind.Ledger && e.Start.X == 1803).ToList())
            {
                l.Start = new Vec3(2761 - 60, l.Start.Y, l.Start.Z);   // within the end clearance of the UHV (ends at 2761)
                l.End = new Vec3(2761 - 60, l.End.Y, l.End.Z);
            }

            s.Reanalyze();
            // Nothing is routed to our standard A through that hanging end now.
            Assert.True(s.Result.Points.Single().AreaM2 < DeckArea / 8 + 1e-9);
        }
    }
}

namespace BeamCheck.Core.Tests
{
    public class UhaRecognitionTests
    {
        [Theory]
        [InlineData("UHA", "101731", "PERI_UP_UHA")]
        [InlineData("UHA", "", "PERI_UP_UHA")]
        public void The_UHA_connector_is_an_accessory_not_a_ledger(string name, string art, string layer)
        {
            var sig = new Recognition.ElementSignature { EntityType = "Part", Layer = layer };
            sig.Add("PART:Name", name);
            sig.Add("PART:ArtNr", art);

            Assert.Equal(Model.ElementKind.Accessory, new Recognition.ElementClassifier(new Settings.BeamCheckSettings()).Classify(sig).Kind);
        }

        [Theory]
        [InlineData("UH 150 +", "PERI_FLEX_UH_NEW")]
        [InlineData("UHV 200 PLUS", "PERI_FLEX_UHV_NEW")]
        public void Real_ledgers_are_still_ledgers(string name, string layer)
        {
            var sig = new Recognition.ElementSignature { EntityType = "Part", Layer = layer };
            sig.Add("PART:Name", name);
            Assert.Equal(Model.ElementKind.Ledger, new Recognition.ElementClassifier(new Settings.BeamCheckSettings()).Classify(sig).Kind);
        }
    }
}
