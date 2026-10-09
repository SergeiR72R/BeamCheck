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

        /// <summary>Bumped when defaults change in a way old settings files must not keep.</summary>
        public const int CurrentVersion = 2;

        [DataMember(Order = 0)] public int SettingsVersion { get; set; }

        // ---------- Loads ----------

        /// <summary>Service load classes per EN 12811-1, table 3 (uniformly distributed load).</summary>
        [DataMember(Order = 1)] public List<LoadClassDefinition> LoadClasses { get; set; }

        [DataMember(Order = 2)] public int DefaultLoadClass { get; set; }

        /// <summary>"WorstLevelPlusAdjacent" or "AllLevels".</summary>
        [DataMember(Order = 3)] public string LevelMode { get; set; }

        /// <summary>Factor on service load of the governing (most loaded) level.</summary>
        [DataMember(Order = 4)] public double MainLevelFactor { get; set; }

        /// <summary>Factor on service load of the ONE level directly above or below the governing one (the larger load).</summary>
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

        /// <summary>How far above the stack top an upper beam may sit and still count as carried by it.</summary>
        [DataMember(Order = 30)] public double UpperBeamBearingTolerance { get; set; }

        /// <summary>Max vertical gap between stacked sections of one column.</summary>
        [DataMember(Order = 29)] public double ColumnGapTolerance { get; set; }

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

        /// <summary>Upper beams of unknown weight: 0 = not counted (a warning is shown).</summary>
        [DataMember(Order = 46)] public double BeamKgPerM { get; set; }

        /// <summary>Article number → weight/kind overrides.</summary>
        [DataMember(Order = 45)] public List<CatalogItem> Catalog { get; set; }

        // ---------- Recognition ----------

        /// <summary>
        /// Ordered rules; the first regex that matches the object signature
        /// (block name, layer, entity type, attributes, XData…) decides the kind.
        /// </summary>
        [DataMember(Order = 60)] public List<RecognitionRule> Rules { get; set; }

        /// <summary>Recognise unmatched objects by the shape of their axis lines (see ShapeAnalyzer).</summary>
        [DataMember(Order = 65)] public bool UseShapeRecognition { get; set; }

        /// <summary>Shorter parts (wedges, couplers, base plates) are ignored by shape recognition.</summary>
        [DataMember(Order = 66)] public double ShapeMinLength { get; set; }

        /// <summary>Max plan size of a standard (vertical part).</summary>
        [DataMember(Order = 67)] public double ShapeStandardMaxPlan { get; set; }

        [DataMember(Order = 68)] public double ShapeLedgerMaxDz { get; set; }

        [DataMember(Order = 69)] public double ShapeLedgerMaxWidth { get; set; }

        [DataMember(Order = 70)] public double ShapeDeckMaxDz { get; set; }

        [DataMember(Order = 71)] public double ShapeDeckMinWidth { get; set; }

        [DataMember(Order = 72)] public double ShapeDeckMaxWidth { get; set; }

        /// <summary>
        /// Regex on block names of PERI display blocks; group 1 = article number
        /// (PERI CAD 24: "PERI_132234_PartDisplayName_3D").
        /// </summary>
        [DataMember(Order = 73)] public string PeriDisplayBlockPattern { get; set; }

        /// <summary>Objects whose DXF class name matches are skipped (PERI part groups would double-count their parts).</summary>
        [DataMember(Order = 75)] public string IgnoreDxfPattern { get; set; }

        /// <summary>Suffix of the PERI display block that holds the part axis lines.</summary>
        [DataMember(Order = 74)] public string PeriAxisBlockSuffix { get; set; }

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
        private void OnDeserializing(StreamingContext context)
        {
            SetDefaults();
            SettingsVersion = 0; // a file without a version is older than any version this code knows
        }

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
            SettingsVersion = CurrentVersion;
            LevelMode = LevelModes.AllLevels;
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
            ColumnGapTolerance = 300;
            UpperBeamBearingTolerance = 400;
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
                new RecognitionRule("Beam", @"(?i)(gitterträger|gittertraeger|multiträger|multitraeger|träger|traeger|girder|балк|двутавр|(?<![A-Z0-9])(ULS|ULA|ELM|VT ?20|GT ?24|HEB|HEA|IPE|UPE)(?![A-Z]))"),
                new RecognitionRule("Deck", @"(?i)(deck|belag|plattform|platform|настил|(?<![A-Z0-9])(UD[IGPLA]|UAP)(?![A-Z]))"),
                new RecognitionRule("Diagonal", @"(?i)(diagonal|диагонал|(?<![A-Z0-9])(UBL|UVD)(?![A-Z]))"),
                new RecognitionRule("Accessory", @"(?i)(toe ?board|bordbrett|guard ?rail|gel[aä]nder|ограж|борт)"),
                new RecognitionRule("Ledger", @"(?i)(ledger|riegel|transom|ригел|леджер|(?<![A-Z0-9])(UH[A-Z]?|U[LX])(?![A-Z]))"),
                new RecognitionRule("Standard", @"(?i)(standard|vertical|stiel|vertikal|стойк|(?<![A-Z0-9])UV[RH](?![A-Z]))"),
            };
            // PERI CAD 24 library blocks carry attributes ART, PERI_Beschreibung, Gewicht.
            ArticleKeys = new List<string> { "PART:ARTNR", "ATTR:ART", "ARTICLE", "ARTIKEL", "ARTNR", "ART_NO", "ARTNO", "PARTNO", "АРТИКУЛ" };
            ArticlePattern = @"(?<![0-9])\d{6}(-\d+)?(?![0-9])";
            WeightKeys = new List<string> { "WEIGHT", "GEWICHT", "MASS", "MASSE", "ВЕС", "МАССА" };
            DescriptionKeys = new List<string> { "PART:NAME", "BESCHREIBUNG", "DESCRIPTION", "BEZEICHNUNG", "BLOCKTEXT", "ОПИСАНИЕ", "НАИМЕНОВАНИЕ" };

            UseShapeRecognition = true;
            ShapeMinLength = 250;
            ShapeStandardMaxPlan = 200;
            ShapeLedgerMaxDz = 150;
            ShapeLedgerMaxWidth = 80;
            ShapeDeckMaxDz = 80;
            ShapeDeckMinWidth = 150;
            ShapeDeckMaxWidth = 800;
            PeriDisplayBlockPattern = @"^PERI_(\d{6}(?:-\d+)?)_PartDisplayName_(.+)$";
            PeriAxisBlockSuffix = "Line";
            IgnoreDxfPattern = @"(?i)GRUPPE|_GROUP|MANAGER|DISPREP|DISP_REP";

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
