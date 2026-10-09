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
    /// Main beam along X at Z 0 with two stacks (X = 0 and 4000, 2 sections each up to Z = 4000).
    /// An upper beam lies on their tops (Z 4000..4200) and carries further stands.
    /// </summary>
    public class UpperBeamTests
    {
        private int _n;

        private ScaffoldElement Std(double x, double y, double z0, double z1, double kg = 10) => new ScaffoldElement
        {
            Id = "S" + (++_n), Kind = ElementKind.Standard, Start = new Vec3(x, y, z0), End = new Vec3(x, y, z1),
            ZMin = z0, ZMax = z1, WeightKg = kg,
        };

        private ScaffoldElement UpperBeam(double x0, double x1, double kg = 100) => new ScaffoldElement
        {
            Id = "U" + (++_n), Kind = ElementKind.Beam, Description = "UB", Start = new Vec3(x0, 0, 4100), End = new Vec3(x1, 0, 4100),
            Width = 200, ZMin = 4000, ZMax = 4200, WeightKg = kg,
        };

        private (AnalysisInput input, List<ScaffoldElement> elements) Setup(params ScaffoldElement[] extra)
        {
            var beam = new ScaffoldElement
            {
                Id = "MAIN", Kind = ElementKind.Beam, Start = new Vec3(-500, 0, -100), End = new Vec3(4500, 0, -100),
                Width = 200, ZMin = -200, ZMax = 0,
            };
            var els = new List<ScaffoldElement>
            {
                Std(0, 0, 0, 2000), Std(0, 0, 2000, 4000), Std(4000, 0, 0, 2000), Std(4000, 0, 2000, 4000),
            };
            els.AddRange(extra);
            var selected = els.Where(e => e.Kind == ElementKind.Standard && e.ZMin == 0).ToList();
            return (new AnalysisInput { Beam = beam, SelectedStandards = selected, Candidates = els }, els);
        }

        private static BeamCheckSettings Settings() => new BeamCheckSettings { LevelMode = LevelModes.AllLevels };

        [Fact]
        public void Without_upper_beam_only_the_column_itself_counts()
        {
            var (input, _) = Setup();
            var r = new BeamLoadSession(Settings(), input).Result;

            Assert.Equal(2, r.Points.Count);
            Assert.Equal(LoadCalculator.KgToKn(20), r.Points[0].Gk, 6);
            Assert.All(r.Points, p => Assert.Equal(0, p.TransferFk, 9));
        }

        [Fact]
        public void Stands_on_an_upper_beam_load_its_two_supports_by_the_lever_rule()
        {
            var ub = UpperBeam(0, 4000);
            // Stands on the beam at X = 1000 (3/4 to the left support) and X = 3000 (1/4).
            var (input, _) = Setup(ub, Std(1000, 0, 4200, 6200), Std(3000, 0, 4200, 6200));
            var r = new BeamLoadSession(Settings(), input).Result;

            // Left column: own 20 + beam 50 + stands 10*0.75 + 10*0.25 = 80 kg.
            Assert.Equal(LoadCalculator.KgToKn(80), r.Points[0].Gk, 6);
            Assert.Equal(LoadCalculator.KgToKn(80), r.Points[1].Gk, 6);
            Assert.Equal(LoadCalculator.KgToKn(60), r.Points[0].TransferGk, 6);
            Assert.Equal(2 * LoadCalculator.KgToKn(80), r.Points.Sum(p => p.Gk), 6);
        }

        [Fact]
        public void An_off_centre_stand_loads_the_nearer_support_more()
        {
            var ub = UpperBeam(0, 4000, kg: 0);
            var (input, _) = Setup(ub, Std(1000, 0, 4200, 6200, kg: 40));
            var r = new BeamLoadSession(Settings(), input).Result;

            Assert.Equal(LoadCalculator.KgToKn(20 + 30), r.Points[0].Gk, 6);
            Assert.Equal(LoadCalculator.KgToKn(20 + 10), r.Points[1].Gk, 6);
        }

        [Fact]
        public void A_support_outside_the_selection_takes_its_share_and_the_beam_weight_is_spread_by_span()
        {
            // Upper beam 0..8000 rests on a third stack at 8000 that is not selected.
            var ub = UpperBeam(0, 8000);
            var (input, els) = Setup(ub, Std(8000, 0, 0, 2000), Std(8000, 0, 2000, 4000), Std(3000, 0, 4200, 6200));
            var r = new BeamLoadSession(Settings(), input).Result;

            // Beam weight 100 kg: left support 25, middle support 50, outer one 25.
            // Stand at 3000 between 0 and 4000: 0.25 left, 0.75 middle.
            Assert.Equal(LoadCalculator.KgToKn(20 + 25 + 2.5), r.Points[0].Gk, 6);
            Assert.Equal(LoadCalculator.KgToKn(20 + 50 + 7.5), r.Points[1].Gk, 6);
        }

        [Fact]
        public void Distribute_handles_spans_overhangs_and_single_supports()
        {
            Assert.Equal(new[] { 1.0 }, ScaffoldAnalyzer.Distribute(new[] { 100.0 }, 5000));
            var mid = ScaffoldAnalyzer.Distribute(new[] { 0.0, 4000.0 }, 1000);
            Assert.Equal(0.75, mid[0], 9);
            Assert.Equal(0.25, mid[1], 9);
            var over = ScaffoldAnalyzer.Distribute(new[] { 0.0, 4000.0 }, 5000);   // 1000 beyond the right support
            Assert.Equal(1.25, over[1], 9);
            Assert.Equal(-0.25, over[0], 9);
        }

        [Fact]
        public void Deck_load_on_upper_stands_reaches_the_main_beam_only_for_the_stands_it_carries()
        {
            var ub = UpperBeam(0, 4000, kg: 0);
            var els = new List<ScaffoldElement> { ub };
            var st = Std(2000, 0, 4200, 6200, kg: 0);
            var st2 = Std(2000, 1000, 4200, 6200, kg: 0);
            els.AddRange(new[] { st, st2 });
            // A 1000 x 1000 mm platform between the two upper stands on two ledgers, at Z = 5000.
            var l1 = new ScaffoldElement { Id = "L1", Kind = ElementKind.Ledger, Start = new Vec3(2000, 0, 5000), End = new Vec3(2000, 1000, 5000), ZMin = 5000, ZMax = 5000 };
            var l2 = new ScaffoldElement { Id = "L2", Kind = ElementKind.Ledger, Start = new Vec3(3000, 0, 5000), End = new Vec3(3000, 1000, 5000), ZMin = 5000, ZMax = 5000 };
            var st3 = Std(3000, 0, 4200, 6200, kg: 0);
            var st4 = Std(3000, 1000, 4200, 6200, kg: 0);
            var deck = new ScaffoldElement { Id = "D", Kind = ElementKind.Deck, Start = new Vec3(2000, 500, 5030), End = new Vec3(3000, 500, 5030), Width = 1000, ZMin = 5010, ZMax = 5060, WeightKg = 0 };
            els.AddRange(new[] { l1, l2, st3, st4, deck });
            var (input, baseEls) = Setup();
            foreach (var e in els) input.Candidates.Add(e);

            var session = new BeamLoadSession(Settings(), input);
            session.Options.LoadClass = 3;
            session.Recalculate();
            var r = session.Result;

            // A 1 m² deck at 2 kN/m² gives 2 kN. Only the two stands on the upper beam line (Y = 0) are
            // carried by it; the stands at Y = 1000 stand elsewhere and take their half themselves.
            Assert.Equal(1.0, r.Points.Sum(p => p.Qk), 3);
            Assert.Equal(0.5, r.Points.Sum(p => p.AreaM2), 3);
            Assert.Single(session.Topology.Levels);
        }
    }
}
