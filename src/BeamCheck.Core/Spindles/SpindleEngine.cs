using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;

namespace BeamCheck.Core.Spindles
{
    /// <summary>
    /// SPINDELLOAD engine (V03_yashu+): every spindle is calculated with BeamCheck's own analysis (ScaffoldAnalyzer + LoadCalculator,
    /// the same code as BEAMLOAD / BEAMBATCH). An invisible short support is placed under the spindle foot, the
    /// spindle is the "standard standing on it", and BeamCheck finds everything its column carries: upper beams,
    /// consoles, pin ledgers, decks on ledgers, level factors, design values ...
    ///
    /// User changes are BeamCheck inputs, exactly like the buttons in BEAMLOAD:
    ///   * Link  (what stands on a head spindle)  -> extra selected standards / forced parts
    ///   * Add   (parts window-selected)           -> forced parts (BeamLoadSession.Add)
    ///   * Remove                                  -> excluded ids (everywhere)
    ///   * Manual load                             -> added after BeamCheck (dead / service, with gammas)
    /// Changes made on a spindle also apply to every spindle below it in the same column (base spindle).
    /// </summary>
    public sealed class SpindleEngine
    {
        private readonly ScanResult _scan;
        private readonly SpindleSettings _s;
        private readonly BeamCheckSettings _bs;
        private readonly int _loadClass;
        private readonly Dictionary<long, List<ScaffoldElement>> _grid = new Dictionary<long, List<ScaffoldElement>>();
        private const double Cell = 2000;
        public int GridSkipped;
        private Dictionary<string, SpindleInfo> _byKey = new Dictionary<string, SpindleInfo>();

        public SpindleEngine(ScanResult scan, SpindleSettings s, BeamCheckSettings bs, int loadClass)
        {
            _scan = scan; _s = s; _bs = bs; _loadClass = loadClass;
            if (s.MaxThreads > 0) Threads = Math.Min(Threads, s.MaxThreads);
            foreach (var e in scan.Elements)
            {
                double w = Math.Max(e.Width, 0) / 2;
                long i0 = (long)Math.Floor((Math.Min(e.Start.X, e.End.X) - w) / Cell), i1 = (long)Math.Floor((Math.Max(e.Start.X, e.End.X) + w) / Cell);
                long j0 = (long)Math.Floor((Math.Min(e.Start.Y, e.End.Y) - w) / Cell), j1 = (long)Math.Floor((Math.Max(e.Start.Y, e.End.Y) + w) / Cell);
                if ((i1 - i0 + 1) * (j1 - j0 + 1) > 20000) { GridSkipped++; continue; }   // absurd size: never loop over millions of cells
                for (long i = i0; i <= i1; i++)
                    for (long j = j0; j <= j1; j++)
                    {
                        List<ScaffoldElement> l;
                        long k = Key(i, j);
                        if (!_grid.TryGetValue(k, out l)) { l = new List<ScaffoldElement>(); _grid[k] = l; }
                        l.Add(e);
                    }
            }
        }

        private static long Key(long i, long j) { return (i << 32) ^ (j & 0xffffffffL); }

        private IEnumerable<ScaffoldElement> Near(double x0, double y0, double x1, double y1)
        {
            var seen = new HashSet<ScaffoldElement>();
            for (long i = (long)Math.Floor(x0 / Cell); i <= (long)Math.Floor(x1 / Cell); i++)
                for (long j = (long)Math.Floor(y0 / Cell); j <= (long)Math.Floor(y1 / Cell); j++)
                {
                    List<ScaffoldElement> l;
                    if (_grid.TryGetValue(Key(i, j), out l)) foreach (var e in l) if (seen.Add(e)) yield return e;
                }
        }

        // =====================================================================================
        /// <summary>Full calculation (keys == null) or recalculation of some spindles of an existing result.</summary>
        // progress, read by the progress window (from the main thread while the calculation runs in the background)
        public volatile string Phase = "";
        public volatile int Done, Total;
        public int Threads = Math.Max(1, Environment.ProcessorCount);

        /// <summary>
        /// Picked parts to calculate besides the spindles (SPINDELLOAD "Pick"): element ids of any vertical part - spindle, UVA, UVR,
        /// UVH, UH-Zapfen, base plate … Each is calculated like a spindle: everything standing on it is collected.
        /// </summary>
        public HashSet<string> TargetIds = new HashSet<string>();

        public CalcResult Run(Sidecar sc, CalcResult previous = null, ICollection<string> keys = null, CancellationToken cancel = default(CancellationToken))
        {
            var sw = Stopwatch.StartNew();
            var excluded = new HashSet<string>(sc.Excluded);
            CalcResult res;
            if (previous == null)
            {
                Phase = "Preparing spindles"; Done = 0; Total = _scan.SpindleCores.Count;
                res = new CalcResult { Scan = _scan, LoadClass = _loadClass, QKnPerM2 = _bs.GetServiceLoad(_loadClass), GammaG = _bs.GammaG, GammaQ = _bs.GammaQ };
                res.Spindles = BuildSpindles();
            }
            else res = previous;
            _byKey = res.Spindles.ToDictionary(x => x.Key);
            _footCache.Clear();

            var todo = (keys == null ? res.Spindles : res.Spindles.Where(x => keys.Contains(x.Key))).OrderByDescending(x => x.MinZ).ToList();
            foreach (var sp in todo) sp.Calculated = false;

            // pass 1: all spindles in parallel (BeamCheck's engine has no shared state)
            Phase = "Calculating spindles (" + Threads + " CPU cores)"; Done = 0; Total = todo.Count;
            int done = 0;
            try
            {
                Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Threads, CancellationToken = cancel }, sp =>
                {
                    Safe(sp, sc, excluded, res);
                    Done = Interlocked.Increment(ref done);
                });
            }
            catch (OperationCanceledException) { }

            // pass 2: spindles carrying linked columns need the final result of the linked spindle -> top-down, one by one
            if (!cancel.IsCancellationRequested)
            {
                var linked = todo.Where(sp => sp.ChainKeys.Any(k => { var e = sc.Get(k, false); return e != null && e.LinkHandles.Count > 0; })).ToList();
                if (linked.Count > 0)
                {
                    Phase = "Linked columns"; Done = 0; Total = linked.Count;
                    int d2 = 0;
                    foreach (var sp in linked)
                    {
                        if (cancel.IsCancellationRequested) break;
                        Safe(sp, sc, excluded, res);
                        Done = ++d2;
                    }
                }
            }
            foreach (var sp in todo.Where(x => !x.Calculated))
            {
                sp.Problems.Clear(); sp.Problems.Add("Not calculated (cancelled).");
            }
            res.Cancelled = cancel.IsCancellationRequested;
            MarkCarriedSpindles(res);
            foreach (var sp in res.Spindles.Where(x => x.IsPicked && x.Calculated))
            {
                var above = res.Spindles.Where(o => o != sp && o.Open && (sp.UsedIds.Contains(o.Key) || sp.ColumnIds.Contains(o.Key))).ToList();
                if (above.Count > 0)
                    sp.Problems.Add("Open head spindle(s) above it: " + string.Join(", ", above.Select(o => "S" + o.Index)) + " - answer them so that what stands on them is counted here too.");
            }

            res.ModelWeightKn = LoadCalculator.KgToKn(_scan.Elements.Where(e => !excluded.Contains(e.Id)).Sum(e => e.WeightKg));
            res.BaseSpindlesKn = res.Spindles.Where(x => !x.HasBelow && x.IsSpindle).Sum(x => x.DeadKn);
            res.Millis = sw.ElapsedMilliseconds;
            Phase = "Done";
            return res;
        }

        private void Safe(SpindleInfo sp, Sidecar sc, HashSet<string> excluded, CalcResult res)
        {
            try { Analyze(sp, sc, excluded); sp.Calculated = true; }
            catch (Exception ex)
            {
                sp.Problems.Clear(); sp.Problems.Add("Calculation failed: " + ex.Message);
                sp.BaseGk = sp.BaseQk = sp.BaseFd = 0; sp.Recompute(res.GammaG, res.GammaQ);
                sp.Calculated = true;
            }
        }

        /// <summary>Spindles to recalculate after a change near (x, y): everything within the BeamCheck search radius.</summary>
        public List<string> KeysNear(CalcResult res, double x, double y, double extra = 0)
        {
            double r = (_s.CandidateRadius > 0 ? _s.CandidateRadius * 2 : _bs.SearchRadius) + extra + 1000;
            return res.Spindles.Where(sp => Math.Abs(sp.X - x) <= r && Math.Abs(sp.Y - y) <= r).Select(sp => sp.Key).ToList();
        }

        /// <summary>
        /// A spindle that is the foot of a stack carried by a stack transfer (it stands on a head spindle beside or above it) is not a
        /// bottom spindle: its load is in the spindle below already.
        /// </summary>
        private void MarkCarriedSpindles(CalcResult res)
        {
            var carried = new Dictionary<string, SpindleInfo>();
            foreach (var sp in res.Spindles.OrderByDescending(x => x.MinZ))   // the head right under a stack first
            {
                if (sp.Session == null) continue;
                foreach (var l in sp.Session.Topology.UpperBeams.Where(l => l.IsStackTransfer))
                    foreach (var run in l.Loaded)
                        foreach (var sec in run.Sections)
                            if (sec.Id != sp.Key && !carried.ContainsKey(sec.Id)) carried[sec.Id] = sp;
            }

            foreach (var sp in res.Spindles)
            {
                SpindleInfo below;
                bool onTransfer = carried.TryGetValue(sp.Key, out below);
                sp.HasBelow = sp.HasBelowInLine || onTransfer;
                sp.Type = sp.IsPicked && !sp.IsSpindle ? "picked" : sp.IsHead ? (sp.HasBelow ? "head" : "solo") : (sp.HasBelow ? "mid" : "base");
                sp.StandsOn = onTransfer ? below.Key : null;
            }
        }

        /// <summary>The direct load path of a spindle: its own column and the stacks standing on its head (stack transfers, also chained).</summary>
        public static List<Column> DirectColumns(Topology topo)
        {
            var cols = new List<Column>(topo.Columns);
            for (bool more = true; more;)
            {
                more = false;
                foreach (var l in topo.UpperBeams.Where(l => l.IsStackTransfer && l.Supports.Count > 0 && cols.Contains(l.Supports[0].Column)))
                    foreach (var run in l.Loaded)
                        if (!cols.Contains(run)) { cols.Add(run); more = true; }
            }
            return cols;
        }

        /// <summary>
        /// The standard a picked part stands for: a standard (spindle, UVA, UVR, UVH …) itself; another part (UH-Zapfen, base plate,
        /// nut …) the lowest standard standing on it or passing through it.
        /// </summary>
        public static ScaffoldElement FootOf(ScaffoldElement picked, IEnumerable<ScaffoldElement> elements)
        {
            if (picked == null || picked.Kind == ElementKind.Standard) return picked;
            var mid = (picked.Start + picked.End) / 2;
            double zLow = Math.Min(picked.ZMin, Math.Min(picked.Start.Z, picked.End.Z)), zHigh = Math.Max(picked.ZMax, Math.Max(picked.Start.Z, picked.End.Z));
            return elements.Where(e => e.Kind == ElementKind.Standard && e.Id != picked.Id && Vec3.DistanceXY((e.Start + e.End) / 2, mid) <= 100
                                       && e.ZMin >= zLow - 150 && e.ZMin <= zHigh + 300)
                           .OrderBy(e => e.ZMin).FirstOrDefault();
        }

        // -------------------------------------------------------------------------------------
        private List<SpindleInfo> BuildSpindles()
        {
            var list = new List<SpindleInfo>();
            var standards = _scan.Elements.Where(e => e.Kind == ElementKind.Standard).ToList();
            foreach (var core in _scan.SpindleCores)
            {
                ScaffoldElement el;
                if (!_scan.ElementById.TryGetValue(core.Handle, out el)) continue;
                var sp = new SpindleInfo
                {
                    Key = core.Handle, Core = core, Element = el, IsSpindle = true, IsPicked = TargetIds.Contains(core.Handle),
                    X = (el.Start.X + el.End.X) / 2, Y = (el.Start.Y + el.End.Y) / 2,
                    MinZ = Math.Min(el.ZMin, Math.Min(el.Start.Z, el.End.Z)), MaxZ = Math.Max(el.ZMax, Math.Max(el.Start.Z, el.End.Z)),
                };
                sp.ComponentIds.Add(core.Handle);
                double tol = Math.Max(_bs.ColumnXYTolerance, 30);
                sp.HasBelowInLine = Near(sp.X - tol, sp.Y - tol, sp.X + tol, sp.Y + tol).Any(o => o != el && o.Kind == ElementKind.Standard
                                   && Vec3.DistanceXY((o.Start + o.End) / 2, new Vec3(sp.X, sp.Y, 0)) <= tol
                                   && Math.Max(o.ZMax, Math.Max(o.Start.Z, o.End.Z)) <= sp.MinZ + 50
                                   && Math.Max(o.ZMax, Math.Max(o.Start.Z, o.End.Z)) >= sp.MinZ - _bs.ColumnGapTolerance);
                sp.HasBelow = sp.HasBelowInLine;
                list.Add(sp);
            }
            foreach (var id in TargetIds)
            {
                ScaffoldElement el; PartInfo part;
                if (list.Any(x => x.Key == id) || !_scan.ElementById.TryGetValue(id, out el) || !_scan.PartById.TryGetValue(id, out part)) continue;
                list.Add(new SpindleInfo
                {
                    Key = id, Core = part, Element = el, IsPicked = true, HasBelowInLine = true, HasBelow = true, Type = "picked",
                    X = (el.Start.X + el.End.X) / 2, Y = (el.Start.Y + el.End.Y) / 2,
                    MinZ = Math.Min(el.ZMin, Math.Min(el.Start.Z, el.End.Z)), MaxZ = Math.Max(el.ZMax, Math.Max(el.Start.Z, el.End.Z)),
                    ComponentIds = { id },
                });
            }
            foreach (var comp in _scan.SpindleComponents)
            {
                var sp = list.Where(x => Math.Abs(x.X - comp.CenterX) < 100 && Math.Abs(x.Y - comp.CenterY) < 100 && comp.MinZ < x.MaxZ + 300 && comp.MaxZ > x.MinZ - 300)
                             .OrderBy(x => Math.Abs(x.MinZ - comp.CenterZ)).FirstOrDefault();
                if (sp != null) sp.ComponentIds.Add(comp.Handle);
            }
            list = list.OrderBy(x => Math.Round(x.X / 100.0)).ThenBy(x => x.Y).ThenBy(x => x.MinZ).ToList();
            for (int i = 0; i < list.Count; i++) list[i].Index = i + 1;
            return list;
        }

        // -------------------------------------------------------------------------------------
        private BeamLoadSession Session(ScaffoldElement foot, string label, List<ScaffoldElement> forced, HashSet<string> excluded)
        {
            var el = foot;
            double fx = (el.Start.X + el.End.X) / 2, fy = (el.Start.Y + el.End.Y) / 2;
            double z = Math.Min(el.ZMin, Math.Min(el.Start.Z, el.End.Z)), h = _s.VirtualSupportSize / 2;
            var virt = new ScaffoldElement
            {
                Id = "SPINDLE-SUPPORT-" + el.Id, Kind = ElementKind.Beam, Description = "virtual support under " + label,
                Start = new Vec3(fx - h, fy, z), End = new Vec3(fx + h, fy, z), Width = _s.VirtualSupportSize,
                ZMin = z - 100, ZMax = z, WeightKg = 0
            };

            // candidates: box around the spindle (CandidateRadius), grown along every beam / console above the foot that
            // reaches into the box (columns standing far out on an RCS / SRU beam are still found), 2 rounds for beams on beams.
            // CandidateRadius <= 0: BeamCheck's SearchRadius, exactly like BEAMBATCH (slower).
            double R = _s.CandidateRadius > 0 ? _s.CandidateRadius : _bs.SearchRadius;
            double below = _s.CandidateRadius > 0 ? _s.CandidateBelow : _bs.SearchRadius;
            double x0 = Math.Min(el.Start.X, el.End.X) - R, x1 = Math.Max(el.Start.X, el.End.X) + R;
            double y0 = Math.Min(el.Start.Y, el.End.Y) - R, y1 = Math.Max(el.Start.Y, el.End.Y) + R;
            foreach (var f in forced)
            {
                x0 = Math.Min(x0, Math.Min(f.Start.X, f.End.X) - R); x1 = Math.Max(x1, Math.Max(f.Start.X, f.End.X) + R);
                y0 = Math.Min(y0, Math.Min(f.Start.Y, f.End.Y) - R); y1 = Math.Max(y1, Math.Max(f.Start.Y, f.End.Y) + R);
            }
            double zLow = z - below;
            if (_s.CandidateRadius > 0)
            {
                for (int round = 0; round < 2; round++)
                {
                    double nx0 = x0, nx1 = x1, ny0 = y0, ny1 = y1;
                    foreach (var b in Near(x0, y0, x1, y1))
                    {
                        if (b.Kind != ElementKind.Beam && b.Kind != ElementKind.Console) continue;
                        if (excluded.Contains(b.Id) || Math.Max(b.ZMax, Math.Max(b.Start.Z, b.End.Z)) < z) continue;
                        if (Math.Max(b.Start.X, b.End.X) < x0 || Math.Min(b.Start.X, b.End.X) > x1 || Math.Max(b.Start.Y, b.End.Y) < y0 || Math.Min(b.Start.Y, b.End.Y) > y1) continue;
                        nx0 = Math.Min(nx0, Math.Min(b.Start.X, b.End.X) - R); nx1 = Math.Max(nx1, Math.Max(b.Start.X, b.End.X) + R);
                        ny0 = Math.Min(ny0, Math.Min(b.Start.Y, b.End.Y) - R); ny1 = Math.Max(ny1, Math.Max(b.Start.Y, b.End.Y) + R);
                    }
                    if (nx0 == x0 && nx1 == x1 && ny0 == y0 && ny1 == y1) break;
                    x0 = nx0; x1 = nx1; y0 = ny0; y1 = ny1;
                }
            }
            var cand = Near(x0, y0, x1, y1)
                .Where(e => e.Id != el.Id && !excluded.Contains(e.Id)
                         && Math.Max(e.Start.X, e.End.X) >= x0 && Math.Min(e.Start.X, e.End.X) <= x1
                         && Math.Max(e.Start.Y, e.End.Y) >= y0 && Math.Min(e.Start.Y, e.End.Y) <= y1
                         && Math.Max(e.ZMax, Math.Max(e.Start.Z, e.End.Z)) >= zLow)
                .ToList();

            var input = new AnalysisInput { Beam = virt, SelectedStandards = new List<ScaffoldElement> { el }, Candidates = cand };
            foreach (var id in excluded) input.ExcludedIds.Add(id);
            var ses = new BeamLoadSession(_bs, input);
            ses.Options.LoadClass = _loadClass;
            var add = forced.Where(f => !excluded.Contains(f.Id)).ToList();
            if (add.Count > 0) ses.Add(add);      // = "Objekte hinzufügen" in BEAMLOAD
            else ses.Recalculate();
            return ses;
        }

        /// <summary>Load of a column standing on a picked (non-spindle) standard: calculated with BeamCheck at its own foot.</summary>
        private readonly ConcurrentDictionary<string, double[]> _footCache = new ConcurrentDictionary<string, double[]>();
        private double[] FootLoad(ScaffoldElement foot, HashSet<string> excluded)
        {
            double[] v;
            if (_footCache.TryGetValue(foot.Id, out v)) return v;
            var ses = Session(foot, foot.DisplayName, new List<ScaffoldElement>(), excluded);
            v = new[] { ses.Result.Points.Sum(p => p.Gk), ses.Result.Points.Sum(p => p.Qk), ses.Result.Points.Sum(p => p.Fd) };
            _footCache[foot.Id] = v;
            return v;
        }

        private void Analyze(SpindleInfo sp, Sidecar sc, HashSet<string> excluded)
        {
            sp.Problems.Clear(); sp.Info.Clear(); sp.Rows.Clear();
            var mine = sc.Get(sp.Key, false);
            sp.Confirmed = mine != null && mine.Confirmed;

            // pass 1: plain BeamCheck analysis; then follow the column for user changes on spindles above (max 4 rounds)
            var chain = new List<string> { sp.Key };
            BeamLoadSession ses = null;
            var forced = new List<ScaffoldElement>();
            for (int round = 0; round < 4; round++)
            {
                ses = Session(sp.Element, "spindle S" + sp.Index, forced, excluded);
                var colIds = new HashSet<string>(DirectColumns(ses.Topology).SelectMany(c => c.Sections).Select(e => e.Id));
                var newChain = new List<string> { sp.Key };
                foreach (var id in colIds)
                    if (id != sp.Key && _byKey.ContainsKey(id) && _byKey[id].MinZ >= sp.MinZ - 1) newChain.Add(id);
                var nForced = new List<ScaffoldElement>();
                foreach (var k in newChain)
                {
                    var e = sc.Get(k, false);
                    if (e == null) continue;
                    foreach (var id in e.AddedIds)
                    {
                        ScaffoldElement ae;
                        if (_scan.ElementById.TryGetValue(id, out ae) && !nForced.Contains(ae)) nForced.Add(ae);
                    }
                }
                bool same = nForced.Count == forced.Count && nForced.All(forced.Contains);
                chain = newChain;
                if (same) break;          // nothing new to give BeamCheck: one pass is enough
                forced = nForced;
            }
            sp.ChainKeys = chain;
            sp.Session = ses;

            // results: all columns on the virtual support are carried by this spindle
            var r = ses.Result;
            sp.BaseGk = r.Points.Sum(p => p.Gk);
            sp.BaseQk = r.Points.Sum(p => p.Qk);
            sp.BaseFd = r.Points.Sum(p => p.Fd);
            sp.EstimatedWeights = r.Points.Any(p => p.HasEstimatedWeights);
            var direct = DirectColumns(ses.Topology);
            sp.ColumnIds = new HashSet<string>(direct.SelectMany(c => c.Sections).Select(e => e.Id));
            sp.UsedIds = new HashSet<string>(ses.Topology.UsedElementIds);
            sp.ColumnTopZ = direct.Count > 0 ? direct.Max(c => c.ZTop) : sp.MaxZ;

            // linked columns (what stands on a head spindle of this chain): their complete load comes in on top
            foreach (var k in chain)
            {
                var e = sc.Get(k, false);
                if (e == null) continue;
                foreach (var hdl in e.LinkHandles)
                {
                    var group = _scan.Elements.Where(x => TopHandle(x.Id) == hdl && !excluded.Contains(x.Id)).ToList();
                    if (group.Count == 0) { sp.Problems.Add("Linked object " + hdl + " not found any more."); continue; }
                    double g, q, fd; string what;
                    var linkedSpindle = group.Select(x => { SpindleInfo o; return _byKey.TryGetValue(x.Id, out o) ? o : null; }).FirstOrDefault(o => o != null);
                    if (linkedSpindle != null)
                    {
                        if (linkedSpindle == sp || chain.Contains(linkedSpindle.Key)) continue;
                        g = linkedSpindle.DeadKn; q = linkedSpindle.ServiceKn; fd = linkedSpindle.DesignKn; what = "spindle S" + linkedSpindle.Index;
                    }
                    else
                    {
                        var foot = group.Where(x => x.Kind == ElementKind.Standard).OrderBy(x => x.ZMin).FirstOrDefault() ?? group.OrderBy(x => x.ZMin).First();
                        var v = FootLoad(foot, excluded);
                        g = v[0]; q = v[1]; fd = v[2]; what = foot.DisplayName;
                    }
                    sp.BaseGk += g; sp.BaseQk += q; sp.BaseFd += fd;
                    sp.Rows.Add(new LoadRow { Kind = "LINKED", Label = what + " and everything on it" + (k != sp.Key ? " (via S" + _byKey[k].Index + ")" : ""), Id = hdl, Share = 1, Kn = g, Type = "link (Q " + q.ToString("0.##") + " kN)" });
                }
            }

            // manual loads of this spindle and every spindle above it in the chain
            sp.ManualDead = 0; sp.ManualService = 0;
            foreach (var k in chain)
            {
                var e = sc.Get(k, false);
                if (e == null) continue;
                foreach (var m in e.Manual)
                {
                    sp.ManualDead += m.DeadKn; sp.ManualService += m.ServiceKn;
                    sp.Rows.Add(new LoadRow { Kind = "MANUAL", Label = m.Description + (k != sp.Key ? " (on S" + _byKey[k].Index + ")" : ""), Share = 1, Kn = m.DeadKn, Type = "manual (Q " + m.ServiceKn.ToString("0.##") + " kN)" });
                }
            }
            sp.Recompute(_bs.GammaG, _bs.GammaQ);

            // load list
            foreach (var g in ses.Topology.Contributions.GroupBy(c => c.Source.Id))
            {
                var c0 = g.First();
                sp.Rows.Add(new LoadRow
                {
                    Kind = c0.Source.Kind.ToString(), Label = c0.Source.DisplayName, Id = c0.Source.Id,
                    Share = g.Sum(c => c.Share), Kn = LoadCalculator.KgToKn(g.Sum(c => c.WeightKg)), AreaM2 = g.Sum(c => c.AreaM2),
                    Type = c0.Type.ToString(), Via = c0.ViaBeam != null ? c0.ViaBeam.DisplayName : (c0.Via != null ? c0.Via.DisplayName : "")
                });
            }
            sp.Rows = sp.Rows.OrderByDescending(x => x.Kn).ToList();

            // head spindle: nothing in line above it, no beam lying on it, no answer yet
            // (a picked part that is not a spindle is never a head: open head spindles above it are listed with it instead)
            sp.IsHead = sp.IsSpindle && sp.ColumnTopZ <= sp.MaxZ + _s.HeadTopTolerance && ses.Topology.UpperBeams.Count == 0;
            sp.Type = sp.IsPicked && !sp.IsSpindle ? "picked" : sp.IsHead ? (sp.HasBelow ? "head" : "solo") : (sp.HasBelow ? "mid" : "base");
            bool answered = chain.Select(k => sc.Get(k, false)).Any(e => e != null && (e.HeadAnswer != null || e.LinkHandles.Count > 0));
            sp.Open = sp.IsHead && !answered;
            if (sp.Open) sp.Problems.Add("Head spindle: nothing found standing on it - tell the program what it supports.");

            foreach (var w in r.Warnings.Distinct())
            {
                if (w.StartsWith("ℹ", StringComparison.Ordinal)) sp.Info.Add(w);
                else sp.Problems.Add(w);
            }
            if (sp.EstimatedWeights) sp.Info.Add("Some weights are estimated (article not in BeamCheck.weights.csv).");
        }

        public static string TopHandle(string id) { int i = id.IndexOf('/'); return i < 0 ? id : id.Substring(0, i); }
    }
}
