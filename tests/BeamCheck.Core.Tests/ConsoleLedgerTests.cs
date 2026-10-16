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
    /// Zeichnung2.dwg: a standard (UVR, Z 6840..9000) with a console (UCM 50) whose root is on it and a UHV ledger whose end is 42 mm short of the
    /// console tip; no stand on the console. A second ledger can hang on a coupler in the middle of the first one.
    /// </summary>
    public class ConsoleLedgerTests
    {
        private const double Y = -14914;

        private BeamLoadSession Run(bool withCrossLedger, double crossEndOffset = 40, BeamCheckSettings s = null)
        {
            var beam = new ScaffoldElement
            {
                Id = "MAIN", Kind = ElementKind.Beam, Start = new Vec3(-500, Y, 6740), End = new Vec3(2000, Y, 6740), Width = 82, ZMin = 6640, ZMax = 6840,
            };
            var std = new ScaffoldElement { Id = "STD", Kind = ElementKind.Standard, Start = new Vec3(895, Y, 6840), End = new Vec3(895, Y, 9000), ZMin = 6840, ZMax = 9000, WeightKg = 10 };
            var console = new ScaffoldElement
            {
                Id = "CON", Kind = ElementKind.Console, Description = "UCM 50", Start = new Vec3(395, Y, 8340), End = new Vec3(895, Y, 8340), ZMin = 7852, ZMax = 8340, WeightKg = 5,
            };
            var ledger = new ScaffoldElement
            {
                Id = "LED", Kind = ElementKind.Ledger, Description = "UHV", Start = new Vec3(-2063, Y, 8175), End = new Vec3(353, Y, 8175), ZMin = 8100, ZMax = 8250, WeightKg = 20,
            };
            var all = new List<ScaffoldElement> { std, console, ledger };
            if (withCrossLedger)
                all.Add(new ScaffoldElement
                {
                    Id = "CROSS", Kind = ElementKind.Ledger, Description = "UHV", Start = new Vec3(-1000, Y + crossEndOffset, 8175), End = new Vec3(-1000, Y + 2500, 8175), ZMin = 8100, ZMax = 8250, WeightKg = 20,
                });
            return new BeamLoadSession(s ?? new BeamCheckSettings(), new AnalysisInput { Beam = beam, SelectedStandards = new List<ScaffoldElement> { std }, Candidates = all });
        }

        [Fact]
        public void A_ledger_on_a_console_without_a_stand_passes_half_its_weight_to_the_console_and_the_standard()
        {
            var r = Run(false).Result;
            // standard 10 + console 5 + half of the ledger 20 (its other end hangs free)
            Assert.Equal(LoadCalculator.KgToKn(10 + 5 + 10), r.Points.Single().Gk, 6);
        }

        [Fact]
        public void The_ledger_end_is_taken_up_to_5_cm_from_the_console_tip()
        {
            var tight = new BeamCheckSettings { ConsoleLedgerTolerance = 10 };
            Assert.Equal(LoadCalculator.KgToKn(10 + 5), Run(false, 40, tight).Result.Points.Single().Gk, 6);
            Assert.Equal(LoadCalculator.KgToKn(10 + 5 + 10), Run(false).Result.Points.Single().Gk, 6);
        }

        [Fact]
        public void A_ledger_on_a_coupler_in_the_middle_of_that_ledger_passes_its_half_weight_proportionally_through_the_console()
        {
            // the cross ledger hangs on LED at 1063 of 2416 mm from the free end: the console side takes 1063/2416 of its half (10 kg)
            var r = Run(true).Result;
            double t = 1063.0 / 2416.0;
            Assert.Equal(LoadCalculator.KgToKn(10 + 5 + 10 + 10 * t), r.Points.Single().Gk, 4);
        }

        [Fact]
        public void A_stack_on_a_pin_in_a_ledger_that_hangs_on_a_console_goes_through_the_ledger_and_the_console_to_the_standard()
        {
            var r = Run(false);
            var input = r.Input;
            var all = input.Candidates.ToList();
            all.Add(new ScaffoldElement { Id = "PIN", Kind = ElementKind.Accessory, Article = "130681", Description = "UH Zapfen", Start = new Vec3(-1000, Y + 21, 8260), End = new Vec3(-1000, Y - 21, 8260), Width = 40, ZMin = 8050, ZMax = 8420, WeightKg = 0 });
            all.Add(new ScaffoldElement { Id = "PSTK", Kind = ElementKind.Standard, Start = new Vec3(-1000, Y, 8350), End = new Vec3(-1000, Y, 10350), ZMin = 8350, ZMax = 10350, WeightKg = 40 });
            input.Candidates = all;
            var s = new BeamLoadSession(new BeamCheckSettings(), input);
            double t = 1063.0 / 2416.0;
            Assert.Equal(LoadCalculator.KgToKn(10 + 5 + 10 + 40 * t), s.Result.Points.Single().Gk, 4);
        }

        [Fact]
        public void A_ledger_end_further_than_5_cm_from_the_supporting_ledger_is_not_connected()
        {
            var r = Run(true, crossEndOffset: 80).Result;
            Assert.Equal(LoadCalculator.KgToKn(10 + 5 + 10), r.Points.Single().Gk, 6);
        }

        [Theory]
        [InlineData("ECM 100", "130365")]
        [InlineData("ECM 75", "136918")]
        [InlineData("ECM 50", "136923")]
        [InlineData("UCM 75", "112693")]
        [InlineData("UCM 50-2", "110483")]
        public void Console_brackets_are_recognised_by_name(string name, string article)
        {
            var sig = new BeamCheck.Core.Recognition.ElementSignature { Layer = "0" };
            sig.Add("PART:ArtNr", article);
            sig.Add("PART:Name", name);
            Assert.Equal(ElementKind.Console, new BeamCheck.Core.Recognition.ElementClassifier(new BeamCheckSettings()).Classify(sig).Kind);
        }
    }
}
