using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using BeamCheck.Core.Model;

namespace BeamCheck.Core.Spindles
{
    public enum PartKind
    {
        Other, Spindle, SpindleComponent, Standard, Ledger, Beam, Console, Deck, Diagonal, Accessory, BeamConnector
    }

    /// <summary>One recognised drawing part (display / selection data). All geometry in millimetres.
    /// The CAD side finds the drawing object to highlight by <see cref="Handle"/>.</summary>
    public sealed class PartInfo
    {
        public string Handle = "";        // element id from the reader ("1A2F" or "1A2F/3B" for nested parts)
        public string TopHandle = "";     // handle of the top-level drawing object
        public PartKind Kind;
        public string Article = "";
        public string Name = "";
        public double WeightKg;
        public bool WeightEstimated;
        public double MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        public double Ax, Ay, Az, Bx, By, Bz;   // axis (start / end) in mm

        public double CenterX { get { return 0.5 * (MinX + MaxX); } }
        public double CenterY { get { return 0.5 * (MinY + MaxY); } }
        public double CenterZ { get { return 0.5 * (MinZ + MaxZ); } }

        public string Label
        {
            get
            {
                string n = (Article.Length > 0 ? Article + " " : "") + Name;
                return n.Trim().Length > 0 ? n.Trim() : Kind.ToString();
            }
        }

        /// <summary>Display data of a part read by BeamCheck: plan rectangle around its axis, heights from its body.</summary>
        public static PartInfo FromElement(ScaffoldElement el)
        {
            PartKind kind;
            if (el.Kind == ElementKind.Unknown || !Enum.TryParse(el.Kind.ToString(), out kind)) kind = PartKind.Other;

            var a = el.Start; var b = el.End;
            double zMin = el.ZMin, zMax = el.ZMax;
            if (!(zMax > zMin)) { zMin = Math.Min(a.Z, b.Z); zMax = Math.Max(a.Z, b.Z); }

            // plan rectangle: axis a-b, width el.Width
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            double w = Math.Max(el.Width, 1.0);
            var xs = new double[4]; var ys = new double[4];
            if (len < 1.0)
            {
                double h = Math.Max(w, 50.0) / 2.0, cx = (a.X + b.X) / 2.0, cy = (a.Y + b.Y) / 2.0;
                xs[0] = cx - h; xs[1] = cx + h; xs[2] = cx - h; xs[3] = cx + h;
                ys[0] = cy - h; ys[1] = cy - h; ys[2] = cy + h; ys[3] = cy + h;
            }
            else
            {
                double px = -dy / len * w / 2.0, py = dx / len * w / 2.0;
                xs[0] = a.X + px; xs[1] = a.X - px; xs[2] = b.X + px; xs[3] = b.X - px;
                ys[0] = a.Y + py; ys[1] = a.Y - py; ys[2] = b.Y + py; ys[3] = b.Y - py;
            }

            return new PartInfo
            {
                Handle = el.Id,
                TopHandle = SpindleEngine.TopHandle(el.Id),
                Ax = a.X, Ay = a.Y, Az = a.Z, Bx = b.X, By = b.Y, Bz = b.Z,
                Kind = kind,
                Article = el.Article ?? "",
                Name = el.Description ?? "",
                WeightKg = el.WeightKg,
                WeightEstimated = el.WeightIsEstimated,
                MinX = xs.Min(), MaxX = xs.Max(), MinY = ys.Min(), MaxY = ys.Max(),
                MinZ = zMin, MaxZ = zMax,
            };
        }
    }

    /// <summary>One line of a spindle's load list (from BeamCheck's topology, or a manual load).</summary>
    public sealed class LoadRow
    {
        public string Kind = "";
        public string Label = "";
        public string Id = "";
        public double Share;
        public double Kn;          // weight share in kN (dead)
        public double AreaM2;      // deck area share (service load comes from BeamCheck per level)
        public string Type = "";   // BeamCheck contribution type
        public string Via = "";    // via beam / console
    }

    [DataContract]
    public sealed class ManualItem
    {
        [DataMember] public string Description;
        [DataMember] public double DeadKn;
        [DataMember] public double ServiceKn;
    }

    public sealed class SpindleInfo
    {
        public int Index;                 // 1-based number
        public string Key = "";           // element id of the spindle: stable across runs
        public PartInfo Core;
        public ScaffoldElement Element;
        public double X, Y, MinZ, MaxZ;
        public bool HasBelow;             // standard / spindle directly below (not a base spindle)
        public bool IsHead;               // nothing found standing on it in its column
        public double ColumnTopZ;
        public string Type = "";          // base / head / mid / solo

        // results (BeamCheck engine + manual)
        public double BaseGk, BaseQk, BaseFd;
        public double ManualDead, ManualService;
        public double DeadKn, ServiceKn, CharKn, DesignKn;
        public bool EstimatedWeights;

        public List<LoadRow> Rows = new List<LoadRow>();
        public List<string> Problems = new List<string>();   // things the user should look at
        public List<string> Info = new List<string>();       // BeamCheck notes (ℹ)
        public List<string> ChainKeys = new List<string>();  // spindles above in the same column (incl. linked) whose edits apply here
        public HashSet<string> ColumnIds = new HashSet<string>();   // element ids used in its columns
        public HashSet<string> UsedIds = new HashSet<string>();     // all element ids BeamCheck used
        public BeamCheck.Core.Analysis.BeamLoadSession Session;     // for the Rechenweg
        public bool Confirmed;
        public volatile bool Calculated;
        public bool Open;                 // open head spindle (nothing on top, not answered)
        public List<string> ComponentIds = new List<string>();      // element ids of the spindle and its nut / base plate / pin

        public void Recompute(double gammaG, double gammaQ)
        {
            DeadKn = BaseGk + ManualDead;
            ServiceKn = BaseQk + ManualService;
            CharKn = DeadKn + ServiceKn;
            DesignKn = BaseFd + gammaG * ManualDead + gammaQ * ManualService;
        }

        public string Status
        {
            get
            {
                if (Confirmed) return "OK (checked)";
                if (Open) return "OPEN HEAD";
                if (Problems.Count > 0) return "CHECK";
                return "";
            }
        }
    }

    public sealed class ScanResult
    {
        public List<PartInfo> Parts = new List<PartInfo>();          // load-relevant parts (no spindles)
        public List<PartInfo> SpindleCores = new List<PartInfo>();
        public List<PartInfo> SpindleComponents = new List<PartInfo>();
        public List<PartInfo> Unrecognized = new List<PartInfo>();
        public List<ScaffoldElement> Elements = new List<ScaffoldElement>();                 // everything BeamCheck can use
        public Dictionary<string, ScaffoldElement> ElementById = new Dictionary<string, ScaffoldElement>();
        public Dictionary<string, PartInfo> PartById = new Dictionary<string, PartInfo>();
        public int EstimatedWeights;
        public int Skipped;
        public string FirstError;
        public double UnitToMm = 1;
        public bool Cancelled;
        public List<string> Ignored = new List<string>();       // objects skipped before reading (reason)
        public List<string> SlowObjects = new List<string>();   // objects that took > 2 s to read

        public IEnumerable<PartInfo> AllParts { get { return Parts.Concat(SpindleCores).Concat(SpindleComponents).Concat(Unrecognized); } }

        /// <summary>Adds a part read by BeamCheck. Spindles become standards: BeamCheck builds its columns from standards.</summary>
        public void Add(ScaffoldElement el, bool isSpindle, bool isComponent)
        {
            if (ElementById.ContainsKey(el.Id)) return;
            if (isSpindle) el.Kind = ElementKind.Standard;
            Elements.Add(el);
            ElementById[el.Id] = el;
            var p = PartInfo.FromElement(el);
            PartById[el.Id] = p;
            if (isSpindle) { p.Kind = PartKind.Spindle; SpindleCores.Add(p); }
            else if (isComponent) { p.Kind = PartKind.SpindleComponent; SpindleComponents.Add(p); }
            else
            {
                if (p.WeightEstimated) EstimatedWeights++;
                Parts.Add(p);
            }
        }
    }

    public sealed class CalcResult
    {
        public List<SpindleInfo> Spindles = new List<SpindleInfo>();
        public ScanResult Scan;
        public int LoadClass;
        public double QKnPerM2;
        public double GammaG = 1.5, GammaQ = 1.5;
        public double ModelWeightKn;      // all parts (not excluded)
        public double BaseSpindlesKn;     // sum of dead load on base spindles
        public long Millis;
        public long ReadMillis;
        public bool Cancelled;
        public List<SpindleInfo> OpenHeads { get { return Spindles.Where(s => s.Open).ToList(); } }
        public int ProblemCount { get { return Spindles.Count(s => !s.Confirmed && (s.Open || s.Problems.Count > 0)); } }
        public SpindleInfo Max { get { return Spindles.OrderByDescending(s => s.DesignKn).FirstOrDefault(); } }
    }

    // ---------------- persisted user changes (sidecar file next to the DWG) ----------------

    [DataContract]
    public sealed class SpindleEdit
    {
        [DataMember] public string Key;
        [DataMember] public List<string> ExcludedHandles = new List<string>();   // legacy (V01)
        [DataMember] public List<ManualItem> Manual = new List<ManualItem>();
        [DataMember] public bool Confirmed;
        [DataMember] public string Note;
        [DataMember] public string HeadAnswer;                                 // null | "none" | "manual" | "link"
        [DataMember] public List<string> LinkHandles = new List<string>();     // drawing objects standing on this spindle (user)
        [DataMember] public List<string> AddedIds = new List<string>();        // parts added to this spindle's calculation (user)

        public bool IsEmpty { get { return !Confirmed && Manual.Count == 0 && HeadAnswer == null && LinkHandles.Count == 0 && AddedIds.Count == 0 && string.IsNullOrEmpty(Note); } }
    }

    /// <summary><c>&lt;drawing&gt;.spindleload.json</c> next to the DWG: every answer and change of the user (same file as V01-V05_yashu).</summary>
    [DataContract]
    public sealed class Sidecar
    {
        [DataMember] public List<SpindleEdit> Edits = new List<SpindleEdit>();
        [DataMember] public List<string> Excluded = new List<string>();        // element ids excluded from every calculation (in order)
        [DataMember] public List<int> ExcludedBatches = new List<int>();     // size of each "remove" action, for undo

        public SpindleEdit Get(string key, bool create)
        {
            var e = Edits.FirstOrDefault(x => x.Key == key);
            if (e == null && create) { e = new SpindleEdit { Key = key }; Edits.Add(e); }
            return e;
        }

        private void Fix()
        {
            if (Edits == null) Edits = new List<SpindleEdit>();
            if (Excluded == null) Excluded = new List<string>();
            if (ExcludedBatches == null) ExcludedBatches = new List<int>();
            foreach (var e in Edits)
            {
                if (e.Manual == null) e.Manual = new List<ManualItem>();
                if (e.LinkHandles == null) e.LinkHandles = new List<string>();
                if (e.AddedIds == null) e.AddedIds = new List<string>();
                if (e.ExcludedHandles != null)
                    foreach (var h in e.ExcludedHandles) if (!Excluded.Contains(h)) Excluded.Add(h);
                e.ExcludedHandles = new List<string>();
            }
        }

        public static Sidecar Load(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                    using (var fs = File.OpenRead(path))
                    {
                        var sc = (Sidecar)new DataContractJsonSerializer(typeof(Sidecar)).ReadObject(fs);
                        sc.Fix();
                        return sc;
                    }
            }
            catch (Exception) { /* corrupt file: start fresh */ }
            var n = new Sidecar(); n.Fix(); return n;
        }

        public void Save(string path)
        {
            if (path == null) return;
            Edits.RemoveAll(e => e.IsEmpty);
            using (var fs = File.Create(path))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true, "  "))
            {
                new DataContractJsonSerializer(typeof(Sidecar)).WriteObject(w, this);
                w.Flush();
            }
        }
    }
}
