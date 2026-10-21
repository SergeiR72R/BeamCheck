using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Batch;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    /// <summary>
    /// A standard that runs through a beam (support under it) and goes on 10 m up with decks on every level: what the beam gets,
    /// depending on where the first joint above the beam is.
    /// </summary>
    public class ThroughStandardTests
    {
        private const double Tol = 0.002;

        private static double KN(double kg) => kg * 9.81 / 1000;

        /// <summary>
        /// Beam along X, top Z 0, underside -120. Support stack at x = 4000: standard from -2236 up to <paramref name="firstJoint"/>, then 2 m
        /// sections up to 10 m. A second stack 1 m beside it (y = 1000) the same, ledgers and a deck between them every 2 m above the beam.
        /// Another support (stack) at x = 0. Returns the load at the through standard (null: none).
        /// </summary>
        private static BeamEvent LoadAtThroughStandard(double firstJoint, out double stackKg)
        {
            var all = new List<ScaffoldElement>();
            int id = 0;
            ScaffoldElement E(ElementKind k, Vec3 a, Vec3 b, double w, double z0, double z1, double kg, string d)
            {
                var e = new ScaffoldElement { Id = "T" + (++id), Kind = k, Start = a, End = b, Width = w, ZMin = z0, ZMax = z1, WeightKg = kg, Description = d };
                all.Add(e);
                return e;
            }

            var beam = E(ElementKind.Beam, new Vec3(0, 0, -60), new Vec3(5000, 0, -60), 120, -120, 0, 100, "SRU U120");
            E(ElementKind.Standard, new Vec3(0, 0, -2836), new Vec3(0, 0, -2236), 48, -2836, -2236, 6, "Fußspindel UJB");
            E(ElementKind.Standard, new Vec3(0, 0, -2236), new Vec3(0, 0, -100), 48, -2236, -100, 10, "Vertikalstiel UVR 200");
            stackKg = 0;
            foreach (var y in new[] { 0.0, 1000 })
            {
                E(ElementKind.Standard, new Vec3(4000, y, -2836), new Vec3(4000, y, -2236), 48, -2836, -2236, 6, "Fußspindel UJB");
                E(ElementKind.Standard, new Vec3(4000, y, -2236), new Vec3(4000, y, firstJoint), 48, -2236, firstJoint, 10, "Vertikalstiel UVR 200");
                for (double z = firstJoint; z < 10000; z += 2000)
                {
                    E(ElementKind.Standard, new Vec3(4000, y, z), new Vec3(4000, y, z + 2000), 48, z, z + 2000, 10, "Vertikalstiel UVR 200");
                    if (y == 0) stackKg += 10;
                }
            }

            for (double z = 1000; z <= 9000; z += 2000)
            {
                E(ElementKind.Ledger, new Vec3(4000, 0, z - 30), new Vec3(4000, 1000, z - 30), 48, z - 60, z, 4, "Horizontalriegel UH 100 Plus");
                E(ElementKind.Ledger, new Vec3(5000, 0, z - 30), new Vec3(5000, 1000, z - 30), 48, z - 60, z, 4, "Horizontalriegel UH 100 Plus");
                E(ElementKind.Deck, new Vec3(4000, 500, z), new Vec3(5000, 500, z), 1000, z - 50, z, 10, "Stahlbelag UDG");
            }

            var s = new BeamCheckSettings { UseShapeRecognition = false };
            var r = BatchAnalyzer.AnalyzeBeam("B", beam, all, s, new BatchOptions { LoadClass = 3 });
            Assert.Contains(r.Supports, x => System.Math.Abs(x.Position - 4000) < 1);
            return r.Loads.FirstOrDefault(l => System.Math.Abs(l.Position - 4000) < 1);
        }

        [Theory]
        [InlineData(20)]     // the standard under the beam ends just above it, the next section starts there (B-005)
        [InlineData(400)]    // first joint 400 mm above the beam top
        public void JointNearTheBeam_TheWholeStackAboveIsListedAtTheSupport(double joint)
        {
            var c = LoadAtThroughStandard(joint, out double stackKg);
            Assert.NotNull(c);
            // every section above the joint (5 × 10 kg), half of each ledger (5 × 2 kg) and a quarter of each deck (5 × 2.5 kg) — but not the 2.2 m standard below it
            Assert.Equal(KN(stackKg + 5 * 2 + 5 * 2.5), c.Gk, 0.05);
            Assert.True(c.Qk > 0);
        }

        [Fact]
        public void OnePieceThroughTheBeam_FirstJointHigh_IsNotALoadOnTheBeam()
        {
            // the standard runs 1 m through and above the beam in one piece: the beam is clamped to it, the stack goes down the standard
            var c = LoadAtThroughStandard(1000, out _);
            Assert.Null(c);
        }
    }
}
