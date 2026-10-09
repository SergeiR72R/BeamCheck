using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;

namespace BeamCheck.Core.Recognition
{
    /// <summary>Everything the CAD layer could read about an object, as plain strings.</summary>
    public sealed class ElementSignature
    {
        public string Handle { get; set; }
        public string EntityType { get; set; }
        public string DxfName { get; set; }
        public string BlockName { get; set; }
        public string Layer { get; set; }

        /// <summary>Attributes, dynamic properties, XData and Xrecord values: key → value.</summary>
        public List<KeyValuePair<string, string>> Properties { get; } = new List<KeyValuePair<string, string>>();

        public void Add(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                Properties.Add(new KeyValuePair<string, string>(key ?? "", value.Trim()));
        }

        /// <summary>Single line used for regex matching.</summary>
        public string ToMatchText()
        {
            var sb = new StringBuilder();
            sb.Append("block=").Append(BlockName).Append(" | layer=").Append(Layer)
              .Append(" | type=").Append(EntityType).Append(" | dxf=").Append(DxfName);
            foreach (var p in Properties)
                sb.Append(" | ").Append(p.Key).Append('=').Append(p.Value);
            return sb.ToString();
        }
    }

    public sealed class Classification
    {
        public ElementKind Kind { get; set; }
        public string Article { get; set; }
        public string Description { get; set; }

        /// <summary>Weight from the drawing data or catalogue; null if unknown.</summary>
        public double? WeightKg { get; set; }
    }

    /// <summary>Turns an <see cref="ElementSignature"/> into kind / article / weight using the settings rules.</summary>
    public sealed class ElementClassifier
    {
        private readonly BeamCheckSettings _settings;
        private readonly List<KeyValuePair<ElementKind, Regex>> _rules = new List<KeyValuePair<ElementKind, Regex>>();
        private readonly Regex _articleRegex;
        private readonly Dictionary<string, CatalogItem> _catalog;

        public ElementClassifier(BeamCheckSettings settings)
        {
            _settings = settings;
            foreach (var rule in settings.Rules ?? new List<RecognitionRule>())
            {
                if (!TryParseKind(rule.Kind, out var kind) || string.IsNullOrEmpty(rule.Pattern))
                    continue;
                _rules.Add(new KeyValuePair<ElementKind, Regex>(kind, new Regex(rule.Pattern, RegexOptions.CultureInvariant)));
            }

            if (!string.IsNullOrEmpty(settings.ArticlePattern))
                _articleRegex = new Regex(settings.ArticlePattern, RegexOptions.CultureInvariant);

            _catalog = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in settings.Catalog ?? new List<CatalogItem>())
                if (!string.IsNullOrEmpty(item.Article))
                    _catalog[item.Article.Trim()] = item;
        }

        public static bool TryParseKind(string name, out ElementKind kind) =>
            Enum.TryParse(name, true, out kind) && kind != ElementKind.Unknown;

        public Classification Classify(ElementSignature sig)
        {
            var result = new Classification
            {
                Article = FindValue(sig, _settings.ArticleKeys),
                Description = FindValue(sig, _settings.DescriptionKeys),
            };

            string text = sig.ToMatchText();
            if (string.IsNullOrEmpty(result.Article) && _articleRegex != null)
            {
                // Not the layer: PERI layer names contain digit runs too.
                var m = _articleRegex.Match((sig.BlockName ?? "") + " | " + string.Join(" | ", sig.Properties.Select(p => p.Value)));
                if (m.Success)
                    result.Article = m.Value;
            }

            if (TryParseNumber(FindValue(sig, _settings.WeightKeys), out double w) && w > 0)
                result.WeightKg = w;

            foreach (var rule in _rules)
            {
                if (rule.Value.IsMatch(text))
                {
                    result.Kind = rule.Key;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(result.Article) && _catalog.TryGetValue(result.Article.Trim(), out var item))
            {
                if (TryParseKind(item.Kind, out var catalogKind))
                    result.Kind = catalogKind;
                if (item.WeightKg > 0)
                    result.WeightKg = item.WeightKg;
                if (string.IsNullOrEmpty(result.Description))
                    result.Description = item.Description;
            }

            if (string.IsNullOrEmpty(result.Description))
                result.Description = sig.BlockName;

            return result;
        }

        /// <summary>Weight to use for an element: known weight or a per-length/area default.</summary>
        public double DefaultWeightKg(ElementKind kind, double lengthMm, double widthMm)
        {
            double m = lengthMm / 1000.0;
            switch (kind)
            {
                case ElementKind.Standard: return _settings.StandardKgPerM * m;
                case ElementKind.Ledger: return _settings.LedgerKgPerM * m;
                case ElementKind.Deck: return _settings.DeckKgPerM2 * m * widthMm / 1000.0;
                case ElementKind.Diagonal: return _settings.DiagonalKgPerM * m;
                case ElementKind.Accessory: return _settings.AccessoryKgPerM * m;
                case ElementKind.Beam: return _settings.BeamKgPerM * m;
                default: return 0;
            }
        }

        private static string FindValue(ElementSignature sig, IEnumerable<string> keys)
        {
            if (keys == null)
                return null;
            var keyList = keys.Where(k => !string.IsNullOrEmpty(k)).ToList();
            foreach (var p in sig.Properties)
            {
                string key = (p.Key ?? "").ToUpperInvariant();
                // Match on the last path segment so "XDATA:PERI.ARTICLE" style keys work.
                if (keyList.Any(k => key.Contains(k.ToUpperInvariant())))
                    return p.Value;
            }

            return null;
        }

        public static bool TryParseNumber(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var m = Regex.Match(text, @"-?\d+(?:[.,]\d+)?");
            return m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
