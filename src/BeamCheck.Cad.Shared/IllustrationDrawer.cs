using System;
using BeamCheck.Core.Report;
#if BRICSCAD
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
#else
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace BeamCheck.Cad
{
    /// <summary>Draws the picture of a beam (profile, loads, supports, dimension chain) into the drawing.</summary>
    internal static class IllustrationDrawer
    {
        public const string LayerName = "BEAMCHECK_BILDER";

        private static short Aci(IllColor c)
        {
            switch (c)
            {
                case IllColor.Beam: return 5;
                case IllColor.Load: return 1;
                case IllColor.Support: return 3;
                default: return 7;
            }
        }

        /// <summary>Draws one picture with its lower-left corner at <paramref name="origin"/>; returns its height in drawing units.</summary>
        public static double Draw(Transaction tr, Database db, BlockTableRecord space, BeamIllustration ill, Point3d origin, double toMm)
        {
            double k = 1.0 / toMm;
            var layerId = ResultDrawer.EnsureLayer(tr, db, LayerName);
            Point3d P(double x, double y) => new Point3d(origin.X + (x - ill.MinX) * k, origin.Y + (y - ill.MinY) * k, origin.Z);

            void Add(Entity e, IllColor c)
            {
                e.SetDatabaseDefaults(db);
                e.LayerId = layerId;
                e.Color = Color.FromColorIndex(ColorMethod.ByAci, Aci(c));
                space.AppendEntity(e);
                tr.AddNewlyCreatedDBObject(e, true);
            }

            foreach (var l in ill.Lines)
                Add(new Line(P(l.X1, l.Y1), P(l.X2, l.Y2)), l.Color);

            foreach (var p in ill.Polys)
            {
                var pl = new Polyline();
                for (int i = 0; i < p.Points.Count; i++)
                {
                    var q = P(p.Points[i][0], p.Points[i][1]);
                    pl.AddVertexAt(i, new Point2d(q.X, q.Y), 0, 0, 0);
                }

                pl.Closed = p.Closed;
                pl.Elevation = origin.Z;
                Add(pl, p.Color);
                if (p.Filled && p.Points.Count == 3)
                {
                    // Arrow head: a solid triangle.
                    var a = P(p.Points[0][0], p.Points[0][1]);
                    var b = P(p.Points[1][0], p.Points[1][1]);
                    var c = P(p.Points[2][0], p.Points[2][1]);
                    Add(new Solid(a, b, c, c), p.Color);
                }
            }

            foreach (var c in ill.Circles)
                Add(new Circle(P(c.X, c.Y), Vector3d.ZAxis, c.R * k), c.Color);

            foreach (var t in ill.Texts)
            {
                var mt = new MText
                {
                    Contents = (t.Bold ? "{\\b1;" : "") + Escape(t.Text) + (t.Bold ? "}" : ""),
                    TextHeight = t.Height * k,
                    Location = P(t.X, t.Y),
                    Attachment = t.Anchor == TextAnchor.BottomCenter ? AttachmentPoint.BottomCenter : AttachmentPoint.BottomLeft,
                };
                Add(mt, t.Color);
            }

            return ill.Height * k;
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
    }
}
