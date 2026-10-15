using System;
using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Report;

namespace BeamCheck.Core.Batch
{
    /// <summary>The workbook of a batch run: overview of all beams, every support and load with its distances, the pictures.</summary>
    public static class BatchExcel
    {
        private static object L(BeamEvent e, double v) => e.Kind == BeamEventKind.Load ? (object)v : null;

        private static int I(double v) => (int)Math.Round(v);

        /// <param name="pictures">Renders the picture of a beam as PNG with its size in pixels (null: no picture sheet).</param>
        public static XlsxBuilder Build(IList<BeamReport> reports, Func<BeamReport, Tuple<byte[], int, int>> pictures = null)
        {
            var wb = new XlsxBuilder();

            var overview = wb.AddSheet("Übersicht");
            overview.Rows.Add(new object[]
            {
                "Balken", "Handle", "Artikel", "Bezeichnung", "Länge [mm]", "Höhe UK [mm]", "Höhe OK [mm]", "Mitte X [mm]", "Mitte Y [mm]", "Gewicht [kg]", "Auflager", "Lasten",
                "Σ Lasten Fk [kN]", "max. Last Fk [kN]", "Auflage", "Hinweise",
            });
            foreach (var r in reports)
            {
                overview.Rows.Add(new object[]
                {
                    r.Name, r.Id, r.Article ?? "", r.Description ?? "", I(r.Length), I(r.ZMin), I(r.ZMax), I(r.CenterX), I(r.CenterY), r.WeightKg, r.Supports.Count, r.Loads.Count,
                    r.TotalLoadFk, r.MaxLoadFk, r.ContinuousSupport ? "durchgehend (Untergrund)" : "Gelenklager (Ständer)", string.Join(" | ", r.Notes),
                });
            }

            overview.ColumnWidths.AddRange(new double[] { 10, 10, 10, 26, 11, 12, 12, 13, 13, 11, 9, 8, 14, 14, 22, 70 });

            var positions = wb.AddSheet("Positionen");
            positions.Rows.Add(new object[]
            {
                "Balken", "Handle", "Art", "Name", "Position vom linken Rand [mm]", "Abstand zum vorherigen [mm]",
                "Gk [kN]", "Qk [kN]", "Fk [kN]", "Fd [kN]", "Fläche [m²]", "Herkunft",
            });
            foreach (var r in reports)
            {
                foreach (var e in r.Events)
                {
                    positions.Rows.Add(new object[]
                    {
                        r.Name, r.Id, e.Kind == BeamEventKind.Support ? "Auflager (Gelenklager)" : "Last", e.Label, I(e.Position), I(e.Delta),
                        L(e, e.Gk), L(e, e.Qk), L(e, e.Fk), L(e, e.Fd), e.Kind == BeamEventKind.Load ? (object)e.AreaM2 : null, e.Source ?? "",
                    });
                }
            }

            positions.ColumnWidths.AddRange(new double[] { 10, 10, 20, 8, 18, 18, 10, 10, 10, 10, 11, 12 });

            if (pictures != null)
            {
                var sheet = wb.AddSheet("Abbildungen");
                sheet.FreezeHeader = false;
                sheet.AutoFilter = false;
                sheet.Rows.Add(new object[] { "Balken", "Abbildung" });
                sheet.ColumnWidths.AddRange(new double[] { 12, 130 });
                int row = 1;
                foreach (var r in reports)
                {
                    var pic = pictures(r);
                    sheet.Rows.Add(new object[] { r.Name + (string.IsNullOrEmpty(r.Description) ? "" : "\n" + r.Description) });
                    if (pic != null)
                    {
                        sheet.Pictures.Add(new XlsxBuilder.Picture { Png = pic.Item1, WidthPx = pic.Item2, HeightPx = pic.Item3, Row = row, Column = 1 });
                        sheet.RowHeights[row] = pic.Item3 * 0.75 + 6;
                    }

                    sheet.BoldRows.Add(row);
                    row++;
                }
            }

            return wb;
        }
    }
}
