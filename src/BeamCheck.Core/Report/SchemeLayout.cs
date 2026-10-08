using System;
using System.Collections.Generic;
using BeamCheck.Core.Analysis;

namespace BeamCheck.Core.Report
{
    public enum TextAnchor
    {
        BottomCenter,
        BottomLeft,
        MiddleLeft,
    }

    public sealed class SchemeText
    {
        public double X;
        public double Y;
        public string Text;
        public double Height;
        public TextAnchor Anchor;
    }

    public sealed class SchemeArrow
    {
        public double X;

        /// <summary>Arrow tail (top).</summary>
        public double YTail;

        /// <summary>Arrow tip (touches the beam top).</summary>
        public double YTip;

        public double HeadLength;
    }

    public sealed class SchemeDimension
    {
        public double X1;
        public double X2;

        /// <summary>Y of the extension line origins.</summary>
        public double YRef;

        /// <summary>Y of the dimension line.</summary>
        public double YLine;
    }

    /// <summary>
    /// 2D load scheme in local coordinates (mm, origin = beam start top edge, X along the beam).
    /// The CAD layer only scales/moves these primitives to the picked point.
    /// </summary>
    public sealed class SchemeLayout
    {
        public double BeamLength;
        public double BeamHeight;
        public double TextHeight;
        public List<SchemeArrow> Arrows { get; } = new List<SchemeArrow>();
        public List<SchemeText> Texts { get; } = new List<SchemeText>();
        public List<SchemeDimension> Dimensions { get; } = new List<SchemeDimension>();

        /// <summary>Top-left corner of the result table.</summary>
        public double TableX;
        public double TableY;

        /// <param name="textHeight">Model-space text height in mm (paper height × scale).</param>
        public static SchemeLayout Build(BeamLoadResult r, double textHeight, string decimalSeparator)
        {
            var f = new NumberFormat(decimalSeparator);
            double th = textHeight;
            var s = new SchemeLayout
            {
                BeamLength = r.BeamLength,
                BeamHeight = th * 1.2,
                TextHeight = th,
            };

            double arrowLen = th * 5;
            double labelWidthPerChar = th * 0.75;

            // Stagger labels vertically when they would overlap their left neighbour.
            var rowRightEdge = new List<double>();
            foreach (var p in r.Points)
            {
                string label = f.Kn(r.DisplayValue(p));
                double w = label.Length * labelWidthPerChar;
                double left = p.Position - w / 2;
                int row = 0;
                while (row < rowRightEdge.Count && rowRightEdge[row] > left)
                    row++;
                if (row == rowRightEdge.Count)
                    rowRightEdge.Add(double.MinValue);
                rowRightEdge[row] = p.Position + w / 2 + th;

                double tail = arrowLen + row * th * 2;
                s.Arrows.Add(new SchemeArrow { X = p.Position, YTail = tail, YTip = 0, HeadLength = th });
                s.Texts.Add(new SchemeText { X = p.Position, Y = tail + th * 0.4, Text = label, Height = th, Anchor = TextAnchor.BottomCenter });
                s.Texts.Add(new SchemeText { X = p.Position, Y = -s.BeamHeight - th * 0.5 - th * 0.8, Text = p.Name, Height = th * 0.8, Anchor = TextAnchor.BottomCenter });
            }

            double maxTail = arrowLen + Math.Max(0, rowRightEdge.Count - 1) * th * 2;
            string title = (string.IsNullOrEmpty(r.BeamName) ? "Балка" : r.BeamName)
                           + "   класс " + r.LoadClass + ", " + (r.ShowDesignValues ? "Fd" : "Fk");
            s.Texts.Add(new SchemeText { X = 0, Y = maxTail + th * 2.5, Text = title, Height = th, Anchor = TextAnchor.BottomLeft });

            double yChain = -s.BeamHeight - th * 4;
            double yTotal = yChain - th * 3;
            var stops = new List<double> { 0 };
            foreach (var p in r.Points)
                stops.Add(Math.Max(0, Math.Min(r.BeamLength, p.Position)));
            stops.Add(r.BeamLength);
            stops.Sort();
            for (int i = 1; i < stops.Count; i++)
            {
                if (stops[i] - stops[i - 1] < 0.5)
                    continue;
                s.Dimensions.Add(new SchemeDimension { X1 = stops[i - 1], X2 = stops[i], YRef = -s.BeamHeight, YLine = yChain });
            }

            s.Dimensions.Add(new SchemeDimension { X1 = 0, X2 = r.BeamLength, YRef = -s.BeamHeight, YLine = yTotal });

            s.TableX = 0;
            s.TableY = yTotal - th * 4;
            return s;
        }
    }
}
