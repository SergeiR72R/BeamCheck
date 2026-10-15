using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BeamCheck.Core.Batch;

namespace BeamCheck.Core.Report
{
    public enum IllColor
    {
        Beam,
        Load,
        Support,
        Dimension,
        Text,
    }

    public sealed class IllLine
    {
        public double X1, Y1, X2, Y2;
        public IllColor Color;
    }

    public sealed class IllPoly
    {
        public List<double[]> Points = new List<double[]>();
        public bool Closed;
        public bool Filled;
        public IllColor Color;
    }

    public sealed class IllCircle
    {
        public double X, Y, R;
        public IllColor Color;
    }

    public sealed class IllText
    {
        public double X, Y, Height;
        public string Text;
        public TextAnchor Anchor;
        public bool Bold;
        public IllColor Color;
    }

    /// <summary>
    /// Picture of one beam: its profile in side view, the loads on it (letters and values), the supports under it
    /// (with the reactions) and the dimension chain from the left edge. Millimetres, X along the beam, Y up, beam top at Y = 0.
    /// The CAD layer renders the primitives (drawing, PNG); <see cref="ToSvg"/> is for checking the layout.
    /// </summary>
    public sealed class BeamIllustration
    {
        public List<IllLine> Lines { get; } = new List<IllLine>();
        public List<IllPoly> Polys { get; } = new List<IllPoly>();
        public List<IllCircle> Circles { get; } = new List<IllCircle>();
        public List<IllText> Texts { get; } = new List<IllText>();

        public double MinX, MinY, MaxX, MaxY;
        public double TextHeight;

        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        private const double CharWidth = 0.62;

        public static BeamIllustration Build(BeamReport r, bool design, string decimalSeparator)
        {
            var f = new NumberFormat(decimalSeparator);
            double len = r.Length;
            double th = Math.Max(len, 1500) / 42.0;
            double H = th * 2.4;
            var ill = new BeamIllustration { TextHeight = th };

            // Beam profile (side view) with its holes.
            ill.Rect(0, -H, len, 0, IllColor.Beam);
            double pitch = th * 3;
            for (double x = pitch / 2; x < len - th; x += pitch)
            {
                ill.Circles.Add(new IllCircle { X = x, Y = -H * 0.28, R = th * 0.2, Color = IllColor.Beam });
                ill.Circles.Add(new IllCircle { X = x, Y = -H * 0.72, R = th * 0.2, Color = IllColor.Beam });
            }

            // Loads: arrows from above with letter and value, staggered where the labels would meet.
            var rowRight = new List<double>();
            double arrow = th * 5, rowStep = th * 2.6, maxTail = arrow;
            foreach (var l in r.Loads)
            {
                string value = f.Kn(design ? l.Fd : l.Fk);
                double w = Math.Max(value.Length * CharWidth * th * 0.9, th * 2);
                double left = l.Position - w / 2;
                int row = 0;
                while (row < rowRight.Count && rowRight[row] > left)
                    row++;
                if (row == rowRight.Count)
                    rowRight.Add(double.MinValue);
                rowRight[row] = l.Position + w / 2 + th * 0.4;

                double tail = arrow + row * rowStep;
                maxTail = Math.Max(maxTail, tail);
                ill.Arrow(l.Position, tail, 0, th, IllColor.Load);
                ill.Texts.Add(new IllText { X = l.Position, Y = tail + th * 0.3, Text = value, Height = th * 0.9, Anchor = TextAnchor.BottomCenter, Color = IllColor.Load });
                ill.Texts.Add(new IllText { X = l.Position, Y = tail + th * 1.45, Text = l.Label, Height = th * 1.4, Anchor = TextAnchor.BottomCenter, Bold = true, Color = IllColor.Text });
            }

            // Supports under the beam (always pin supports), with their names.
            double lowest = -H;
            if (r.ContinuousSupport)
            {
                double yg = -H - th * 0.3;
                ill.Lines.Add(new IllLine { X1 = -th, Y1 = yg, X2 = len + th, Y2 = yg, Color = IllColor.Support });
                for (double x = -th; x < len + th; x += th * 1.2)
                    ill.Lines.Add(new IllLine { X1 = x, Y1 = yg, X2 = x - th * 0.7, Y2 = yg - th * 0.9, Color = IllColor.Support });
                ill.Texts.Add(new IllText { X = len / 2, Y = yg - th * 2.2, Text = "durchgehend aufgelagert (fester, ebener Untergrund)", Height = th * 0.9, Anchor = TextAnchor.BottomCenter, Color = IllColor.Support });
                lowest = yg - th * 2.2;
            }
            else
            {
                double yBase = -H - th * 1.8;
                foreach (var s in r.Supports)
                {
                    ill.Poly(new[] { new[] { s.Position, -H }, new[] { s.Position - th, yBase }, new[] { s.Position + th, yBase } }, true, false, IllColor.Support);
                    ill.Lines.Add(new IllLine { X1 = s.Position - th * 1.3, Y1 = yBase, X2 = s.Position + th * 1.3, Y2 = yBase, Color = IllColor.Support });
                    // Pin support with its name; no reactions are shown.
                    double yText = yBase - th * 1.2;
                    ill.Texts.Add(new IllText { X = s.Position, Y = yText, Text = s.Label, Height = th * 1.0, Anchor = TextAnchor.BottomCenter, Bold = true, Color = IllColor.Text });
                    lowest = Math.Min(lowest, yText);
                }

                lowest = Math.Min(lowest, yBase);
            }

            // Dimension chain from the left edge through every load and support, then the overall length.
            double yChain = lowest - th * 3;
            var stops = new List<double> { 0, len };
            stops.AddRange(r.Events.Select(e => Math.Max(0, Math.Min(len, e.Position))));
            stops = stops.OrderBy(x => x).ToList();
            // Extension lines start at the beam, or below the support labels so they do not run through them.
            double yBelow = lowest - th * 0.3;
            foreach (double x in stops.Distinct())
            {
                bool atSupport = r.Supports.Any(sp => Math.Abs(Math.Max(0, Math.Min(len, sp.Position)) - x) < 0.5);
                ill.Lines.Add(new IllLine { X1 = x, Y1 = atSupport ? yBelow : -H, X2 = x, Y2 = yChain - th * 0.4, Color = IllColor.Dimension });
            }

            for (int i = 1; i < stops.Count; i++)
            {
                double a = stops[i - 1], b = stops[i];
                if (b - a < 0.5)
                    continue;
                string text = f.Mm(b - a);
                double w = text.Length * CharWidth * th;
                bool narrow = b - a < w + th * 0.5;
                ill.DimensionSegment(a, b, yChain, text, th, narrow ? th * 1.3 : 0);
            }

            double yTotal = yChain - th * 3;
            ill.Lines.Add(new IllLine { X1 = 0, Y1 = yChain - th * 0.4, X2 = 0, Y2 = yTotal - th * 0.4, Color = IllColor.Dimension });
            ill.Lines.Add(new IllLine { X1 = len, Y1 = yChain - th * 0.4, X2 = len, Y2 = yTotal - th * 0.4, Color = IllColor.Dimension });
            ill.DimensionSegment(0, len, yTotal, "L = " + f.Mm(len), th, 0);

            // Title.
            string title = r.Name + (string.IsNullOrEmpty(r.Description) ? "" : "   " + r.Description) + "   L = " + f.Mm(len) + " mm   Lastklasse " + r.LoadClass
                           + "   (" + (design ? "Fd" : "Fk") + ")";
            ill.Texts.Add(new IllText { X = 0, Y = maxTail + th * 3.4, Text = title, Height = th * 1.2, Anchor = TextAnchor.BottomLeft, Bold = true, Color = IllColor.Text });

            ill.ComputeBounds(th * 2);
            return ill;
        }

        private void Rect(double x0, double y0, double x1, double y1, IllColor c) =>
            Poly(new[] { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } }, true, false, c);

        private void Poly(IEnumerable<double[]> pts, bool closed, bool filled, IllColor c)
        {
            var p = new IllPoly { Closed = closed, Filled = filled, Color = c };
            p.Points.AddRange(pts);
            Polys.Add(p);
        }

        private void Arrow(double x, double yTail, double yTip, double head, IllColor c)
        {
            Lines.Add(new IllLine { X1 = x, Y1 = yTail, X2 = x, Y2 = yTip + head, Color = c });
            Poly(new[] { new[] { x, yTip }, new[] { x - head * 0.55, yTip + head * 1.3 }, new[] { x + head * 0.55, yTip + head * 1.3 } }, true, true, c);
        }

        private void DimensionSegment(double x1, double x2, double yLine, string text, double th, double textLift)
        {
            Lines.Add(new IllLine { X1 = x1, Y1 = yLine, X2 = x2, Y2 = yLine, Color = IllColor.Dimension });
            foreach (double x in new[] { x1, x2 })
                Lines.Add(new IllLine { X1 = x - th * 0.35, Y1 = yLine - th * 0.35, X2 = x + th * 0.35, Y2 = yLine + th * 0.35, Color = IllColor.Dimension });
            Texts.Add(new IllText { X = (x1 + x2) / 2, Y = yLine + th * 0.25 + textLift, Text = text, Height = th, Anchor = TextAnchor.BottomCenter, Color = IllColor.Text });
        }

        private void ComputeBounds(double margin)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            void P(double x, double y)
            {
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }

            foreach (var l in Lines) { P(l.X1, l.Y1); P(l.X2, l.Y2); }
            foreach (var p in Polys) foreach (var q in p.Points) P(q[0], q[1]);
            foreach (var c in Circles) { P(c.X - c.R, c.Y - c.R); P(c.X + c.R, c.Y + c.R); }
            foreach (var t in Texts)
            {
                double w = t.Text.Length * CharWidth * t.Height * (t.Bold ? 1.1 : 1);
                double xa = t.Anchor == TextAnchor.BottomCenter ? t.X - w / 2 : t.X;
                double xb = t.Anchor == TextAnchor.BottomCenter ? t.X + w / 2 : t.X + w;
                P(xa, t.Y); P(xb, t.Y + t.Height);
            }

            MinX = x0 - margin; MaxX = x1 + margin; MinY = y0 - margin; MaxY = y1 + margin;
        }

        private static string Col(IllColor c)
        {
            switch (c)
            {
                case IllColor.Beam: return "#1f4e79";
                case IllColor.Load: return "#c00000";
                case IllColor.Support: return "#2e7d32";
                case IllColor.Dimension: return "#333333";
                default: return "#000000";
            }
        }

        /// <summary>SVG of the picture, for checking the layout (the real rendering is done by the CAD layer).</summary>
        public string ToSvg(double pixelWidth = 1200)
        {
            var inv = CultureInfo.InvariantCulture;
            double k = pixelWidth / Width;
            double X(double x) => (x - MinX) * k;
            double Y(double y) => (MaxY - y) * k;
            var sb = new StringBuilder();
            sb.AppendFormat(inv, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{0:0}\" height=\"{1:0}\" viewBox=\"0 0 {0:0} {1:0}\">", pixelWidth, Height * k);
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"white\"/>");
            foreach (var l in Lines)
                sb.AppendFormat(inv, "<line x1=\"{0:0.##}\" y1=\"{1:0.##}\" x2=\"{2:0.##}\" y2=\"{3:0.##}\" stroke=\"{4}\" stroke-width=\"1.4\"/>", X(l.X1), Y(l.Y1), X(l.X2), Y(l.Y2), Col(l.Color));
            foreach (var p in Polys)
            {
                sb.Append(p.Closed ? "<polygon points=\"" : "<polyline fill=\"none\" points=\"");
                sb.Append(string.Join(" ", p.Points.Select(q => X(q[0]).ToString("0.##", inv) + "," + Y(q[1]).ToString("0.##", inv))));
                sb.AppendFormat(inv, "\" fill=\"{0}\" stroke=\"{1}\" stroke-width=\"1.6\"/>", p.Filled ? Col(p.Color) : "none", Col(p.Color));
            }

            foreach (var c in Circles)
                sb.AppendFormat(inv, "<circle cx=\"{0:0.##}\" cy=\"{1:0.##}\" r=\"{2:0.##}\" fill=\"none\" stroke=\"{3}\" stroke-width=\"1.2\"/>", X(c.X), Y(c.Y), c.R * k, Col(c.Color));
            foreach (var t in Texts)
                sb.AppendFormat(inv, "<text x=\"{0:0.##}\" y=\"{1:0.##}\" font-family=\"Arial\" font-size=\"{2:0.##}\" text-anchor=\"{3}\" fill=\"{4}\"{5}>{6}</text>",
                    X(t.X), Y(t.Y), t.Height * k * 0.95, t.Anchor == TextAnchor.BottomCenter ? "middle" : "start", Col(t.Color), t.Bold ? " font-weight=\"bold\"" : "",
                    System.Security.SecurityElement.Escape(t.Text));
            sb.Append("</svg>");
            return sb.ToString();
        }
    }
}
