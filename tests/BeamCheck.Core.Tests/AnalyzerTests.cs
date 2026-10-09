using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    public class AnalyzerTests
    {
        private static readonly BeamCheckSettings AllLevels = new BeamCheckSettings { LevelMode = LevelModes.AllLevels };

        private static BeamLoadResult Run(ScaffoldBuilder b, BeamCheckSettings s, int loadClass = 3, AnalysisInput input = null)
        {
            var session = new BeamLoadSession(s, input ?? b.Input()) ;
            session.Options.LoadClass = loadClass;
            session.Recalculate();
            return session.Result;
        }

        [Fact]
        public void Positions_and_dimension_chain_match_example()
        {
            var r = Run(ScaffoldBuilder.Example(2000, 4000), AllLevels);

            Assert.Equal(new[] { "S1", "S2", "S3" }, r.Points.Select(p => p.Name));
            Assert.Equal(new[] { 1423.0, 2423.0, 3923.0 }, r.Points.Select(p => p.Position));
            Assert.Equal(new[] { 1423.0, 1000.0, 1500.0, 549.0 }, r.DimensionChain());
            Assert.DoesNotContain(r.Warnings, w => w.Contains("не опирается"));
        }

        [Fact]
        public void Deck_area_is_distributed_a_quarter_per_corner_standard()
        {
            var r = Run(ScaffoldBuilder.Example(2000, 4000), AllLevels);

            // Bay 1: 1.000 x 0.732 m, bay 2: 1.500 x 0.732 m; each beam standard gets 1/4 of adjacent bays per level.
            Assert.Equal(2 * 0.183, r.Points[0].AreaM2, 6);
            Assert.Equal(2 * (0.183 + 0.2745), r.Points[1].AreaM2, 6);
            Assert.Equal(2 * 0.2745, r.Points[2].AreaM2, 6);
            Assert.All(r.Points, p => Assert.Equal(2, p.Levels.Count));
        }

        [Fact]
        public void Service_load_uses_load_class()
        {
            var r = Run(ScaffoldBuilder.Example(2000, 4000), AllLevels, loadClass: 3);

            Assert.Equal(2.0, r.ServiceLoad);
            Assert.Equal(2.0 * 2 * (0.183 + 0.2745), r.Points[1].Qk, 6);
        }

        [Fact]
        public void Self_weight_includes_standards_ledgers_and_decks()
        {
            var r = Run(ScaffoldBuilder.Example(2000, 4000), AllLevels);

            // S1: 3 sections x 10 kg + per level (½ long ledger 5 kg + ½ transverse 4 kg + ¾ deck-equivalents x 5 kg) x 2.
            double kg = 30 + 2 * (2.5 + 2 + 0.75 * 5);
            Assert.Equal(LoadCalculator.KgToKn(kg), r.Points[0].Gk, 6);
            Assert.Equal(1.35 * r.Points[0].Gk + 1.5 * r.Points[0].Qk, r.Points[0].Fd, 9);
        }

        [Fact]
        public void One_selected_section_pulls_in_the_whole_column()
        {
            var b = ScaffoldBuilder.Example(2000, 4000);
            var topSections = b.BeamRowStandards.GroupBy(s => s.Start.X).Select(g => g.OrderBy(s => s.ZMin).Last());
            var r = Run(b, AllLevels, input: b.Input(topSections));

            Assert.Equal(3, r.Points.Count);
            Assert.All(r.Points, p => Assert.Equal(3, p.Column.Sections.Count));
        }

        [Fact]
        public void Ledgers_modelled_short_of_the_standard_axis_are_still_connected()
        {
            var b = ScaffoldBuilderWithGap(60);
            var r = Run(b, AllLevels);

            Assert.Equal(0.183, r.Points[0].AreaM2, 3);
        }

        private static ScaffoldBuilder ScaffoldBuilderWithGap(double gap)
        {
            var b = ScaffoldBuilder.Example();
            b.LedgerGap = gap;
            b.AddLevel(2000);
            return b;
        }

        [Fact]
        public void Worst_level_gets_full_load_and_neighbours_half()
        {
            var b = ScaffoldBuilder.Example(2000, 4000, 6000);
            var session = new BeamLoadSession(new BeamCheckSettings(), b.Input());

            // All levels are equal: the top-most one is chosen as governing.
            Assert.Equal(0.0, session.Options.LevelFactors[1]);
            Assert.Equal(0.5, session.Options.LevelFactors[2]);
            Assert.Equal(1.0, session.Options.LevelFactors[3]);

            session.SetLevelFactor(1, 1.0);
            Assert.Equal(2.0 * 0.183 * (1.0 + 0.5 + 1.0), session.Result.Points[0].Qk, 6);
        }

        [Fact]
        public void Excluding_decks_removes_their_area()
        {
            var b = ScaffoldBuilder.Example(2000, 4000);
            var session = new BeamLoadSession(AllLevels, b.Input());
            var upperDecks = b.Elements.Where(e => e.Kind == Model.ElementKind.Deck && e.ZMin > 3000).Select(e => e.Id);

            session.Exclude(upperDecks);

            Assert.Single(session.Topology.Levels);
            Assert.Equal(0.183, session.Result.Points[0].AreaM2, 6);
        }

        [Fact]
        public void Standard_off_the_beam_is_reported()
        {
            var b = ScaffoldBuilder.Example(2000);
            var inner = b.Elements.Where(e => e.Kind == Model.ElementKind.Standard && e.Start.Y > 500 && e.Start.X == 1423);
            var r = Run(b, AllLevels, input: b.Input(b.BeamRowStandards.Concat(inner)));

            Assert.Contains(r.Warnings, w => w.Contains("не опирается"));
        }

        [Fact]
        public void Flipping_measures_from_the_other_end()
        {
            var b = ScaffoldBuilder.Example(2000);
            var session = new BeamLoadSession(AllLevels, b.Input());
            session.Options.FlipBeam = true;
            session.Recalculate();

            Assert.Equal(new[] { 549.0, 2049.0, 3049.0 }, session.Result.Points.Select(p => p.Position));
            Assert.Equal(new[] { 549.0, 1500.0, 1000.0, 1423.0 }, session.Result.DimensionChain());
        }

        [Fact]
        public void Missing_deck_reduces_only_the_adjacent_standards()
        {
            var b = ScaffoldBuilder.Example(2000);
            var bay2Deck = b.Elements.First(e => e.Kind == Model.ElementKind.Deck && e.Start.X == 2423);
            var input = b.Input();
            input.ExcludedIds.Add(bay2Deck.Id);
            var r = Run(b, AllLevels, input: input);

            Assert.Equal(0.183, r.Points[0].AreaM2, 6);
            Assert.True(r.Points[1].AreaM2 < 0.183 + 0.2745);
            Assert.True(r.Points[2].AreaM2 < 0.2745);
        }
    }
}

namespace BeamCheck.Core.Tests
{
    public class LevelRuleTests
    {
        [Fact]
        public void Only_one_neighbour_gets_fifty_percent_the_more_loaded_one()
        {
            // 3 equal levels would tie; remove half of the lowest level so the neighbour choice matters.
            var b = ScaffoldBuilder.Example(2000, 4000, 6000);
            var input = b.Input();
            var lowDecks = b.Elements.Where(e => e.Kind == Model.ElementKind.Deck && e.ZMin < 3000).ToList();
            input.ExcludedIds.Add(lowDecks[0].Id);
            input.ExcludedIds.Add(lowDecks[1].Id);
            input.ExcludedIds.Add(lowDecks[2].Id);

            var session = new BeamLoadSession(new BeamCheckSettings(), input);
            var f = session.Options.LevelFactors;

            Assert.Equal(3, f.Count);
            Assert.Equal(1, f.Values.Count(v => v == 1.0));
            Assert.Equal(1, f.Values.Count(v => v == 0.5));
            Assert.Equal(1, f.Values.Count(v => v == 0.0));
            // Two equal upper levels: the top one governs, the middle one is the neighbour, the weaker lowest level is unloaded.
            Assert.Equal(0.0, f[1]);
            Assert.Equal(0.5, f[2]);
            Assert.Equal(1.0, f[3]);
        }
    }
}
