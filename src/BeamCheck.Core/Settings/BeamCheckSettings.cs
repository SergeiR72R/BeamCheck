using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BeamCheck.Core.Settings
{
    /// <summary>
    /// All tunable values of the module. Stored as JSON next to the plugin DLL
    /// (or in %APPDATA%\BeamCheck) so that recognition rules, weights and tolerances
    /// can be adapted to a PERI CAD version without recompiling.
    /// </summary>
    [DataContract]
    public sealed class BeamCheckSettings
    {
        public BeamCheckSettings()
        {
            SetDefaults();
        }

        // ---------- Loads ----------

        /// <summary>Service load classes per EN 12811-1, table 3 (uniformly distributed load).</summary>
        [DataMember(Order = 1)] public List<LoadClassDefinition> LoadClasses { get; set; }

        [DataMember(Order = 2)] public int DefaultLoadClass { get; set; }

        /// <summary>"WorstLevelPlusAdjacent" or "AllLevels".</summary>
        [DataMember(Order = 3)] public string LevelMode { get; set; }

        /// <summary>Factor on service load of the governing (most loaded) level.</summary>
        [DataMember(Order = 4)] public double MainLevelFactor { get; set; }

        /// <summary>Factor on service load of the levels directly above/below the governing one.</summary>
        [DataMember(Order = 5)] public double AdjacentLevelFactor { get; set; }

        /// <summary>Factor on service load of all other levels.</summary>
        [DataMember(Order = 6)] public double OtherLevelFactor { get; set; }

        [DataMember(Order = 7)] public double GammaG { get; set; }

        [DataMember(Order = 8)] public double GammaQ { get; set; }

        /// <summary>Show design values (γG·G + γQ·Q) on the drawing instead of characteristic ones.</summary>
        [DataMember(Order = 9)] public bool ShowDesignValues { get; set; }

        // ---------- Tolerances (mm) ----------

        /// <summary>Max plan offset between stacked standard sections of one column.</summary>
        [DataMember(Order = 20)] public double ColumnXYTolerance { get; set; }

        /// <summary>Max plan distance between a ledger/diagonal end and the standard axis.</summary>
        [DataMember(Order = 21)] public double NodeTolerance { get; set; }

        /// <summary>Max plan distance between a deck bearing edge and the ledger axis.</summary>
        [DataMember(Order = 22)] public double DeckBearingTolerance { get; set; }

        /// <summary>Allowed range of (deck bottom − ledger axis Z).</summary>
        [DataMember(Order = 23)] public double DeckOverLedgerMin { get; set; }

        [DataMember(Order = 24)] public double DeckOverLedgerMax { get; set; }

        /// <summary>Extra plan tolerance beyond the beam half-width for a standard to count as standing on it.</summary>
        [DataMember(Order = 25)] public double BeamPlanTolerance { get; set; }

        /// <summary>Max gap between beam top and column bottom (base plates, jacks).</summary>
        [DataMember(Order = 26)] public double ColumnOnBeamZTolerance { get; set; }

        /// <summary>Decks whose bottom Z differ less than this belong to one level.</summary>
        [DataMember(Order = 27)] public double LevelZTolerance { get; set; }

        /// <summary>Plan radius around the selected standards in which ledgers/decks are searched.</summary>
        [DataMember(Order = 28)] public double SearchRadius { get; set; }

        // ---------- Weights ----------

        /// <summary>Fallback weights when neither the drawing nor the catalogue gives one.</summary>
        [DataMember(Order = 40)] public double StandardKgPerM { get; set; }

        [DataMember(Order = 41)] public double LedgerKgPerM { get; set; }

        [DataMember(Order = 42)] public double DeckKgPerM2 { get; set; }

        [DataMember(Order = 43)] public double DiagonalKgPerM { get; set; }

        [DataMember(Order = 44)] public double AccessoryKgPerM { get; set; }

        /// <summary>Article number → weight/kind overrides.</summary>
        [DataMember(Order = 45)] public List<CatalogItem> Catalog { get; set; }

        // ---------- Recognition ----------

        /// <summary>
        /// Ordered rules; the first regex that matches the object signature
        /// (block name, layer, entity type, attributes, XData…) decides the kind.
        /// </summary>
        [DataMember(Order = 60)] public List<RecognitionRule> Rules { get; set; }

        /// <summary>Property names (case-insensitive, substring) holding the article number.</summary>
        [DataMember(Order = 61)] public List<string> ArticleKeys { get; set; }

        /// <summary>Regex used to find an article number anywhere in the signature if no key matched.</summary>
        [DataMember(Order = 62)] public string ArticlePattern { get; set; }

        /// <summary>Property names (case-insensitive, substring) holding the weight in kg.</summary>
        [DataMember(Order = 63)] public List<string> WeightKeys { get; set; }

        /// <summary>Property names holding a human readable description.</summary>
        [DataMember(Order = 64)] public List<string> DescriptionKeys { get; set; }

        // ---------- Drawing output ----------

        /// <summary>Text height on paper, mm.</summary>
        [DataMember(Order = 80)] public double TextHeight { get; set; }

        /// <summary>Drawing scale denominator (50 → 1:50). Model-space text = TextHeight × Scale.</summary>
        [DataMember(Order = 81)] public double Scale { get; set; }

        [DataMember(Order = 82)] public string Layer { get; set; }

        /// <summary>Decimal separator used in the drawing/table ("," or ".").</summary>
        [DataMember(Order = 83)] public string DecimalSeparator { get; set; }

        /// <summary>Drawing unit → mm when INSUNITS is not set (1 = drawing in mm).</summary>
        [DataMember(Order = 84)] public double FallbackUnitToMm { get; set; }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) => SetDefaults();

        private void SetDefaults()
        {
            LoadClasses = new List<LoadClassDefinition>
            {
                new LoadClassDefinition(1, 0.75),
                new LoadClassDefinition(2, 1.50),
                new LoadClassDefinition(3, 2.00),
                new LoadClassDefinition(4, 3.00),
                new LoadClassDefinition(5, 4.50),
                new LoadClassDefinition(6, 6.00),
            };
            DefaultLoadClass = 3;
            LevelMode = LevelModes.WorstLevelPlusAdjacent;
            MainLevelFactor = 1.0;
            AdjacentLevelFactor = 0.5;
            OtherLevelFactor = 0.0;
            GammaG = 1.35;
            GammaQ = 1.5;
            ShowDesignValues = false;

            ColumnXYTolerance = 30;
            NodeTolerance = 120;
            DeckBearingTolerance = 120;
            DeckOverLedgerMin = -100;
            DeckOverLedgerMax = 200;
            BeamPlanTolerance = 150;
            ColumnOnBeamZTolerance = 600;
            LevelZTolerance = 150;
            SearchRadius = 5000;

            StandardKgPerM = 5.5;
            LedgerKgPerM = 3.5;
            DeckKgPerM2 = 22;
            DiagonalKgPerM = 3.5;
            AccessoryKgPerM = 3.0;
            Catalog = new List<CatalogItem>();

            // Short PERI codes are delimited by non-alphanumerics (underscore counts as a delimiter).
            // Defaults are educated guesses; adjust them to PERI CAD data after running PERIDUMP.
            Rules = new List<RecognitionRule>
            {
                new RecognitionRule("Deck", @"(?i)(deck|belag|plattform|platform|настил|(?<![A-Z0-9])(UD[IGPLA]|UAP)(?![A-Z]))"),
                new RecognitionRule("Diagonal", @"(?i)(diagonal|диагонал|(?<![A-Z0-9])(UBL|UVD)(?![A-Z]))"),
                new RecognitionRule("Accessory", @"(?i)(toe ?board|bordbrett|guard ?rail|gel[aä]nder|ограж|борт)"),
                new RecognitionRule("Ledger", @"(?i)(ledger|riegel|transom|ригел|леджер|(?<![A-Z0-9])(UH[A-Z]?|U[LX])(?![A-Z]))"),
                new RecognitionRule("Standard", @"(?i)(standard|vertical|stiel|vertikal|стойк|(?<![A-Z0-9])UV[RH](?![A-Z]))"),
            };
            ArticleKeys = new List<string> { "ARTICLE", "ARTIKEL", "ARTNR", "ART_NO", "ARTNO", "PARTNO", "АРТИКУЛ" };
            ArticlePattern = @"\b\d{6}\b";
            WeightKeys = new List<string> { "WEIGHT", "GEWICHT", "MASS", "MASSE", "ВЕС", "МАССА" };
            DescriptionKeys = new List<string> { "DESCRIPTION", "BEZEICHNUNG", "NAME", "ОПИСАНИЕ", "НАИМЕНОВАНИЕ" };

            TextHeight = 2.5;
            Scale = 50;
            Layer = "BEAMLOAD";
            DecimalSeparator = ",";
            FallbackUnitToMm = 1;
        }

        public double GetServiceLoad(int loadClass)
        {
            foreach (var c in LoadClasses)
                if (c.Class == loadClass)
                    return c.ServiceLoadKnPerM2;
            return 0;
        }
    }

    public static class LevelModes
    {
        public const string WorstLevelPlusAdjacent = "WorstLevelPlusAdjacent";
        public const string AllLevels = "AllLevels";
    }

    [DataContract]
    public sealed class LoadClassDefinition
    {
        public LoadClassDefinition()
        {
        }

        public LoadClassDefinition(int cls, double q)
        {
            Class = cls;
            ServiceLoadKnPerM2 = q;
        }

        [DataMember(Order = 1)] public int Class { get; set; }

        [DataMember(Order = 2)] public double ServiceLoadKnPerM2 { get; set; }
    }

    [DataContract]
    public sealed class RecognitionRule
    {
        public RecognitionRule()
        {
        }

        public RecognitionRule(string kind, string pattern)
        {
            Kind = kind;
            Pattern = pattern;
        }

        /// <summary>Name of <see cref="Model.ElementKind"/>.</summary>
        [DataMember(Order = 1)] public string Kind { get; set; }

        /// <summary>.NET regular expression matched against the object signature text.</summary>
        [DataMember(Order = 2)] public string Pattern { get; set; }
    }

    [DataContract]
    public sealed class CatalogItem
    {
        [DataMember(Order = 1)] public string Article { get; set; }

        /// <summary>Optional kind override (name of ElementKind).</summary>
        [DataMember(Order = 2, EmitDefaultValue = false)] public string Kind { get; set; }

        [DataMember(Order = 3)] public double WeightKg { get; set; }

        [DataMember(Order = 4, EmitDefaultValue = false)] public string Description { get; set; }
    }
}
