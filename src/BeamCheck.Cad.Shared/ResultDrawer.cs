using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Report;
using BeamCheck.Core.Settings;
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
    /// <summary>Draws the 2D load scheme and the result table, grouped, on the BEAMLOAD layer.</summary>
    internal static class ResultDrawer
    {
        public static void Draw(Database db, BeamLoadResult result, BeamCheckSettings settings, Point3d origin, double toMm)
        {
            double k = 1.0 / toMm; // mm → drawing units
            double thMm = settings.TextHeight * settings.Scale;
            var layout = SchemeLayout.Build(result, thMm, settings.DecimalSeparator);
            var table = ResultTable.Build(result, settings.DecimalSeparator);
            double th = thMm * k;

            Point3d P(double x, double y) => new Point3d(origin.X + x * k, origin.Y + y * k, origin.Z);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                var layerId = EnsureLayer(tr, db, settings.Layer);
                var created = new ObjectIdCollection();

                void Add(Entity e)
                {
                    e.SetDatabaseDefaults(db);
                    e.LayerId = layerId;
                    created.Add(space.AppendEntity(e));
                    tr.AddNewlyCreatedDBObject(e, true);
                }

                // Beam
                var beam = new Polyline();
                beam.AddVertexAt(0, To2d(P(0, 0)), 0, 0, 0);
                beam.AddVertexAt(1, To2d(P(layout.BeamLength, 0)), 0, 0, 0);
                beam.AddVertexAt(2, To2d(P(layout.BeamLength, -layout.BeamHeight)), 0, 0, 0);
                beam.AddVertexAt(3, To2d(P(0, -layout.BeamHeight)), 0, 0, 0);
                beam.Closed = true;
                beam.Elevation = origin.Z;
                Add(beam);
                beam.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);

                // Load arrows
                foreach (var a in layout.Arrows)
                {
                    Add(new Line(P(a.X, a.YTail), P(a.X, a.YTip + a.HeadLength)));
                    var head = new Polyline();
                    head.AddVertexAt(0, To2d(P(a.X, a.YTip + a.HeadLength)), 0, a.HeadLength * 0.6 * k, 0);
                    head.AddVertexAt(1, To2d(P(a.X, a.YTip)), 0, 0, 0);
                    head.Elevation = origin.Z;
                    Add(head);
                }

                foreach (var t in layout.Texts)
                {
                    var mt = new MText
                    {
                        Contents = EscapeMText(t.Text),
                        TextHeight = t.Height * k,
                        Location = P(t.X, t.Y),
                        Attachment = t.Anchor == TextAnchor.BottomCenter ? AttachmentPoint.BottomCenter
                            : t.Anchor == TextAnchor.MiddleLeft ? AttachmentPoint.MiddleLeft
                            : AttachmentPoint.BottomLeft,
                    };
                    Add(mt);
                }

                foreach (var d in layout.Dimensions)
                {
                    var dim = new RotatedDimension(0, P(d.X1, d.YRef), P(d.X2, d.YRef), P((d.X1 + d.X2) / 2, d.YLine), "", db.Dimstyle);
                    Add(dim);
                    dim.Dimscale = 1;
                    dim.Dimtxt = th;
                    dim.Dimasz = th * 0.8;
                    dim.Dimtsz = th * 0.5;  // oblique ticks as in the PERI elevation drawings
                    dim.Dimexe = th * 0.5;
                    dim.Dimexo = th * 0.5;
                    dim.Dimgap = th * 0.3;
                    dim.Dimdec = 0;
                    dim.Dimlfac = toMm;     // always show millimetres
                    dim.Dimtad = 1;
                    dim.Dimtih = false;
                    dim.Dimtoh = false;
                }

                Add(BuildTable(db, table, P(layout.TableX, layout.TableY), th));

                var groups = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);
                var group = new Group("BeamCheck: " + table.Title, true);
                groups.SetAt("BEAMLOAD_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture), group);
                tr.AddNewlyCreatedDBObject(group, true);
                group.Append(created);

                tr.Commit();
            }
        }

        private static Table BuildTable(Database db, ResultTable data, Point3d position, double th)
        {
            int cols = data.Header.Count;
            int rows = 2 + data.Rows.Count + data.Notes.Count;
            var t = new Table { TableStyle = db.Tablestyle, Position = position };
            t.SetSize(rows, cols);

            var widths = new double[cols];
            for (int c = 0; c < cols; c++)
            {
                int chars = Math.Max(data.Header[c].Length, data.Rows.Select(r => r[c].Length).DefaultIfEmpty(0).Max());
                widths[c] = (chars * 0.8 + 2) * th;
            }

            for (int c = 0; c < cols; c++)
                t.Columns[c].Width = widths[c];
            for (int r = 0; r < rows; r++)
                t.Rows[r].Height = th * 2;

            void Set(int r, int c, string text, CellAlignment align)
            {
                t.Cells[r, c].TextString = text ?? "";
                t.Cells[r, c].TextHeight = th;
                t.Cells[r, c].Alignment = align;
            }

            Merge(t, 0, 0, 0, cols - 1);
            Set(0, 0, data.Title, CellAlignment.MiddleCenter);
            for (int c = 0; c < cols; c++)
                Set(1, c, data.Header[c], CellAlignment.MiddleCenter);
            for (int r = 0; r < data.Rows.Count; r++)
                for (int c = 0; c < cols; c++)
                    Set(2 + r, c, data.Rows[r][c], c == 0 ? CellAlignment.MiddleCenter : CellAlignment.MiddleRight);
            for (int n = 0; n < data.Notes.Count; n++)
            {
                int r = 2 + data.Rows.Count + n;
                Merge(t, r, 0, r, cols - 1);
                Set(r, 0, data.Notes[n], CellAlignment.MiddleLeft);
            }

            t.GenerateLayout();
            return t;
        }

        private static void Merge(Table t, int r1, int c1, int r2, int c2)
        {
            try
            {
                t.MergeCells(CellRange.Create(t, r1, c1, r2, c2));
            }
            catch (System.Exception)
            {
                // Already merged by the table style (title row) — nothing to do.
            }
        }

        private static ObjectId EnsureLayer(Transaction tr, Database db, string name)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name))
                return lt[name];
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, 2) };
            var id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        private static Point2d To2d(Point3d p) => new Point2d(p.X, p.Y);

        private static string EscapeMText(string s) => (s ?? "").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
    }
}
