using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BeamCheck.Core.Analysis;

namespace BeamCheck.Core.Report
{
    /// <summary>Plain text table (title, header, rows, notes) shared by the CAD table and the CSV export.</summary>
    public sealed class ResultTable
    {
        public string Title { get; set; }

        public List<string> Header { get; } = new List<string>();

        public List<List<string>> Rows { get; } = new List<List<string>>();

        public List<string> Notes { get; } = new List<string>();

        public static ResultTable Build(BeamLoadResult r, string decimalSeparator)
        {
            var f = new NumberFormat(decimalSeparator);
            var t = new ResultTable
            {
                Title = "Нагрузки на балку" + (string.IsNullOrEmpty(r.BeamName) ? "" : " " + r.BeamName)
                        + ", L = " + f.Mm(r.BeamLength) + " мм",
            };

            bool transfer = r.Points.Any(p => p.TransferFk > 0.005);
            bool design = r.ShowDesignValues;
            t.Header.AddRange(new[] { "Стойка", "X, мм", "Площадь, м²", "Live load, кН", "Self weight, кН", "Total load, кН" });
            if (design)
                t.Header.Add("Total Fd, кН");
            if (transfer)
                t.Header.Add("в т.ч. с верх. балок, кН");
            foreach (var p in r.Points)
            {
                var row = new List<string>
                {
                    p.Name,
                    f.Mm(p.Position),
                    f.Num(p.AreaM2, 2),
                    f.Num(p.Qk, 2),
                    f.Num(p.Gk, 2),
                    f.Num(p.Fk, 2),
                };
                if (design)
                    row.Add(f.Num(p.Fd, 2));
                if (transfer)
                    row.Add(f.Num(p.TransferFk, 2));
                t.Rows.Add(row);
            }

            double area = r.Points.Sum(p => p.AreaM2), live = r.Points.Sum(p => p.Qk);
            double self = r.Points.Sum(p => p.Gk), total = r.Points.Sum(p => p.Fk);
            var sum = new List<string> { "Σ", "", f.Num(area, 2), f.Num(live, 2), f.Num(self, 2), f.Num(total, 2) };
            if (design)
                sum.Add(f.Num(r.Points.Sum(p => p.Fd), 2));
            if (transfer)
                sum.Add(f.Num(r.Points.Sum(p => p.TransferFk), 2));
            t.Rows.Add(sum);

            t.Notes.Add("Summary: площадь = " + f.Num(area, 2) + " м², live load = " + f.Num(live, 2) + " кН, self weight load = "
                        + f.Num(self, 2) + " кН, total load = " + f.Num(total, 2) + " кН");
            t.Notes.Add("Класс нагрузки " + r.LoadClass + " (EN 12811-1): q = " + f.Num(r.ServiceLoad, 2) + " кН/м²");
            var levelNotes = r.Points.SelectMany(p => p.Levels)
                .Where(l => l.Factor > 0)
                .GroupBy(l => l.Level.Index)
                .OrderBy(g => g.Key)
                .Select(g => "ярус " + g.Key + " (Z=" + f.Mm(g.First().Level.Z) + "): " + f.Num(g.First().Factor * 100, 0) + "%")
                .ToList();
            if (levelNotes.Count > 0)
                t.Notes.Add("Live load приложен на: " + string.Join("; ", levelNotes) + "; на остальных ярусах 0 (работы ведутся на одном ярусе)");
            t.Notes.Add("Total load = self weight + live load (нормативные); Fd = " + f.Num(r.GammaG, 2) + "·self weight + " + f.Num(r.GammaQ, 2) + "·live load; на схеме — " + (r.ShowDesignValues ? "Fd" : "total load"));
            return t;
        }

        public string ToCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Escape(Title));
            sb.AppendLine(string.Join(";", Header.Select(Escape)));
            foreach (var row in Rows)
                sb.AppendLine(string.Join(";", row.Select(Escape)));
            foreach (var n in Notes)
                sb.AppendLine(Escape(n));
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ';', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }

    public sealed class NumberFormat
    {
        private readonly NumberFormatInfo _nfi;

        public NumberFormat(string decimalSeparator)
        {
            _nfi = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            _nfi.NumberDecimalSeparator = string.IsNullOrEmpty(decimalSeparator) ? "." : decimalSeparator;
        }

        public string Num(double v, int decimals) => v.ToString("F" + decimals, _nfi);

        public string Mm(double v) => v.ToString("0", _nfi);

        /// <summary>Load label for the drawing, e.g. "13,1 кН" (2 decimals below 10 kN, 1 above).</summary>
        public string Kn(double v) => v.ToString(v < 10 ? "0.00" : "0.0", _nfi) + " кН";
    }
}
