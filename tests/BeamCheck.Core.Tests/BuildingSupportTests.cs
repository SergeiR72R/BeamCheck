using System;
using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;
using BeamCheck.Core.Spindles;
using Xunit;

namespace BeamCheck.Core.Tests
{
    /// <summary>Supports from the building model (layer BUILDING STRUCTURE): steel beams, foundations, pedestals.</summary>
    public class BuildingSupportTests
    {
        private const double Tol = 0.002;

        private static double KN(double kg) => kg * 9.81 / 1000;

        /// <summary>The corners of a box, as the faces of an IFC block give them.</summary>
        private static List<Vec3> Box(double x0, double y0, double x1, double y1, double z0, double z1, double angleDeg = 0)
        {
            double a = angleDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            var pts = new List<Vec3>();
            foreach (var x in new[] { x0, x1 })
                foreach (var y in new[] { y0, y1 })
                    foreach (var z in new[] { z0, z1 })
                        pts.Add(new Vec3(cx + (x - cx) * c - (y - cy) * s, cy + (x - cx) * s + (y - cy) * c, z));
            return pts;
        }

        [Fact]
        public void Outline_IsTheRealRectangle_AlsoRotated()
        {
            // IPE 200 from the PERIDUMP: 100 wide, 528 long, Z 101550..101750
            var ipe = BuildingSupports.FromPoints(Box(1170980, 1388217, 1171080, 1388745, 101550, 101750), "8EA09", "IfcBeam_ IPE200 - 10297");
            Assert.Equal(ElementKind.BuildingSupport, ipe.Kind);
            Assert.Equal(100, ipe.Width, 1);
            Assert.Equal(528, ipe.PlanLength, 1);
            Assert.Equal(101750, ipe.ZMax, 1);
            Assert.Equal(0, ipe.WeightKg);

            var rotated = BuildingSupports.FromPoints(Box(0, 0, 6000, 200, 0, 400, 30), "R", "HEB 200");
            Assert.Equal(200, rotated.Width, 1);
            Assert.Equal(6000, rotated.PlanLength, 1);
        }

        [Fact]
        public void Bearing_OuterSupportsAtTheInnerEdge_InnerSupportsAtTheMiddle()
        {
            var s = new BeamCheckSettings();
            // scaffold beam along X, underside at Z 1000, 6 m long
            var beam = new ScaffoldElement { Id = "B", Kind = ElementKind.Beam, Start = new Vec3(0, 0, 1000), End = new Vec3(6000, 0, 1000), Width = 100, ZMin = 1000, ZMax = 1200 };
            // foundation under the left end (x -500..400), a steel beam across the middle (x 2950..3050), a pedestal under the right end (x 5500..6500);
            // tops 250 mm under the beam (mesh grating in between)
            var parts = new List<ScaffoldElement>
            {
                BuildingSupports.FromPoints(Box(-500, -1000, 400, 1000, 0, 750), "F", "Fundament"),
                BuildingSupports.FromPoints(Box(2950, -3000, 3050, 3000, 550, 750), "S", "IPE200"),
                BuildingSupports.FromPoints(Box(5500, -1000, 6500, 1000, 0, 750), "P", "Pedestal"),
                // 400 mm under the beam: too deep, not a support
                BuildingSupports.FromPoints(Box(4000, -1000, 4500, 1000, 0, 600), "D", "deep"),
                // 1 m beside the beam: not under it
                BuildingSupports.FromPoints(Box(1000, 500, 2000, 1500, 0, 750), "N", "beside"),
            };
            var under = BuildingSupports.Under(beam, parts, s);
            Assert.Equal(new[] { "F", "S", "P" }, under.OrderBy(b => b.Middle).Select(b => b.Part.Id));

            var f = under.Single(b => b.Part.Id == "F");
            Assert.Equal(0, f.From, 1);
            Assert.Equal(400, f.To, 1);
            Assert.Equal(400, BuildingSupports.SupportPoint(f, 6000, true), 1);       // inner edge
            var st = under.Single(b => b.Part.Id == "S");
            Assert.Equal(3000, BuildingSupports.SupportPoint(st, 6000, false), 1);    // middle of the flange
            var p = under.Single(b => b.Part.Id == "P");
            Assert.Equal(5500, BuildingSupports.SupportPoint(p, 6000, true), 1);      // inner edge

            // the analyzer puts them in this way (BEAMBATCH supports)
            var sup = new ScaffoldAnalyzer(s).FindSupports(beam, new List<ScaffoldElement>(), parts);
            Assert.Equal(new[] { 400.0, 3000, 5500 }, sup.Select(x => Math.Round(x.Position)));
            Assert.All(sup, x => Assert.NotNull(x.Building));
        }

        private sealed class Model
        {
            private int _h;
            public readonly ScanResult Scan = new ScanResult();

            public ScaffoldElement Add(ScaffoldElement e, bool spindle = false)
            {
                e.Id = e.Id ?? "E" + (++_h);
                Scan.Add(e, spindle, false);
                return e;
            }

            public ScaffoldElement V(double x, double z0, double z1, double kg, string d, bool spindle = false) =>
                Add(new ScaffoldElement { Kind = ElementKind.Standard, Start = new Vec3(x, 0, z0), End = new Vec3(x, 0, z1), Width = 48, ZMin = z0, ZMax = z1, WeightKg = kg, Description = d }, spindle);
        }

        [Fact]
        public void SpindleUnderABeamThatAlsoLiesOnTheBuilding_GetsOnlyItsLeverShare()
        {
            // RCS beam 0..4000 at Z 3000..3200: on spindle A (x 0) and on a steel beam of the building under x 3900..4000 (top 200 mm lower);
            // a stack (26 kg) stands on the RCS beam at x 1000
            var m = new Model();
            var a = m.V(0, 0, 600, 6, "Fußspindel UJB", true);
            m.V(0, 600, 3000, 20, "Vertikalstiel UVR");
            m.Add(new ScaffoldElement { Kind = ElementKind.Beam, Start = new Vec3(0, 0, 3000), End = new Vec3(4000, 0, 3000), Width = 100, ZMin = 3000, ZMax = 3200, WeightKg = 0, Description = "RCS Schiene" });
            m.V(1000, 3200, 3800, 6, "Fußspindel UJB", true);
            m.V(1000, 3800, 6000, 20, "Vertikalstiel UVR");
            var steel = m.Add(BuildingSupports.FromPoints(Box(3900, -2000, 4000, 2000, 2600, 2800), "STEEL", "IPE200"));

            var bs = new BeamCheckSettings { UseShapeRecognition = false };
            var ss = new SpindleSettings();
            ss.Normalize();
            var r = new SpindleEngine(m.Scan, ss, bs, 3).Run(new Sidecar());
            var sa = r.Spindles.Single(x => x.Key == a.Id);
            // supports 0 and 3900 (inner edge of the steel): A gets (3900 - 1000) / 3900 of the stack
            Assert.Equal(KN(26 + 26 * 2900.0 / 3900), sa.DeadKn, Tol);
            Assert.Contains(sa.Info, w => w.Contains("Bauwerk"));
            // the building weighs nothing in the model check
            Assert.Equal(KN(26 + 26), r.ModelWeightKn, Tol);

            // without the building part A carries the whole stack (only support)
            m.Scan.Elements.Remove(steel);
            r = new SpindleEngine(m.Scan, ss, bs, 3).Run(new Sidecar());
            Assert.Equal(KN(26 + 26), r.Spindles.Single(x => x.Key == a.Id).DeadKn, Tol);
        }

        [Fact]
        public void StackStandingOnTheBuilding_IsNotMovedIntoAHeadSpindleNextToIt()
        {
            var m = new Model();
            m.V(0, 0, 600, 6, "Fußspindel UJB", true);
            var head = m.V(0, 600, 1200, 6, "Kopfspindel UJB", true);
            // a stack 400 mm beside the head, standing on a foundation at Z 1200
            m.Add(BuildingSupports.FromPoints(Box(200, -500, 800, 500, 0, 1200), "FDN", "Fundament"));
            m.V(400, 1200, 1800, 6, "Fußspindel UJB", true);
            m.V(400, 1800, 3800, 20, "Vertikalstiel UVR");
            var ss = new SpindleSettings();
            ss.Normalize();
            var r = new SpindleEngine(m.Scan, ss, new BeamCheckSettings { UseShapeRecognition = false }, 3).Run(new Sidecar());
            Assert.Equal(KN(6), r.Spindles.Single(x => x.Key == head.Id).DeadKn, Tol);
        }

        [Fact]
        public void UvaAndPins_GetARowOfTheirOwn_WithTheirName()
        {
            var m = new Model();
            var sp = m.V(0, 0, 600, 6, "Fußspindel UJB 38-50/30", true);
            var uva = m.Add(new ScaffoldElement { Kind = ElementKind.Accessory, Article = "136779", Description = "Anfangsstück UVA 49", Start = new Vec3(0, 0, 500), End = new Vec3(0, 0, 900), Width = 60, ZMin = 500, ZMax = 900, WeightKg = 2 });
            m.V(0, 650, 2650, 20, "Vertikalstiel UVR 200");
            var ss = new SpindleSettings();
            ss.Normalize();
            var rows = ss.PartRows(m.Scan);
            Assert.Equal(new[] { uva.Id }, rows);

            var eng = new SpindleEngine(m.Scan, ss, new BeamCheckSettings { UseShapeRecognition = false }, 3) { TargetIds = new HashSet<string>(rows), TargetsArePicked = false };
            var r = eng.Run(new Sidecar());
            var row = r.Spindles.Single(x => x.Key == uva.Id);
            Assert.Equal("part", row.Type);
            Assert.Contains("UVA", row.Core.Label);
            Assert.Contains("136779", row.Core.Label);
            Assert.Equal(KN(20), row.DeadKn, 0.03);
            Assert.True(row.DeadKn < r.Spindles.Single(x => x.Key == sp.Id).DeadKn);
            // a part row is not a bottom spindle
            Assert.Equal(r.Spindles.Single(x => x.Key == sp.Id).DeadKn, r.BaseSpindlesKn, Tol);
        }
    }
}
