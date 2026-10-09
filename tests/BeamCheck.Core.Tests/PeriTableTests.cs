using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    /// <summary>
    /// Reproduces the "zusätzliche Stiellast infolge Verkehrslast q1" values of the PERI UP Tabellenbuch
    /// (05/2021, p. 42): V = 1.5 · q1 · b · L / 2 for an inner standard (two bays of length L along the
    /// beam, deck width b), one level 100 % and the adjacent level 50 %.
    /// </summary>
    public class PeriTableTests
    {
        private static double InnerStandLoad(double fieldLengthMm, double widthMm, int loadClass)
        {
            var b = ScaffoldBuilder.Custom(new[] { 1000.0, 1000.0 + fieldLengthMm, 1000.0 + 2 * fieldLengthMm }, widthMm, 2000, 4000);
            var session = new BeamLoadSession(new BeamCheckSettings(), b.Input());
            session.Options.LoadClass = loadClass;
            session.Recalculate();
            var middle = session.Result.Points.Single(p => System.Math.Abs(p.Position - (1000 + fieldLengthMm)) < 1);
            return middle.Qk;
        }

        [Theory]
        [InlineData(3000, 750, 3, 3.38)]   // Flex 75, LC3, 3.00 m
        [InlineData(3000, 750, 4, 5.06)]   // LC4
        [InlineData(2500, 750, 3, 2.81)]   // 2.50 m
        [InlineData(2500, 750, 4, 4.22)]
        [InlineData(3000, 1000, 3, 4.50)]  // Flex 100
        public void Inner_standard_live_load_matches_PERI_table(double fieldMm, double widthMm, int lc, double expectedKn)
        {
            Assert.Equal(expectedKn, InnerStandLoad(fieldMm, widthMm, lc), 2);
        }

        [Fact]
        public void LC5_and_LC6_follow_the_same_formula()
        {
            // The table prints 6.75 / 8.44 kN for LC5 and 9.00 / 11.25 kN for LC6 (fields 2.0 / 2.5 / 3.0 m, b = 1.00 m).
            Assert.Equal(6.75, InnerStandLoad(2000, 1000, 5), 2);
            Assert.Equal(8.44, InnerStandLoad(2500, 1000, 5), 2);
            Assert.Equal(9.00, InnerStandLoad(2000, 1000, 6), 2);
            Assert.Equal(11.25, InnerStandLoad(2500, 1000, 6), 2);
        }
    }
}
