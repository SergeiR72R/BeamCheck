using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    public class LooseDeckTests
    {
        private static double TotalArea(double raise, out Topology topo, double planShift = 0)
        {
            var b = ScaffoldBuilder.Example(2000);
            foreach (var d in b.Elements.Where(e => e.Kind == ElementKind.Deck))
            {
                d.ZMin += raise;
                d.ZMax += raise;
                d.Start = new Vec3(d.Start.X, d.Start.Y + planShift, d.Start.Z + raise);
                d.End = new Vec3(d.End.X, d.End.Y + planShift, d.End.Z + raise);
            }

            var s = new BeamLoadSession(new BeamCheckSettings(), b.Input());
            topo = s.Topology;
            return s.Result.Points.Sum(p => p.AreaM2);
        }

        [Fact]
        public void A_deck_resting_exactly_is_matched_without_warnings()
        {
            double a = TotalArea(0, out var t);
            Assert.Equal(0.183 + (0.183 + 0.2745) + 0.2745, a, 6);
            Assert.DoesNotContain(t.Warnings, w => w.Contains("увеличенным допуском"));
        }

        [Fact]
        public void A_deck_floating_half_a_metre_above_the_ledger_is_still_found_in_the_relaxed_pass()
        {
            double exact = TotalArea(0, out _);
            double loose = TotalArea(600, out var t);

            Assert.Equal(exact, loose, 6);
            Assert.Contains(t.Warnings, w => w.Contains("увеличенным допуском"));
        }

        [Fact]
        public void A_deck_that_is_too_far_from_any_ledger_is_reported_with_the_distances()
        {
            double a = TotalArea(1500, out var t);

            Assert.Equal(0, a, 9);
            var w = t.Warnings.Single(x => x.Contains("без леджера под опорой"));
            Assert.Contains("/", w);
            Assert.Contains("мм", w);
        }
    }
}
