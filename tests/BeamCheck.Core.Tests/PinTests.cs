using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    /// <summary>A stack standing on a pin (UH Zapfen) in the middle of a ledger puts its whole load on the ledger; the ledger gives it to its ends by the lever rule.</summary>
    public class PinTests
    {
        private int _n;

        private ScaffoldElement Std(double x, double z0, double z1, double kg) => new ScaffoldElement
        {
            Id = "S" + (++_n), Kind = ElementKind.Standard, Start = new Vec3(x, 0, z0), End = new Vec3(x, 0, z1), ZMin = z0, ZMax = z1, WeightKg = kg,
        };

        private BeamLoadSession Run(double pinX, bool withPin = true)
        {
            var beam = new ScaffoldElement
            {
                Id = "MAIN", Kind = ElementKind.Beam, Start = new Vec3(-500, 0, -100), End = new Vec3(4500, 0, -100), Width = 200, ZMin = -200, ZMax = 0,
            };
            var els = new List<ScaffoldElement>
            {
                Std(0, 0, 2000, 10), Std(0, 2000, 4000, 10), Std(4000, 0, 2000, 10), Std(4000, 0 + 2000, 4000, 10),
                new ScaffoldElement { Id = "L", Kind = ElementKind.Ledger, Description = "UH 400", Start = new Vec3(0, 0, 2000), End = new Vec3(4000, 0, 2000), Width = 50, ZMin = 1975, ZMax = 2025, WeightKg = 0 },
                Std(pinX, 2250, 3250, 40),
            };
            if (withPin)
                els.Add(new ScaffoldElement { Id = "PIN", Kind = ElementKind.Accessory, Article = "130681", Description = "UH Zapfen", Start = new Vec3(pinX, 21, 2086), End = new Vec3(pinX, -21, 2086), Width = 40, ZMin = 1903, ZMax = 2270, WeightKg = 0 });
            var selected = els.Where(e => e.Kind == ElementKind.Standard && e.ZMin == 0).ToList();
            return new BeamLoadSession(new BeamCheckSettings { LevelMode = LevelModes.AllLevels }, new AnalysisInput { Beam = beam, SelectedStandards = selected, Candidates = els });
        }

        [Fact]
        public void The_whole_load_of_a_stack_on_a_pin_goes_to_the_ledger_ends_in_proportion_to_the_pin_position()
        {
            var r = Run(1000).Result;
            Assert.Equal(2, r.Points.Count);
            // columns: 20 kg each + the pin stack (40 kg) split 75 % / 25 % by the lever rule
            Assert.Equal(LoadCalculator.KgToKn(20 + 30), r.Points[0].Gk, 6);
            Assert.Equal(LoadCalculator.KgToKn(20 + 10), r.Points[1].Gk, 6);
            Assert.Equal(LoadCalculator.KgToKn(40), r.Points.Sum(p => p.TransferGk), 6);
        }

        [Fact]
        public void A_beam_on_a_stack_that_stands_on_a_pin_is_found_and_passes_its_stands_down_through_the_ledger()
        {
            var beam = new ScaffoldElement
            {
                Id = "MAIN", Kind = ElementKind.Beam, Start = new Vec3(-500, 0, -100), End = new Vec3(4500, 0, -100), Width = 200, ZMin = -200, ZMax = 0,
            };
            var els = new List<ScaffoldElement>
            {
                Std(0, 0, 2000, 10), Std(0, 2000, 4000, 10), Std(4000, 0, 2000, 10), Std(4000, 2000, 4000, 10),
                new ScaffoldElement { Id = "L", Kind = ElementKind.Ledger, Description = "UH 400", Start = new Vec3(0, 0, 2000), End = new Vec3(4000, 0, 2000), Width = 50, ZMin = 1975, ZMax = 2025, WeightKg = 0 },
                Std(1000, 2250, 4250, 40),
                new ScaffoldElement { Id = "PIN", Kind = ElementKind.Accessory, Article = "130681", Description = "UH Zapfen", Start = new Vec3(1000, 21, 2086), End = new Vec3(1000, -21, 2086), Width = 40, ZMin = 1903, ZMax = 2270, WeightKg = 0 },
                // a beam on top of the pin stack, and a stand on that beam
                new ScaffoldElement { Id = "UB", Kind = ElementKind.Beam, Description = "UB", Start = new Vec3(0, 0, 4350), End = new Vec3(2000, 0, 4350), Width = 200, ZMin = 4250, ZMax = 4450, WeightKg = 20 },
                Std(1500, 4450, 6450, 100),
            };
            var selected = els.Where(e => e.Kind == ElementKind.Standard && e.ZMin == 0).ToList();
            var s = new BeamLoadSession(new BeamCheckSettings { LevelMode = LevelModes.AllLevels }, new AnalysisInput { Beam = beam, SelectedStandards = selected, Candidates = els });
            var r = s.Result;
            // pin stack 40 + beam 20 + stand 100 = 160 kg on the ledger at 1000 mm: 75 % to the left end, 25 % to the right end
            Assert.Equal(LoadCalculator.KgToKn(20 + 0.75 * 160), r.Points[0].Gk, 6);
            Assert.Equal(LoadCalculator.KgToKn(20 + 0.25 * 160), r.Points[1].Gk, 6);
            Assert.Equal(2, s.Topology.Transfers.Select(t => t.Link).Distinct().Count());
        }

        [Fact]
        public void Without_a_pin_the_stack_carries_nothing_to_the_ledger()
        {
            var r = Run(1000, withPin: false).Result;
            Assert.Equal(LoadCalculator.KgToKn(20), r.Points[0].Gk, 6);
            Assert.Equal(LoadCalculator.KgToKn(20), r.Points[1].Gk, 6);
        }

        [Fact]
        public void The_calculation_path_explains_the_pin_ledger_and_adds_up()
        {
            var s = Run(1000);
            var lines = BeamCheck.Core.Report.CalculationBreakdown.Build(s);
            Assert.Contains(lines, l => l.StartsWith("Riegel mit Zapfen "));
            Assert.Contains(lines, l => l.Contains("75,0 %") && l.Contains("25,0 %"));
            var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            int at = lines.FindIndex(l => l.StartsWith("Prüfsumme"));
            Assert.True(at > 0);
            foreach (var p in s.Result.Points)
            {
                var row = lines.Skip(at).First(l => l.StartsWith(p.Name + ";")).Split(';');
                Assert.Equal(p.Gk, double.Parse(row[1], de), 2);
                Assert.Equal(p.Qk, double.Parse(row[2], de), 2);
            }
        }

        [Fact]
        public void The_transfer_is_recorded_for_the_calculation_path()
        {
            var s = Run(1000);
            var t = Assert.Single(s.Topology.Transfers);
            Assert.True(t.Link.IsPinLedger);
            Assert.Equal(0.75, t.Fractions[0], 6);
            Assert.Equal(0.25, t.Fractions[1], 6);
        }
    }
}
