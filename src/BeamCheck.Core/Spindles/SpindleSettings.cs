using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace BeamCheck.Core.Spindles
{
    /// <summary>
    /// Settings of SPINDELLOAD only (BeamCheck.Spindle.settings.json in %APPDATA%\BeamCheck, a copy is shipped next to the DLL).
    /// Everything else - part recognition, weights, load classes, gamma, level factors, all load-distribution
    /// tolerances - comes from BeamCheck.settings.json, exactly as BEAMLOAD / BEAMBATCH use it.
    /// </summary>
    [DataContract]
    public sealed class SpindleSettings
    {
        public const string FileName = "BeamCheck.Spindle.settings.json";

        /// <summary>Bumped when defaults change in a way old settings files must not keep.</summary>
        public const int CurrentVersion = 7;

        [DataMember] public int SettingsVersion;

        // ---- recognition ----
        [DataMember] public string SpindlePattern;         // the spindle / jack itself
        [DataMember] public string SupportEndPartPattern;  // nuts, base plates, pins ... (belong to a spindle)

        // ---- calculation ----
        [DataMember] public double VirtualSupportSize;     // size of the invisible support placed under each spindle foot (mm)
        [DataMember] public double HeadTopTolerance;       // something counts as "standing on" a head spindle within this height above it (mm)
        [DataMember] public double CandidateRadius;        // parts within this plan distance (grown along beams) are given to BeamCheck; 0 = BeamCheck SearchRadius (slow)
        [DataMember] public double CandidateBelow;         // ... and not lower than this below the spindle foot (mm)
        [DataMember] public int MaxThreads;                // 0 = all CPU cores
        [DataMember] public double MaxObjectSize;          // objects / parts larger than this (mm) are ignored (buildings, xlines ...)
        [DataMember] public int MaxBlockEntities;          // blocks with more objects than this are ignored (imported buildings, site models)
        [DataMember] public string SkipLayersPattern;      // regex: objects on matching layers are ignored (e.g. "(?i)gebäude|building|arch")
        [DataMember] public double PickReadRadius;         // "Pick": only objects within this plan distance of the picked parts are read (mm)

        // ---- editing tools ----
        [DataMember] public double ZoneRadius;             // work zone around a spindle: plan radius (mm)
        [DataMember] public double ZoneBelow;              // ... below the spindle foot (mm)
        [DataMember] public double ZoneAbove;              // ... above the top of its column (mm)
        [DataMember] public double PickTolerance;          // "click near a part": max. distance on screen, in model mm
        [DataMember] public double MarkerRadius;           // problem markers in the view (mm)

        // ---- output ----
        [DataMember] public double TextHeight;
        [DataMember] public double Scale;
        [DataMember] public string Layer;
        [DataMember] public string DecimalSeparator;
        [DataMember] public bool ShowDesignValues;

        public Regex SpindleRx, SupportEndRx;

        public SpindleSettings() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext c) { SetDefaults(); SettingsVersion = 0; }

        public void SetDefaults()
        {
            SettingsVersion = CurrentVersion;
            SpindlePattern = "(?i)(spindel|spindle|(?<![a-z])ujb(?![a-z])|fu(ß|ss)spindel|kopfspindel|schraubspindel)";
            SupportEndPartPattern = "(?i)(wirbelmutter|zapfen|fu(ß|ss)platte|base ?plate|(?<!\\d)(116761|116762|100480|100412|100409|100481|715198|019780|100159|130684|130681|136779)(?!\\d))";
            VirtualSupportSize = 200;
            HeadTopTolerance = 300;
            CandidateRadius = 3500; CandidateBelow = 600; MaxThreads = 0;
            MaxObjectSize = 150000; MaxBlockEntities = 20000; SkipLayersPattern = "";
            PickReadRadius = 15000;
            ZoneRadius = 3000; ZoneBelow = 1000; ZoneAbove = 4000;
            PickTolerance = 600;
            MarkerRadius = 350;
            TextHeight = 2.5; Scale = 50; Layer = "SPINDLELOAD"; DecimalSeparator = ","; ShowDesignValues = false;
        }

        public void Normalize()
        {
            var d = new SpindleSettings();
            if (string.IsNullOrEmpty(SpindlePattern)) SpindlePattern = d.SpindlePattern;
            if (string.IsNullOrEmpty(SupportEndPartPattern)) SupportEndPartPattern = d.SupportEndPartPattern;
            if (VirtualSupportSize <= 0) VirtualSupportSize = d.VirtualSupportSize;
            if (ZoneRadius <= 0) ZoneRadius = d.ZoneRadius;
            if (PickTolerance <= 0) PickTolerance = d.PickTolerance;
            if (MarkerRadius <= 0) MarkerRadius = d.MarkerRadius;
            if (string.IsNullOrEmpty(Layer)) Layer = d.Layer;
            if (MaxObjectSize <= 0) MaxObjectSize = d.MaxObjectSize;
            if (MaxBlockEntities <= 0) MaxBlockEntities = d.MaxBlockEntities;
            if (SkipLayersPattern == null) SkipLayersPattern = "";
            if (PickReadRadius <= 0) PickReadRadius = d.PickReadRadius;
            SpindleRx = new Regex(SpindlePattern, RegexOptions.CultureInvariant);
            SupportEndRx = new Regex(SupportEndPartPattern, RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Loads the first existing file of <paramref name="candidatePaths"/>; without one, defaults are written to the first path.
        /// A file from an older version (V01-V05_yashu: SettingsVersion 6 or lower) is kept as .bak and replaced by the new defaults.
        /// </summary>
        public static SpindleSettings LoadOrCreate(out string usedPath, params string[] candidatePaths)
        {
            SpindleSettings s = null;
            usedPath = null;
            foreach (var path in candidatePaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                usedPath = path;
                using (var fs = File.OpenRead(path))
                    s = (SpindleSettings)new DataContractJsonSerializer(typeof(SpindleSettings)).ReadObject(fs);
                if (s.SettingsVersion < CurrentVersion)
                {
                    try { File.Copy(path, path + ".v" + s.SettingsVersion + ".bak", true); } catch (Exception) { }
                    s = new SpindleSettings();
                    s.Save(path);
                }
                break;
            }

            if (s == null)
            {
                s = new SpindleSettings();
                foreach (var path in candidatePaths)
                {
                    if (string.IsNullOrEmpty(path))
                        continue;
                    if (s.Save(path)) { usedPath = path; break; }
                }
            }

            s.Normalize();
            return s;
        }

        public bool Save(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (var fs = File.Create(path))
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true, "  "))
                {
                    new DataContractJsonSerializer(typeof(SpindleSettings)).WriteObject(w, this);
                    w.Flush();
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        public string Fmt(double v, string format = "0.00")
        {
            string t = v.ToString(format, CultureInfo.InvariantCulture);
            return DecimalSeparator == "," ? t.Replace('.', ',') : t;
        }
    }
}
