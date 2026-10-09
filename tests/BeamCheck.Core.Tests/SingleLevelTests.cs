using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Report;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    public class SingleLevelTests
    {
        /// <summary>Three levels; the middle one loses two of its three decks per bay so the top level is the largest.</summary>
        private static BeamLoadSession ThreeLevels(BeamCheckSettings settings = null)
        {
            var b = ScaffoldBuilder.Example(2000, 4000, 6000);
            var input = b.Input();
            foreach (var d in b.Elements.Where(e => e.Kind == Model.ElementKind.Deck && e.ZMin > 3000 && e.ZMin < 5000).Take(3))
                input.ExcludedIds.Add(d.Id);
            return new BeamLoadSession(settings ?? new BeamCheckSettings(), input);
        }

        [Fact]
        public void Default_mode_puts_live_load_on_one_level_only_the_one_with_the_largest_area()
        {
            var s = ThreeLevels();
            var f = s.Options.LevelFactors;

            Assert.Equal(LevelModes.SingleWorstLevel, s.Settings.LevelMode);
            Assert.Equal(1, f.Values.Count(v => v == 1.0));
            Assert.Equal(2, f.Values.Count(v => v == 0.0));
            // Levels 1 and 3 tie on area; the higher one wins.
            Assert.Equal(1.0, f[3]);
        }

        [Fact]
        public void Live_load_is_one_level_but_self_weight_counts_every_level()
        {
            var one = ThreeLevels();
            var all = ThreeLevels(new BeamCheckSettings { LevelMode = LevelModes.AllLevels });

            double qOne = one.Result.Points.Sum(p => p.Qk), qAll = all.Result.Points.Sum(p => p.Qk);
            Assert.True(qOne > 0 && qOne < qAll);
            Assert.Equal(all.Result.Points.Sum(p => p.Gk), one.Result.Points.Sum(p => p.Gk), 9);
            // q = 2 kN/m² on the governing level's area.
            Assert.Equal(2.0 * one.Result.Points.Sum(p => p.LoadedAreaM2), qOne, 6);
        }

        [Fact]
        public void Area_covers_all_levels_to_the_top_and_the_loaded_part_is_reported_separately()
        {
            var s = ThreeLevels();
            var p = s.Result.Points[1];

            Assert.Equal(p.Levels.Sum(l => l.AreaM2), p.AreaM2, 9);
            Assert.Equal(p.Levels.Single(l => l.Factor > 0).AreaM2, p.LoadedAreaM2, 9);
            Assert.True(p.AreaM2 > p.LoadedAreaM2);
        }

        [Fact]
        public void Table_lists_area_live_load_self_weight_and_total_load_with_a_summary()
        {
            var s = ThreeLevels();
            var t = ResultTable.Build(s.Result, ",");

            Assert.Equal(new[] { "Стойка", "X, мм", "Площадь, м²", "Live load, кН", "Self weight, кН", "Total load, кН" }, t.Header.Take(6));
            var sum = t.Rows.Last();
            Assert.Equal("Σ", sum[0]);
            Assert.Contains(t.Notes, n => n.StartsWith("Summary: площадь (все ярусы до верха) = ") && n.Contains("live load = ") && n.Contains("self weight load = ") && n.Contains("total load = "));

            var r = s.Result;
            string live = new NumberFormat(",").Num(r.Points.Sum(p => p.Qk), 2);
            string self = new NumberFormat(",").Num(r.Points.Sum(p => p.Gk), 2);
            string total = new NumberFormat(",").Num(r.Points.Sum(p => p.Fk), 2);
            Assert.Equal(new[] { live, self, total }, new[] { sum[3], sum[4], sum[5] });
            Assert.DoesNotContain("Total Fd, кН", t.Header);
        }

        [Fact]
        public void Design_value_column_appears_only_when_requested()
        {
            var s = ThreeLevels();
            s.Options.ShowDesignValues = true;
            s.Recalculate();

            Assert.Contains("Total Fd, кН", ResultTable.Build(s.Result, ",").Header);
        }
    }
}
