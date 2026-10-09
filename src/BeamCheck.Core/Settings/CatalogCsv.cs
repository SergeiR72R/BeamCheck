using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BeamCheck.Core.Recognition;

namespace BeamCheck.Core.Settings
{
    /// <summary>
    /// Weight table "article;weight kg;name" (BeamCheck.weights.csv). Separator ';', ',' or tab,
    /// decimal point or comma, optional header row, '#' comments. Rows without a positive weight
    /// are ignored, so the template can be filled in gradually.
    /// </summary>
    public static class CatalogCsv
    {
        public const string FileName = "BeamCheck.weights.csv";

        public static List<CatalogItem> Parse(string text)
        {
            var items = new List<CatalogItem>();
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                char sep = line.Contains(";") ? ';' : line.Contains("\t") ? '\t' : ',';
                var cols = line.Split(sep).Select(c => c.Trim().Trim('"')).ToArray();
                if (cols.Length < 2 || string.IsNullOrEmpty(cols[0]))
                    continue;
                if (!ElementClassifier.TryParseNumber(cols[1], out double kg) || kg <= 0)
                    continue; // header, comment or not filled in yet

                items.Add(new CatalogItem
                {
                    Article = cols[0],
                    WeightKg = kg,
                    Description = cols.Length > 2 && cols[2].Length > 0 ? cols[2] : null,
                });
            }

            return items;
        }

        /// <summary>Adds the table to the settings catalogue; table entries win over existing ones.</summary>
        public static int Merge(BeamCheckSettings settings, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return 0;
            var items = Parse(File.ReadAllText(path));
            foreach (var item in items)
            {
                settings.Catalog.RemoveAll(c => string.Equals(c.Article, item.Article, StringComparison.OrdinalIgnoreCase));
                settings.Catalog.Add(item);
            }

            return items.Count;
        }
    }
}
