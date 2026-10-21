using System;
using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;

namespace BeamCheck.Core.Analysis
{
    /// <summary>
    /// Supports from the building model (steel beams, foundations, pedestals imported e.g. from Navisworks on the layer
    /// <see cref="BeamCheckSettings.BuildingSupportLayerPattern"/>). A scaffold beam bears on such a part when its axis crosses the
    /// part's plan outline and the part's top is up to <see cref="BeamCheckSettings.BuildingSupportHeight"/> below the beam (a mesh
    /// grating lies between them and carries nothing). The load taken there goes into the building.
    /// </summary>
    public static class BuildingSupports
    {
        /// <summary>
        /// The building part as an element: the smallest plan rectangle around its points (so a rotated steel beam keeps its real
        /// outline), axis along the long side at the top, width = short side.
        /// </summary>
        public static ScaffoldElement FromPoints(IList<Vec3> points, string id, string description)
        {
            if (points == null || points.Count == 0)
                return null;
            double zMin = points.Min(p => p.Z), zMax = points.Max(p => p.Z);
            var hull = ConvexHull(points.Select(p => new Vec3(p.X, p.Y, 0)).ToList());
            Vec3 c0, c1;
            double width;
            if (hull.Count < 3)
            {
                // a line or a point in plan
                var ps = hull.Count > 0 ? hull : new List<Vec3> { new Vec3(points[0].X, points[0].Y, 0) };
                c0 = ps.First();
                c1 = ps.Last();
                width = 1;
            }
            else
            {
                MinAreaRectangle(hull, out c0, out c1, out width);
            }

            return new ScaffoldElement
            {
                Id = id,
                Kind = ElementKind.BuildingSupport,
                Description = description,
                Start = new Vec3(c0.X, c0.Y, zMax),
                End = new Vec3(c1.X, c1.Y, zMax),
                Width = Math.Max(width, 1),
                ZMin = zMin,
                ZMax = zMax,
                WeightKg = 0,
            };
        }

        /// <summary>Where a building part carries the beam: the stretch of the beam (mm from its start) lying over the part.</summary>
        public sealed class Bearing
        {
            public ScaffoldElement Part;
            public double From;
            public double To;
            public double Middle => (From + To) / 2;
        }

        /// <summary>The building parts under <paramref name="beam"/> and the stretch of the beam over each.</summary>
        public static List<Bearing> Under(ScaffoldElement beam, IEnumerable<ScaffoldElement> parts, BeamCheckSettings s)
        {
            var result = new List<Bearing>();
            double len = beam.PlanLength;
            if (len < 1)
                return result;
            foreach (var p in parts)
            {
                if (p.Kind != ElementKind.BuildingSupport)
                    continue;
                // top of the part from the underside of the beam down to BuildingSupportHeight (and touching it, 30 mm)
                double gap = beam.ZMin - p.ZMax;
                if (gap < -30 || gap > s.BuildingSupportHeight)
                    continue;
                // The beam (its axis within half its width plus the tolerance beside the part) lies over the stretch where the part is
                // really under it: axis lines shifted sideways across that band, the part's outline as it is.
                double band = s.BuildingSupportPlanTolerance + beam.Width / 2;
                var side = new Vec3(-(beam.End.Y - beam.Start.Y), beam.End.X - beam.Start.X, 0) / len;
                double from = double.MaxValue, to = double.MinValue;
                for (int k = -4; k <= 4; k++)
                {
                    var shift = side * (band * k / 4.0);
                    if (!Crossing(beam.Start + shift, beam.End + shift, p, 0, out double t0, out double t1))
                        continue;
                    from = Math.Min(from, t0 * len);
                    to = Math.Max(to, t1 * len);
                }

                if (from > to)
                    continue;
                result.Add(new Bearing { Part = p, From = from, To = to });
            }

            return result;
        }

        /// <summary>
        /// The support point on a bearing: the outer supports of the beam (first and last) at the edge towards the middle of the beam
        /// (simply supported span from the inner edge of a foundation / flange), the supports between them at the middle of the bearing
        /// (continuous beam). <paramref name="isOuter"/>: first or last support of the beam.
        /// </summary>
        public static double SupportPoint(Bearing b, double beamLength, bool isOuter)
        {
            if (!isOuter)
                return b.Middle;
            return Math.Max(b.From, Math.Min(b.To, beamLength / 2));
        }

        /// <summary>The parameter range (0..1) of the beam axis inside the part's outline grown by <paramref name="grow"/>.</summary>
        private static bool Crossing(Vec3 start, Vec3 end, ScaffoldElement part, double grow, out double t0, out double t1)
        {
            t0 = 0;
            t1 = 1;
            var a = part.Start;
            var axis = (part.End - part.Start).WithZ(0);
            double l = axis.LengthXY;
            Vec3 u = l < 1e-6 ? new Vec3(1, 0, 0) : axis / l;
            var v = new Vec3(-u.Y, u.X, 0);
            var p0 = (start - a).WithZ(0);
            var d = (end - start).WithZ(0);
            double hw = part.Width / 2 + grow;
            // along the part: -grow <= u·(p0 + t d) <= l + grow;  across: -hw <= v·(p0 + t d) <= hw
            return Clip(p0.DotXY(u), d.DotXY(u), -grow, l + grow, ref t0, ref t1)
                && Clip(p0.DotXY(v), d.DotXY(v), -hw, hw, ref t0, ref t1);
        }

        private static bool Clip(double start, double rate, double lo, double hi, ref double t0, ref double t1)
        {
            if (Math.Abs(rate) < 1e-9)
                return start >= lo && start <= hi;
            double a = (lo - start) / rate, b = (hi - start) / rate;
            if (a > b) { var x = a; a = b; b = x; }
            t0 = Math.Max(t0, a);
            t1 = Math.Min(t1, b);
            return t0 <= t1;
        }

        private static List<Vec3> ConvexHull(List<Vec3> pts)
        {
            var p = pts.GroupBy(x => (Math.Round(x.X, 1), Math.Round(x.Y, 1))).Select(g => g.First())
                       .OrderBy(x => x.X).ThenBy(x => x.Y).ToList();
            if (p.Count < 3)
                return p;
            double Cross(Vec3 o, Vec3 a, Vec3 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var h = new List<Vec3>();
            foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
            {
                int start = h.Count;
                foreach (var q in pass)
                {
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 1e-9)
                        h.RemoveAt(h.Count - 1);
                    h.Add(q);
                }

                h.RemoveAt(h.Count - 1);
            }

            return h;
        }

        /// <summary>Smallest-area rectangle around a convex polygon (one side lies on a hull edge): its long centre line and its width.</summary>
        private static void MinAreaRectangle(List<Vec3> hull, out Vec3 c0, out Vec3 c1, out double width)
        {
            double best = double.MaxValue;
            c0 = c1 = hull[0];
            width = 0;
            for (int i = 0; i < hull.Count; i++)
            {
                var e = hull[(i + 1) % hull.Count] - hull[i];
                double el = e.LengthXY;
                if (el < 1e-6)
                    continue;
                var u = e / el;
                var v = new Vec3(-u.Y, u.X, 0);
                double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
                foreach (var q in hull)
                {
                    double du = (q - hull[0]).DotXY(u), dv = (q - hull[0]).DotXY(v);
                    uMin = Math.Min(uMin, du); uMax = Math.Max(uMax, du);
                    vMin = Math.Min(vMin, dv); vMax = Math.Max(vMax, dv);
                }

                double area = (uMax - uMin) * (vMax - vMin);
                if (area >= best - 1e-6)
                    continue;
                best = area;
                var o = hull[0];
                if (uMax - uMin >= vMax - vMin)
                {
                    double vm = (vMin + vMax) / 2;
                    c0 = o + u * uMin + v * vm;
                    c1 = o + u * uMax + v * vm;
                    width = vMax - vMin;
                }
                else
                {
                    double um = (uMin + uMax) / 2;
                    c0 = o + v * vMin + u * um;
                    c1 = o + v * vMax + u * um;
                    width = uMax - uMin;
                }
            }
        }
    }
}
