using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using BeamCheck.Core.Report;

namespace BeamCheck.Cad
{
    /// <summary>Renders the picture of a beam to PNG (for the Excel workbook).</summary>
    internal static class BeamPng
    {
        private static Color Col(IllColor c)
        {
            switch (c)
            {
                case IllColor.Beam: return Color.FromArgb(31, 78, 121);
                case IllColor.Load: return Color.FromArgb(192, 0, 0);
                case IllColor.Support: return Color.FromArgb(46, 125, 50);
                case IllColor.Dimension: return Color.FromArgb(51, 51, 51);
                default: return Color.Black;
            }
        }

        public static Tuple<byte[], int, int> Render(BeamIllustration ill, int widthPx = 1100)
        {
            double k = widthPx / ill.Width;
            int heightPx = Math.Max(60, (int)Math.Ceiling(ill.Height * k));
            float X(double x) => (float)((x - ill.MinX) * k);
            float Y(double y) => (float)((ill.MaxY - y) * k);

            using (var bmp = new Bitmap(widthPx, heightPx))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.White);

                foreach (var l in ill.Lines)
                    using (var pen = new Pen(Col(l.Color), 1.4f))
                        g.DrawLine(pen, X(l.X1), Y(l.Y1), X(l.X2), Y(l.Y2));

                foreach (var p in ill.Polys)
                {
                    var pts = new PointF[p.Points.Count];
                    for (int i = 0; i < pts.Length; i++)
                        pts[i] = new PointF(X(p.Points[i][0]), Y(p.Points[i][1]));
                    using (var pen = new Pen(Col(p.Color), 1.6f))
                    {
                        if (p.Filled)
                            using (var br = new SolidBrush(Col(p.Color)))
                                g.FillPolygon(br, pts);
                        else if (p.Closed)
                            g.DrawPolygon(pen, pts);
                        else
                            g.DrawLines(pen, pts);
                        if (p.Filled)
                            g.DrawPolygon(pen, pts);
                    }
                }

                foreach (var c in ill.Circles)
                    using (var pen = new Pen(Col(c.Color), 1.2f))
                        g.DrawEllipse(pen, X(c.X - c.R), Y(c.Y + c.R), (float)(2 * c.R * k), (float)(2 * c.R * k));

                foreach (var t in ill.Texts)
                {
                    float px = (float)(t.Height * k * 0.95);
                    using (var font = new Font("Arial", px, t.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var br = new SolidBrush(Col(t.Color)))
                    using (var fmt = new StringFormat(StringFormat.GenericTypographic))
                    {
                        fmt.Alignment = t.Anchor == TextAnchor.BottomCenter ? StringAlignment.Center : StringAlignment.Near;
                        // The text sits on its baseline at Y.
                        float ascent = font.FontFamily.GetCellAscent(font.Style) / (float)font.FontFamily.GetEmHeight(font.Style) * px;
                        g.DrawString(t.Text, font, br, X(t.X), Y(t.Y) - ascent, fmt);
                    }
                }

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return Tuple.Create(ms.ToArray(), widthPx, heightPx);
                }
            }
        }
    }
}
