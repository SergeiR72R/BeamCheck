using System.Collections.Generic;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Recognition;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    /// <summary>Shapes as PERI CAD 24 draws them in the "_Line" display blocks (mm).</summary>
    public class ShapeAnalyzerTests
    {
        private static readonly ShapeAnalyzer Shape = new ShapeAnalyzer(new BeamCheckSettings());

        private static Segment S(double x1, double y1, double z1, double x2, double y2, double z2) =>
            new Segment(new Vec3(x1, y1, z1), new Vec3(x2, y2, z2));

        [Fact]
        public void Vertical_line_is_a_standard()
        {
            var segs = new List<Segment> { S(76, 76, 0, 76, 76, 2160) };
            Assert.Equal(ElementKind.Standard, Shape.Guess(segs));
            var e = Shape.Build(segs, ElementKind.Standard);
            Assert.Equal(76, e.Start.X);
            Assert.Equal(0, e.ZMin);
            Assert.Equal(2160, e.ZMax);
        }

        [Fact]
        public void Bar_with_hooks_is_a_ledger_and_axis_is_the_bar()
        {
            // UH ledger 0.75 m: bar 666 mm at +30, wedge hooks down to -60.
            var segs = new List<Segment> { S(0, 15, 30, 666, 15, 30), S(0, 15, 30, 0, 15, -60), S(666, 15, 30, 666, 15, -60) };
            Assert.Equal(ElementKind.Ledger, Shape.Guess(segs));
            var e = Shape.Build(segs, ElementKind.Ledger);
            Assert.Equal(666, e.PlanLength, 3);
            Assert.Equal(30, e.Start.Z);
        }

        [Fact]
        public void Flat_rectangle_is_a_deck_spanning_its_long_side()
        {
            // 0.25 x 1.25 deck outline: 245 x 1200.
            var segs = new List<Segment> { S(0, 0, 0, 0, 1200, 0), S(0, 1200, 0, 245, 1200, 0), S(245, 1200, 0, 245, 0, 0), S(245, 0, 0, 0, 0, 0) };
            Assert.Equal(ElementKind.Deck, Shape.Guess(segs));
            var e = Shape.Build(segs, ElementKind.Deck);
            Assert.Equal(1200, e.PlanLength, 3);
            Assert.Equal(245, e.Width, 3);
            Assert.Equal(122.5, e.Start.X, 3);
            Assert.Equal(0.294, e.AreaM2, 3);
        }

        [Fact]
        public void Long_inclined_line_is_a_diagonal_and_small_parts_are_ignored()
        {
            Assert.Equal(ElementKind.Diagonal, Shape.Guess(new List<Segment> { S(0, 0, 0, 2000, 0, 2000) }));
            Assert.Equal(ElementKind.Unknown, Shape.Guess(new List<Segment> { S(0, 0, 0, 83, 76, 90) }));
        }

        [Fact]
        public void Box_edges_of_a_rotated_beam_give_its_true_axis()
        {
            // 4472 x 200 x 200 beam rotated 30° in plan, given as 8 corners.
            var dir = new Vec3(0.8660254, 0.5, 0);
            var perp = new Vec3(-0.5, 0.8660254, 0);
            var corners = new List<Vec3>();
            for (int i = 0; i < 8; i++)
                corners.Add(dir * ((i & 1) == 0 ? 0 : 4472) + perp * ((i & 2) == 0 ? -100 : 100) + new Vec3(0, 0, (i & 4) == 0 ? -200 : 0));
            var e = Shape.Build(ShapeAnalyzer.BoxEdges(corners), ElementKind.Beam);
            Assert.Equal(4472, e.PlanLength, 3);
            Assert.Equal(200, e.Width, 3);
            Assert.Equal(0, e.ZMax, 6);
        }
    }
}
