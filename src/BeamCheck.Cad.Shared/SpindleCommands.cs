#if !BRICSCAD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using BeamCheck.Core.Report;
using BeamCheck.Core.Settings;
using BeamCheck.Core.Spindles;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

namespace BeamCheck.Cad
{
    public sealed partial class Commands
    {
        [CommandMethod("SPINDELLOAD", CommandFlags.Modal)]
        public void SpindelLoad() => SpindleWorkflow.SpindleLoadCommand();

        [CommandMethod("SPINDELDUMP", CommandFlags.Modal)]
        public void SpindelDump() => SpindleWorkflow.DumpCommand();

        [CommandMethod("SPINDELSETTINGS", CommandFlags.Modal)]
        public void SpindelSettings() => SpindleWorkflow.SettingsCommand();
    }

    /// <summary>
    /// SPINDELLOAD (former V01-V05_yashu plugin): the maximum load in every spindle of the drawing, calculated with BeamCheck's own
    /// engine (<see cref="SpindleEngine"/>), with a review of the spindles one by one, head-spindle questions, work zones,
    /// click-near / window picking, manual loads, Excel and labels.
    /// </summary>
    internal static class SpindleWorkflow
    {
        /// <summary>%APPDATA%\BeamCheck\BeamCheck.Spindle.settings.json (created from the copy next to the DLL).</summary>
        private static string UserSettingsPath => Path.Combine(Path.GetDirectoryName(Plugin.UserSettingsPath) ?? "", SpindleSettings.FileName);

        private static string BundledSettingsPath => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", SpindleSettings.FileName);

        private static SpindleSettings LoadSpindleSettings(out string path)
        {
            try
            {
                if (!File.Exists(UserSettingsPath) && File.Exists(BundledSettingsPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath));
                    File.Copy(BundledSettingsPath, UserSettingsPath);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log("copy " + BundledSettingsPath + " failed: " + ex.Message);
            }

            return SpindleSettings.LoadOrCreate(out path, UserSettingsPath, BundledSettingsPath);
        }

        private static string SidecarPath(Document doc)
        {
            try
            {
                var n = doc.Database.Filename;
                if (string.IsNullOrEmpty(n) || !File.Exists(n)) return null;
                return Path.ChangeExtension(n, ".spindleload.json");
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ state of one run
        private sealed class Run
        {
            public Document Doc;
            public SpindleSettings S;
            public BeamCheckSettings Bcs;
            public ScanResult Scan;
            public SpindleEngine Engine;
            public Dictionary<string, ObjectId> Ids = new Dictionary<string, ObjectId>();   // element id -> drawing object
            public Sidecar Sidecar;
            public string SidecarPath;
            public CalcResult Res;
            public bool Isolated;
            public List<Drawable> Markers = new List<Drawable>();
            public Editor Ed { get { return Doc.Editor; } }
            public double Unit { get { return Scan.UnitToMm; } }

            public void Persist() { try { Sidecar.Save(SidecarPath); } catch (Exception) { } }
            public SpindleEdit Edit(SpindleInfo sp) { return Sidecar.Get(sp.Key, true); }
            public SpindleInfo ByKey(string k) { return Res.Spindles.FirstOrDefault(x => x.Key == k); }
            public ObjectId Oid(string elementId) { ObjectId id; return elementId != null && Ids.TryGetValue(elementId, out id) ? id : ObjectId.Null; }
            public ObjectId Oid(PartInfo p) { return Oid(p.Handle); }

            public void RecalcNear(double x, double y)
            {
                var keys = Engine.KeysNear(Res, x, y);
                using (var pw = new SpindleProgressWindow("Recalculating " + keys.Count + " spindles around the change"))
                {
                    pw.Show();
                    Res = SpindleWorkflow.RunEngine(this, pw, keys);
                }
                Ed.WriteMessage("\n  Recalculated " + keys.Count + " spindle(s) around the change (" + (Res.Millis / 1000.0).ToString("0.0") + " s).");
            }
        }

        // =====================================================================
        //  SPINDELLOAD
        // =====================================================================
        public static void SpindleLoadCommand()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var run = new Run { Doc = doc };

            string spPath;
            try { run.S = LoadSpindleSettings(out spPath); }
            catch (Exception ex) { ed.WriteMessage("\nCannot read the spindle settings: " + ex.Message); return; }
            try
            {
                string bcPath;
                run.Bcs = Plugin.LoadSettings(out bcPath);
                ed.WriteMessage("\nSPINDELLOAD - BeamCheck " + Plugin.BuildTag + " engine, settings " + (bcPath ?? "(built-in defaults)") + ", " + (run.Bcs.Catalog != null ? run.Bcs.Catalog.Count : 0) + " articles with weights, spindle settings " + (spPath ?? "(built-in defaults)") + ".");
                Plugin.Log("SPINDELLOAD " + Plugin.BuildTag);
            }
            catch (Exception ex) { ed.WriteMessage("\nCannot read BeamCheck settings: " + ex.Message); return; }

            var pio = new PromptIntegerOptions("\nLoad class 1-6 <" + run.Bcs.DefaultLoadClass + ">: ")
            { AllowNone = true, LowerLimit = 1, UpperLimit = 6, DefaultValue = run.Bcs.DefaultLoadClass, UseDefaultValue = true };
            var pir = ed.GetInteger(pio);
            if (pir.Status != PromptStatus.OK && pir.Status != PromptStatus.None) return;
            int loadClass = pir.Status == PromptStatus.OK ? pir.Value : run.Bcs.DefaultLoadClass;

            // whole model or a part of it (big models: select only the area you need)
            var so = new PromptKeywordOptions("\nCalculate [All/Select] <All>: ", "All Select") { AllowNone = true };
            so.Keywords.Default = "All";
            var sr = ed.GetKeywords(so);
            if (sr.Status == PromptStatus.Cancel) return;
            ObjectId[] selected = null;
            if (sr.Status == PromptStatus.OK && sr.StringResult == "Select")
            {
                var ps = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect the area to calculate (window / crossing; include everything standing on these spindles): " });
                if (ps.Status != PromptStatus.OK) return;
                selected = ps.Value.GetObjectIds();
            }

            var pw = new SpindleProgressWindow("SPINDELLOAD – " + (selected == null ? "whole model" : selected.Length + " selected objects"));
            try
            {
                pw.Show();
                pw.Report("Reading the drawing (BeamCheck part reader)", 0, 0, true);
                var readClock = System.Diagnostics.Stopwatch.StartNew();
                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var scanner = new SpindleScanner(doc.Database, run.S, run.Bcs, tr);
                    Func<int, int, string, bool> prog = (i, n, what) => { pw.Report("Reading the drawing (BeamCheck part reader)", i, n, false, what); return !pw.Cancelled; };
                    run.Scan = selected == null ? scanner.ScanModelSpace(prog) : scanner.ScanEntities(selected, prog);
                    run.Ids = scanner.Ids;
                    tr.Commit();
                }
                if (run.Scan.Cancelled) { ed.WriteMessage("\nCancelled while reading the drawing."); return; }
                ed.WriteMessage("\nRead " + run.Scan.Elements.Count + " parts, " + run.Scan.SpindleCores.Count + " spindles in " + (readClock.ElapsedMilliseconds / 1000.0).ToString("0") + " s.");
                if (run.Scan.Ignored.Count > 0)
                {
                    ed.WriteMessage("\nIgnored (not scaffold parts / too big): " + run.Scan.Ignored.Count + " objects:");
                    foreach (var g in run.Scan.Ignored.GroupBy(x => x).OrderByDescending(g => g.Count()).Take(10)) ed.WriteMessage("\n   " + g.Count() + " x " + g.Key);
                    ed.WriteMessage("\n   (if scaffold parts are among them: raise MaxObjectSize / MaxBlockEntities in SPINDELSETTINGS)");
                }
                if (run.Scan.SlowObjects.Count > 0)
                {
                    ed.WriteMessage("\nSlow objects (> 2 s each, put them on a layer and add it to SkipLayersPattern to skip them next time):");
                    foreach (var slow in run.Scan.SlowObjects.Take(10)) ed.WriteMessage("\n   " + slow);
                }
                pw.Report("Preparing the calculation", 0, 0, true, run.Scan.Elements.Count + " parts");
                if (run.Scan.SpindleCores.Count == 0)
                {
                    ed.WriteMessage("\nNo spindles found (" + run.Scan.Elements.Count + " parts read). Run SPINDELDUMP on a spindle and check SpindlePattern in the settings.\n");
                    return;
                }
                run.SidecarPath = SidecarPath(doc);
                run.Sidecar = Sidecar.Load(run.SidecarPath);
                run.Engine = new SpindleEngine(run.Scan, run.S, run.Bcs, loadClass);
                if (run.Engine.GridSkipped > 0) ed.WriteMessage("\nIgnored " + run.Engine.GridSkipped + " part(s) with an impossible size.");
                run.Res = RunEngine(run, pw, null);
                run.Res.ReadMillis = readClock.ElapsedMilliseconds - run.Res.Millis;
                ed.WriteMessage("\nTimes: " + pw.Summary());
                if (run.Res.Cancelled) ed.WriteMessage("\nCancelled: spindles not yet calculated are marked 'Not calculated' in the list.");
            }
            finally { pw.Close(); pw.Dispose(); }
            if (run.Scan.Cancelled || run.Scan.SpindleCores.Count == 0) return;

            PrintSummary(run);

            try
            {
                ShowMarkers(run);
                if (run.Res.OpenHeads.Count > 0)
                {
                    var ho = new PromptKeywordOptions("\n" + run.Res.OpenHeads.Count + " head spindle(s) with nothing found on top. Answer them now one by one? [Yes/No] <Yes>: ", "Yes No") { AllowNone = true };
                    ho.Keywords.Default = "Yes";
                    var hr = ed.GetKeywords(ho);
                    if (hr.Status == PromptStatus.Cancel) return;
                    if (hr.Status == PromptStatus.None || hr.StringResult == "Yes") HeadPass(run);
                }
                MainLoop(run);
            }
            finally
            {
                EndIsolation(run);
                ClearMarkers(run);
                run.Persist();
            }
        }

        private static void PrintSummary(Run run)
        {
            var ed = run.Ed; var res = run.Res; var s = run.S;
            ed.WriteMessage("\n------------------------------------------------------------");
            ed.WriteMessage("\nSpindles: " + res.Spindles.Count + "  (" + res.Spindles.Count(x => x.Type == "head") + " head, " + res.Spindles.Count(x => x.Type == "base") + " base)   parts read: " + res.Scan.Elements.Count + "   unrecognised objects: " + res.Scan.Unrecognized.Count);
            ed.WriteMessage("\nLoad class " + res.LoadClass + " = " + s.Fmt(res.QKnPerM2) + " kN/m2,  gamma G " + s.Fmt(res.GammaG) + ", gamma Q " + s.Fmt(res.GammaQ) + "   (" + res.Millis + " ms)");
            ed.WriteMessage("\nModel weight " + s.Fmt(res.ModelWeightKn, "0.0") + " kN  -  dead load on spindles without anything below " + s.Fmt(res.BaseSpindlesKn, "0.0") + " kN" +
                            (res.ModelWeightKn > 0 ? "  (" + (res.BaseSpindlesKn / res.ModelWeightKn * 100).ToString("0") + " %; < 100 % = load not reaching a spindle, > 100 % = conservative uplift / double paths)" : ""));
            ed.WriteMessage("\nTo check: " + res.ProblemCount + " spindles  (open head spindles: " + res.OpenHeads.Count + ")");
            var m = res.Max;
            if (m != null) ed.WriteMessage("\nMAXIMUM: spindle S" + m.Index + "  Gk " + s.Fmt(m.DeadKn) + "  Qk " + s.Fmt(m.ServiceKn) + "  Fd " + s.Fmt(m.DesignKn) + " kN");
            ed.WriteMessage("\n------------------------------------------------------------\n");
        }

        /// <summary>Runs the engine on background threads while the progress window stays alive on the main thread.</summary>
        private static CalcResult RunEngine(Run run, SpindleProgressWindow pw, ICollection<string> keys)
        {
            var cts = new CancellationTokenSource();
            var eng = run.Engine;
            var prev = keys == null ? null : run.Res;
            var task = Task.Run(() => eng.Run(run.Sidecar, prev, keys, cts.Token));
            while (!task.Wait(80))
            {
                pw.Report(eng.Phase, eng.Done, eng.Total, false, eng.Total > 0 ? null : "starting …");
                if (pw.Cancelled && !cts.IsCancellationRequested) cts.Cancel();
            }
            pw.Report(eng.Phase, eng.Done, eng.Total, true);
            return task.Result;
        }

        private static string Header(Run run)
        {
            var res = run.Res; var s = run.S; var m = res.Max;
            return "Spindles: " + res.Spindles.Count + "     to check: " + res.ProblemCount + "     open head spindles: " + res.OpenHeads.Count + "     removed parts: " + run.Sidecar.Excluded.Count +
                   "\nMaximum: S" + (m != null ? m.Index.ToString() : "-") + "   Fd " + (m != null ? s.Fmt(m.DesignKn) : "-") + " kN      load class " + res.LoadClass + " (" + s.Fmt(res.QKnPerM2) + " kN/m²)" +
                   "\nModel weight " + s.Fmt(res.ModelWeightKn, "0.0") + " kN, on bottom spindles " + s.Fmt(res.BaseSpindlesKn, "0.0") + " kN" +
                   (res.ModelWeightKn > 0 ? " (" + (res.BaseSpindlesKn / res.ModelWeightKn * 100).ToString("0") + " %)" : "") +
                   "      red = open head spindle, yellow = BeamCheck warning, green = checked. Double-click = show in model.";
        }

        // =====================================================================
        //  main window loop
        // =====================================================================
        private static void MainLoop(Run run)
        {
            var ed = run.Ed;
            while (true)
            {
                EndIsolation(run);      // back in the list: whole model visible again
                ShowMarkers(run);
                var form = new SpindleListForm(run.Res, run.S, run.Sidecar, Header(run));
                AcApp.ShowModalDialog(form);
                var action = form.Action;
                var sp = form.SelectedKey != null ? run.ByKey(form.SelectedKey) : null;
                form.Dispose();
                if (action == ListAction.None || action == ListAction.Close) return;

                try
                {
                    switch (action)
                    {
                        case ListAction.Show: ShowZone(run, sp); break;
                        case ListAction.Details: Details(run, sp); break;
                        case ListAction.Link: LinkPick(run, sp, false); break;
                        case ListAction.NothingOnTop:
                            run.Edit(sp).HeadAnswer = "none"; run.Persist(); run.RecalcNear(sp.X, sp.Y); break;
                        case ListAction.AddParts: AddParts(run, sp); break;
                        case ListAction.RemoveParts: RemoveParts(run, sp); break;
                        case ListAction.Manual:
                            if (AskManual(run, run.Edit(sp))) { run.Persist(); run.RecalcNear(sp.X, sp.Y); }
                            break;
                        case ListAction.Clear:
                            {
                                var e = run.Edit(sp);
                                e.LinkHandles.Clear(); e.AddedIds.Clear(); e.Manual.Clear(); e.HeadAnswer = null;
                                run.Persist(); run.RecalcNear(sp.X, sp.Y);
                                break;
                            }
                        case ListAction.ToggleOk:
                            {
                                var e = run.Edit(sp); e.Confirmed = !e.Confirmed; sp.Confirmed = e.Confirmed; run.Persist();
                                break;
                            }
                        case ListAction.UndoRemove: UndoRemove(run); break;
                        case ListAction.Finish: Finish(run); return;
                    }
                }
                catch (Exception ex) { ed.WriteMessage("\nAction failed: " + ex.Message); }
            }
        }

        // =====================================================================
        //  head spindles one by one
        // =====================================================================
        private static void HeadPass(Run run)
        {
            var list = run.Res.OpenHeads.Select(x => x.Key).ToList();
            for (int k = 0; k < list.Count; k++)
            {
                var sp = run.ByKey(list[k]);
                if (sp == null || !sp.Open) continue;
                run.Ed.WriteMessage("\n\n=== HEAD SPINDLE S" + sp.Index + "  (" + (k + 1) + " of " + list.Count + ")  top at Z " + sp.MaxZ.ToString("0") + " mm: nothing found standing on it ===");
                if (!LinkPick(run, sp, true)) return;
            }
        }

        /// <summary>Work zone + "click near the part standing on it". Returns false if the user wants to stop.</summary>
        private static bool LinkPick(Run run, SpindleInfo sp, bool headPass)
        {
            var ed = run.Ed;
            Isolate(run, ZoneIds(run, sp, true));
            ZoomTo(run, sp.X, sp.Y, (sp.MaxZ + sp.ColumnTopZ) / 2, run.S.ZoneRadius);
            var hl = Highlight(run, sp.ComponentIds.Select(run.Oid));
            try
            {
                while (true)
                {
                    var o = new PromptKeywordOptions("\nS" + sp.Index + ": what stands on it? [Click/Window/Nothing/Manual/Skip" + (headPass ? "/Stop" : "") + "] <Click>: ",
                                                     "Click Window Nothing Manual Skip" + (headPass ? " Stop" : "")) { AllowNone = true };
                    o.Keywords.Default = "Click";
                    var r = ed.GetKeywords(o);
                    if (r.Status == PromptStatus.Cancel) return false;
                    string a = r.Status == PromptStatus.None ? "Click" : r.StringResult;
                    var e = run.Edit(sp);
                    switch (a)
                    {
                        case "Stop": return false;
                        case "Skip": return true;
                        case "Nothing": e.HeadAnswer = "none"; run.Persist(); run.RecalcNear(sp.X, sp.Y); return true;
                        case "Manual":
                            if (AskManual(run, e)) { e.HeadAnswer = "manual"; run.Persist(); run.RecalcNear(sp.X, sp.Y); return true; }
                            break;
                        case "Window":
                            {
                                var parts = WindowParts(run, "\nWindow over the spindle / vertical standing on S" + sp.Index + ": ")
                                    .Where(p => (p.Kind == PartKind.Spindle || p.Kind == PartKind.Standard) && p.MinZ >= sp.MaxZ - run.S.HeadTopTolerance && p.Handle != sp.Key).ToList();
                                var lowest = parts.OrderBy(p => p.MinZ).FirstOrDefault();
                                if (lowest == null) { ed.WriteMessage("\n  No spindle or vertical above S" + sp.Index + " in that window."); break; }
                                if (Confirm(run, new[] { lowest }, "Link " + lowest.Label + " (foot at Z " + lowest.MinZ.ToString("0") + ") to S" + sp.Index + "?"))
                                {
                                    AddLink(run, sp, lowest); return true;
                                }
                                break;
                            }
                        default:
                            {
                                var cands = run.Scan.SpindleCores.Concat(run.Scan.Parts.Where(p => p.Kind == PartKind.Standard))
                                    .Where(p => p.MinZ >= sp.MaxZ - run.S.HeadTopTolerance && p.Handle != sp.Key
                                             && Math.Abs(p.CenterX - sp.X) <= run.S.ZoneRadius * 2 && Math.Abs(p.CenterY - sp.Y) <= run.S.ZoneRadius * 2).ToList();
                                var picked = ClickNear(run, cands, "\nClick near the spindle / vertical standing on S" + sp.Index + ": ");
                                if (picked != null) { AddLink(run, sp, picked); return true; }
                                break;
                            }
                    }
                }
            }
            finally { Unhighlight(run, hl); }
        }

        private static void AddLink(Run run, SpindleInfo sp, PartInfo p)
        {
            var e = run.Edit(sp);
            if (!e.LinkHandles.Contains(p.TopHandle)) e.LinkHandles.Add(p.TopHandle);
            e.HeadAnswer = "link";
            run.Persist();
            run.RecalcNear(sp.X, sp.Y);
            var now = run.ByKey(sp.Key);
            run.Ed.WriteMessage("\n  Linked " + p.Label + " -> S" + sp.Index + " now Gk " + run.S.Fmt(now.DeadKn) + " kN, Fd " + run.S.Fmt(now.DesignKn) + " kN.");
        }

        // =====================================================================
        //  add / remove parts by window
        // =====================================================================
        private static void AddParts(Run run, SpindleInfo sp)
        {
            Isolate(run, ZoneIds(run, sp, false));
            ZoomTo(run, sp.X, sp.Y, (sp.MinZ + sp.ColumnTopZ) / 2, run.S.ZoneRadius);
            var hl = Highlight(run, HighlightIdsOf(run, sp));
            List<PartInfo> parts;
            try { parts = WindowParts(run, "\nWindow / crossing over the MISSING parts for S" + sp.Index + " (highlighted = already counted): "); }
            finally { Unhighlight(run, hl); }
            var known = parts.Where(p => run.Scan.ElementById.ContainsKey(p.Handle) && p.Handle != sp.Key && !sp.UsedIds.Contains(p.Handle)).ToList();
            var unknown = parts.Where(p => p.Kind == PartKind.Other && !run.Scan.ElementById.ContainsKey(p.Handle)).ToList();
            if (unknown.Count > 0) run.Ed.WriteMessage("\n  " + unknown.Count + " selected object(s) are not recognised as scaffold parts (use Manual load for them).");
            if (known.Count == 0) { run.Ed.WriteMessage("\n  Nothing new to add (selected parts are already counted or not recognised)."); return; }
            if (!Confirm(run, known, "Add these parts to S" + sp.Index + "?")) return;
            var e = run.Edit(sp);
            foreach (var p in known) if (!e.AddedIds.Contains(p.Handle)) e.AddedIds.Add(p.Handle);
            run.Persist();
            run.RecalcNear(sp.X, sp.Y);
            var now = run.ByKey(sp.Key);
            run.Ed.WriteMessage("\n  S" + sp.Index + " now Gk " + run.S.Fmt(now.DeadKn) + " kN, Fd " + run.S.Fmt(now.DesignKn) + " kN.");
        }

        private static void RemoveParts(Run run, SpindleInfo sp)
        {
            Isolate(run, ZoneIds(run, sp, false));
            ZoomTo(run, sp.X, sp.Y, (sp.MinZ + sp.ColumnTopZ) / 2, run.S.ZoneRadius);
            var hl = Highlight(run, HighlightIdsOf(run, sp));
            List<PartInfo> parts;
            try { parts = WindowParts(run, "\nWindow / crossing over the WRONG parts (removed from the whole calculation): "); }
            finally { Unhighlight(run, hl); }
            var known = parts.Where(p => run.Scan.ElementById.ContainsKey(p.Handle) && !run.Sidecar.Excluded.Contains(p.Handle)).ToList();
            if (known.Count == 0) { run.Ed.WriteMessage("\n  Nothing to remove."); return; }
            if (!Confirm(run, known, "Remove these parts from the calculation?")) return;
            foreach (var p in known) run.Sidecar.Excluded.Add(p.Handle);
            run.Sidecar.ExcludedBatches.Add(known.Count);
            run.Persist();
            double x = known.Average(p => p.CenterX), y = known.Average(p => p.CenterY);
            run.RecalcNear(x, y);
        }

        private static void UndoRemove(Run run)
        {
            var ex = run.Sidecar.Excluded;
            if (ex.Count == 0) { run.Ed.WriteMessage("\nNothing was removed."); return; }
            var batches = run.Sidecar.ExcludedBatches;
            int n = batches.Count > 0 ? Math.Min(batches[batches.Count - 1], ex.Count) : 1;
            if (batches.Count > 0) batches.RemoveAt(batches.Count - 1);
            var back = ex.Skip(ex.Count - n).ToList();
            ex.RemoveRange(ex.Count - n, n);
            run.Persist();
            var parts = back.Select(id => { PartInfo p; return run.Scan.PartById.TryGetValue(id, out p) ? p : null; }).Where(p => p != null).ToList();
            run.Ed.WriteMessage("\nBrought back " + n + " part(s)" + (parts.Count > 0 ? ": " + string.Join(", ", parts.Take(5).Select(p => p.Label)) + (parts.Count > 5 ? " ..." : "") : "") + ".");
            if (parts.Count > 0) run.RecalcNear(parts.Average(p => p.CenterX), parts.Average(p => p.CenterY));
        }

        private static bool Confirm(Run run, IEnumerable<PartInfo> parts, string question)
        {
            var list = parts.ToList();
            run.Ed.WriteMessage("\n  Found: " + string.Join(", ", list.GroupBy(p => p.Kind).Select(g => g.Count() + "x " + g.Key)) + "   (" + run.S.Fmt(list.Sum(p => p.WeightKg) * 9.81 / 1000) + " kN)");
            foreach (var p in list.Take(8)) run.Ed.WriteMessage("\n    " + p.Kind + "  " + Trunc(p.Label, 50) + "  " + p.WeightKg.ToString("0.#") + " kg");
            if (list.Count > 8) run.Ed.WriteMessage("\n    ...");
            var hl = Highlight(run, list.Select(run.Oid));
            try
            {
                var o = new PromptKeywordOptions("\n" + question + " [Yes/No] <Yes>: ", "Yes No") { AllowNone = true };
                o.Keywords.Default = "Yes";
                var r = run.Ed.GetKeywords(o);
                return r.Status == PromptStatus.None || (r.Status == PromptStatus.OK && r.StringResult == "Yes");
            }
            finally { Unhighlight(run, hl); }
        }

        /// <summary>Window / crossing selection; returns every recognised part of the selected objects (nested parts included).</summary>
        private static List<PartInfo> WindowParts(Run run, string message)
        {
            var po = new PromptSelectionOptions { MessageForAdding = message, AllowDuplicates = false };
            var r = run.Ed.GetSelection(po);
            if (r.Status != PromptStatus.OK) return new List<PartInfo>();
            var handles = new HashSet<string>(r.Value.GetObjectIds().Select(id => id.Handle.ToString()));
            return run.Scan.AllParts.Where(p => handles.Contains(p.TopHandle)).ToList();
        }

        // =====================================================================
        //  click near a part (screen distance to the part axis)
        // =====================================================================
        private static PartInfo ClickNear(Run run, List<PartInfo> candidates, string message)
        {
            var ed = run.Ed;
            if (candidates.Count == 0) { ed.WriteMessage("\n  No spindle / vertical found above it in the work zone - use Window or Manual."); return null; }
            var pr = ed.GetPoint(new PromptPointOptions(message) { AllowNone = true });
            if (pr.Status != PromptStatus.OK) return null;
            Matrix3d wcs2dcs;
            using (var view = ed.GetCurrentView())
                wcs2dcs = (Matrix3d.PlaneToWorld(view.ViewDirection) * Matrix3d.Displacement(view.Target - Point3d.Origin)
                           * Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)).Inverse();
            var click = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(wcs2dcs);
            double u = run.Unit;
            var ranked = candidates.Select(p =>
            {
                var a = new Point3d(p.Ax / u, p.Ay / u, p.Az / u).TransformBy(wcs2dcs);
                var b = new Point3d(p.Bx / u, p.By / u, p.Bz / u).TransformBy(wcs2dcs);
                return new { P = p, D = SegDist(click.X, click.Y, a.X, a.Y, b.X, b.Y) * u };
            }).Where(x => x.D <= run.S.PickTolerance).OrderBy(x => x.D).Select(x => x.P).Take(10).ToList();
            if (ranked.Count == 0) { ed.WriteMessage("\n  Nothing within " + run.S.PickTolerance.ToString("0") + " mm of the click. Click closer, or use Window."); return null; }

            for (int i = 0; ; i = (i + 1) % ranked.Count)
            {
                var p = ranked[i];
                var hl = Highlight(run, new[] { run.Oid(p) });
                try
                {
                    var o = new PromptKeywordOptions("\n  " + (i + 1) + "/" + ranked.Count + ": " + Trunc(p.Label, 45) + ", foot at Z " + p.MinZ.ToString("0") + " mm. [Accept/Next/Cancel] <Accept>: ", "Accept Next Cancel") { AllowNone = true };
                    o.Keywords.Default = "Accept";
                    var r = ed.GetKeywords(o);
                    if (r.Status == PromptStatus.Cancel || (r.Status == PromptStatus.OK && r.StringResult == "Cancel")) return null;
                    if (r.Status == PromptStatus.None || r.StringResult == "Accept") return p;
                }
                finally { Unhighlight(run, hl); }
            }
        }

        private static double SegDist(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / l2));
            double qx = ax + t * dx - px, qy = ay + t * dy - py;
            return Math.Sqrt(qx * qx + qy * qy);
        }

        // =====================================================================
        //  show / work zone
        // =====================================================================
        private static void ShowZone(Run run, SpindleInfo sp)
        {
            var ed = run.Ed;
            double radius = run.S.ZoneRadius;
            while (true)
            {
                Isolate(run, ZoneIds(run, sp, false, radius));
                ZoomTo(run, sp.X, sp.Y, (sp.MinZ + sp.ColumnTopZ) / 2, radius);
                var hl = Highlight(run, HighlightIdsOf(run, sp));
                ed.WriteMessage("\nS" + sp.Index + " [" + sp.Type + "]  Gk " + run.S.Fmt(sp.DeadKn) + "  Qk " + run.S.Fmt(sp.ServiceKn) + "  Fd " + run.S.Fmt(sp.DesignKn) + " kN   (" + sp.Rows.Count + " load items highlighted)");
                foreach (var p in sp.Problems) ed.WriteMessage("\n  ! " + Trunc(p, 150));
                var o = new PromptKeywordOptions("\nWork zone [Orbit/Wider/Smaller/All/Back] <Back>: ", "Orbit Wider Smaller All Back") { AllowNone = true };
                o.Keywords.Default = "Back";
                var r = ed.GetKeywords(o);
                Unhighlight(run, hl);
                if (r.Status != PromptStatus.OK || r.StringResult == "Back") return;
                switch (r.StringResult)
                {
                    case "Wider": radius *= 2; break;
                    case "Smaller": radius = Math.Max(500, radius / 2); break;
                    case "All": EndIsolation(run); return;
                    case "Orbit":
                        try
                        {
                            ed.SetImpliedSelection(HighlightIdsOf(run, sp).ToArray());
                            ed.Command("_.3DORBIT");
                            ed.SetImpliedSelection(new ObjectId[0]);
                        }
                        catch (Exception ex) { ed.WriteMessage("\n3DORBIT failed: " + ex.Message); }
                        break;
                }
            }
        }

        private static List<ObjectId> HighlightIdsOf(Run run, SpindleInfo sp)
        {
            var ids = sp.ComponentIds.Concat(sp.UsedIds).Concat(sp.ColumnIds).Select(run.Oid).Where(id => !id.IsNull);
            return ids.Distinct().Take(2000).ToList();
        }

        /// <summary>Everything in a box around the spindle (and above it).</summary>
        private static List<ObjectId> ZoneIds(Run run, SpindleInfo sp, bool aboveOnly, double radius = 0)
        {
            var s = run.S;
            if (radius <= 0) radius = s.ZoneRadius;
            double z0 = aboveOnly ? sp.MinZ - 100 : sp.MinZ - s.ZoneBelow;
            double z1 = Math.Max(sp.ColumnTopZ, sp.MaxZ) + s.ZoneAbove;
            return run.Scan.AllParts
                .Where(p => p.MaxX >= sp.X - radius && p.MinX <= sp.X + radius && p.MaxY >= sp.Y - radius && p.MinY <= sp.Y + radius && p.MaxZ >= z0 && p.MinZ <= z1)
                .Select(run.Oid).Where(id => !id.IsNull).Distinct().ToList();
        }

        private static void Isolate(Run run, List<ObjectId> ids)
        {
            if (ids.Count == 0) return;
            try
            {
                if (run.Isolated) { run.Ed.Command("_.UNISOLATEOBJECTS"); run.Isolated = false; }
                run.Ed.SetImpliedSelection(ids.ToArray());
                run.Ed.Command("_.ISOLATEOBJECTS");
                run.Ed.SetImpliedSelection(new ObjectId[0]);
                run.Isolated = true;
            }
            catch (Exception ex) { run.Ed.WriteMessage("\nISOLATEOBJECTS failed: " + ex.Message); }
        }

        private static void EndIsolation(Run run)
        {
            if (!run.Isolated) return;
            try { run.Ed.Command("_.UNISOLATEOBJECTS"); } catch (Exception) { }
            run.Isolated = false;
        }

        private static void ZoomTo(Run run, double xMm, double yMm, double zMm, double radiusMm)
        {
            try
            {
                var ed = run.Ed; double u = run.Unit;
                using (var view = ed.GetCurrentView())
                {
                    var wcs2dcs = (Matrix3d.PlaneToWorld(view.ViewDirection) * Matrix3d.Displacement(view.Target - Point3d.Origin)
                                   * Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)).Inverse();
                    var c = new Point3d(xMm / u, yMm / u, zMm / u).TransformBy(wcs2dcs);
                    double h = 2.4 * radiusMm / u;
                    double aspect = view.Width / view.Height;
                    view.CenterPoint = new Point2d(c.X, c.Y);
                    view.Height = h; view.Width = h * aspect;
                    ed.SetCurrentView(view);
                }
            }
            catch (Exception) { }
        }

        private static List<ObjectId> Highlight(Run run, IEnumerable<ObjectId> idsIn)
        {
            var ids = idsIn.Where(id => !id.IsNull && id.IsValid && !id.IsErased).Distinct().ToList();
            try
            {
                using (run.Doc.LockDocument())
                using (var tr = run.Doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (var id in ids) { try { var e = tr.GetObject(id, OpenMode.ForRead) as Entity; if (e != null) e.Highlight(); } catch (Exception) { } }
                    tr.Commit();
                }
            }
            catch (Exception) { }
            return ids;
        }

        private static void Unhighlight(Run run, List<ObjectId> ids)
        {
            try
            {
                using (run.Doc.LockDocument())
                using (var tr = run.Doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (var id in ids) { try { var e = tr.GetObject(id, OpenMode.ForRead) as Entity; if (e != null) e.Unhighlight(); } catch (Exception) { } }
                    tr.Commit();
                }
            }
            catch (Exception) { }
        }

        // =====================================================================
        //  markers (temporary circles, not saved in the drawing)
        // =====================================================================
        private static void ShowMarkers(Run run)
        {
            ClearMarkers(run);
            try
            {
                var tm = TransientManager.CurrentTransientManager;
                double u = run.Unit, r = run.S.MarkerRadius / u;
                var max = run.Res.Max;
                foreach (var sp in run.Res.Spindles)
                {
                    short color;
                    if (sp == max) color = 1;
                    else if (sp.Confirmed) continue;
                    else if (sp.Open) color = 1;
                    else if (sp.Problems.Count > 0) color = 2;
                    else continue;
                    var c = new Circle(new Point3d(sp.X / u, sp.Y / u, sp.MaxZ / u), Vector3d.ZAxis, r) { ColorIndex = color };
                    tm.AddTransient(c, TransientDrawingMode.DirectTopmost, 128, new IntegerCollection());
                    run.Markers.Add(c);
                    if (sp.Open)
                    {
                        var l = new Line(new Point3d(sp.X / u, sp.Y / u, sp.MaxZ / u), new Point3d(sp.X / u, sp.Y / u, sp.MaxZ / u + 4 * r)) { ColorIndex = color };
                        tm.AddTransient(l, TransientDrawingMode.DirectTopmost, 128, new IntegerCollection());
                        run.Markers.Add(l);
                    }
                }
            }
            catch (Exception) { }
        }

        private static void ClearMarkers(Run run)
        {
            try
            {
                var tm = TransientManager.CurrentTransientManager;
                foreach (var d in run.Markers) { try { tm.EraseTransient(d, new IntegerCollection()); d.Dispose(); } catch (Exception) { } }
            }
            catch (Exception) { }
            run.Markers.Clear();
        }

        // =====================================================================
        //  details, manual load
        // =====================================================================
        private static void Details(Run run, SpindleInfo sp)
        {
            var s = run.S;
            var lines = new List<string>
            {
                "SPINDLE S" + sp.Index + "  [" + sp.Type + "]   element " + sp.Key + "   X " + sp.X.ToString("0") + "  Y " + sp.Y.ToString("0") + "  Z " + sp.MinZ.ToString("0") + ".." + sp.MaxZ.ToString("0") + " mm",
                "Gk " + s.Fmt(sp.DeadKn) + " kN    Qk " + s.Fmt(sp.ServiceKn) + " kN    Fk " + s.Fmt(sp.CharKn) + " kN    Fd " + s.Fmt(sp.DesignKn) + " kN" +
                    (sp.ManualDead != 0 || sp.ManualService != 0 ? "    (manual: G " + s.Fmt(sp.ManualDead) + " / Q " + s.Fmt(sp.ManualService) + " kN)" : ""),
                ""
            };
            if (sp.Problems.Count > 0) { lines.Add("TO CHECK:"); lines.AddRange(sp.Problems.Select(p => "  ! " + p)); lines.Add(""); }
            if (sp.ChainKeys.Count > 1) lines.Add("Your changes on these spindles above also count here: " + string.Join(", ", sp.ChainKeys.Where(k => k != sp.Key).Select(k => { var o = run.ByKey(k); return o != null ? "S" + o.Index : k; })));
            lines.Add("LOAD ITEMS (largest first):");
            lines.Add(string.Format("  {0,-9} {1,-44} {2,6} {3,8} {4,7}  {5,-18} {6}", "Kind", "Part", "Share", "G kN", "A m2", "Type", "Via"));
            foreach (var r in sp.Rows)
                lines.Add(string.Format("  {0,-9} {1,-44} {2,5:0}% {3,8} {4,7}  {5,-18} {6}", r.Kind, Trunc(r.Label, 44), r.Share * 100, s.Fmt(r.Kn, "0.000"), r.AreaM2 > 0 ? s.Fmt(r.AreaM2, "0.00") : "", Trunc(r.Type, 18), Trunc(r.Via, 40)));
            lines.Add("");
            lines.Add("==================== BeamCheck calculation (Rechenweg) ====================");
            lines.Add("(the 'beam' here is the invisible support under the spindle foot)");
            try { if (sp.Session != null) lines.AddRange(CalculationBreakdown.Build(sp.Session)); }
            catch (Exception ex) { lines.Add("Rechenweg not available: " + ex.Message); }
            if (sp.Info.Count > 0) { lines.Add(""); lines.Add("BeamCheck notes:"); lines.AddRange(sp.Info.Select(i => "  " + i)); }
            using (var f = new SpindleTextForm("S" + sp.Index + " – calculation", lines)) AcApp.ShowModalDialog(f);
        }

        private static bool AskManual(Run run, SpindleEdit e)
        {
            var ed = run.Ed;
            var d = ed.GetDouble(new PromptDoubleOptions("\nDead load to add in kN <0>: ") { AllowNone = true, DefaultValue = 0, UseDefaultValue = true, AllowNegative = true });
            if (d.Status != PromptStatus.OK && d.Status != PromptStatus.None) return false;
            var q = ed.GetDouble(new PromptDoubleOptions("\nService load to add in kN <0>: ") { AllowNone = true, DefaultValue = 0, UseDefaultValue = true, AllowNegative = true });
            if (q.Status != PromptStatus.OK && q.Status != PromptStatus.None) return false;
            var t = ed.GetString(new PromptStringOptions("\nDescription: ") { AllowSpaces = true, DefaultValue = "manual load", UseDefaultValue = true });
            if (t.Status != PromptStatus.OK) return false;
            e.Manual.Add(new ManualItem { Description = t.StringResult, DeadKn = d.Status == PromptStatus.OK ? d.Value : 0, ServiceKn = q.Status == PromptStatus.OK ? q.Value : 0 });
            return true;
        }

        // =====================================================================
        //  Finish: Excel + labels
        // =====================================================================
        private static void Finish(Run run)
        {
            EndIsolation(run);
            run.Persist();
            string xlsx = WriteExcel(run);
            if (xlsx != null) run.Ed.WriteMessage("\nExcel written: " + xlsx);
            if (run.SidecarPath == null) run.Ed.WriteMessage("\nNote: the drawing is not saved - your changes were not stored. Save the DWG and run again to keep them.");
            var o = new PromptKeywordOptions("\nPlace load labels in the drawing (layer " + run.S.Layer + ")? [Yes/No] <Yes>: ", "Yes No") { AllowNone = true };
            o.Keywords.Default = "Yes";
            var r = run.Ed.GetKeywords(o);
            if (r.Status == PromptStatus.None || (r.Status == PromptStatus.OK && r.StringResult == "Yes")) PlaceLabels(run);
        }

        private static string WriteExcel(Run run)
        {
            var s = run.S; var res = run.Res;
            try
            {
                string dwg = run.Doc.Database.Filename;
                bool saved = !string.IsNullOrEmpty(dwg) && File.Exists(dwg);
                string dir = saved ? Path.GetDirectoryName(dwg) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string stem = saved ? Path.GetFileNameWithoutExtension(dwg) : "drawing";
                string path = Path.Combine(dir, stem + "_SPINDLELOAD.xlsx");

                var xb = new XlsxBuilder();
                var sh = xb.AddSheet("Spindles");
                sh.Rows.Add(new object[] { "No", "Type", "Element", "X mm", "Y mm", "Z foot mm", "Z top mm", "Gk kN", "Qk kN", "Fk kN", "Fd kN", "Manual G kN", "Manual Q kN", "Status", "Your changes", "To check" });
                foreach (var sp in res.Spindles)
                {
                    var e = run.Sidecar.Get(sp.Key, false);
                    string ch = e == null ? "" : string.Join(", ", new[]
                    {
                        e.LinkHandles.Count > 0 ? "linked " + string.Join(" ", e.LinkHandles) : null,
                        e.HeadAnswer == "none" ? "nothing on top" : null,
                        e.AddedIds.Count > 0 ? "+" + e.AddedIds.Count + " parts" : null,
                        e.Manual.Count > 0 ? string.Join("; ", e.Manual.Select(m => m.Description + " G" + m.DeadKn + "/Q" + m.ServiceKn)) : null
                    }.Where(x => x != null));
                    sh.Rows.Add(new object[] { sp.Index, sp.Type, sp.Key, Math.Round(sp.X), Math.Round(sp.Y), Math.Round(sp.MinZ), Math.Round(sp.MaxZ),
                        Math.Round(sp.DeadKn, 3), Math.Round(sp.ServiceKn, 3), Math.Round(sp.CharKn, 3), Math.Round(sp.DesignKn, 3),
                        Math.Round(sp.ManualDead, 3), Math.Round(sp.ManualService, 3), sp.Status, ch, string.Join(" | ", sp.Problems) });
                }
                sh.ColumnWidths.AddRange(new double[] { 6, 7, 12, 9, 9, 9, 9, 9, 9, 9, 9, 10, 10, 12, 30, 80 });

                var sum = xb.AddSheet("Summary");
                sum.AutoFilter = false;
                sum.Rows.Add(new object[] { "Drawing", saved ? dwg : "(not saved)" });
                sum.Rows.Add(new object[] { "Program", "BeamCheck " + Plugin.BuildTag + " SPINDELLOAD (BeamCheck engine)" });
                sum.Rows.Add(new object[] { "Load class", res.LoadClass });
                sum.Rows.Add(new object[] { "Service load kN/m2", res.QKnPerM2 });
                sum.Rows.Add(new object[] { "Gamma G / Gamma Q", res.GammaG + " / " + res.GammaQ });
                sum.Rows.Add(new object[] { "Spindles", res.Spindles.Count });
                var m = res.Max;
                if (m != null) sum.Rows.Add(new object[] { "Maximum", "S" + m.Index + "  Fd " + Math.Round(m.DesignKn, 2) + " kN  (Gk " + Math.Round(m.DeadKn, 2) + ", Qk " + Math.Round(m.ServiceKn, 2) + ")" });
                sum.Rows.Add(new object[] { "Model weight kN", Math.Round(res.ModelWeightKn, 2) });
                sum.Rows.Add(new object[] { "Dead load on bottom spindles kN", Math.Round(res.BaseSpindlesKn, 2) });
                sum.Rows.Add(new object[] { "Spindles to check", res.ProblemCount });
                sum.Rows.Add(new object[] { "Open head spindles", res.OpenHeads.Count });
                sum.Rows.Add(new object[] { "Parts removed by user", run.Sidecar.Excluded.Count });
                sum.ColumnWidths.AddRange(new double[] { 34, 70 });

                var rem = xb.AddSheet("Removed parts");
                rem.Rows.Add(new object[] { "Element", "Kind", "Part", "kg" });
                foreach (var id in run.Sidecar.Excluded)
                {
                    PartInfo p; run.Scan.PartById.TryGetValue(id, out p);
                    rem.Rows.Add(new object[] { id, p != null ? p.Kind.ToString() : "", p != null ? p.Label : "", p != null ? Math.Round(p.WeightKg, 2) : 0.0 });
                }
                rem.ColumnWidths.AddRange(new double[] { 14, 12, 50, 8 });
                xb.Save(path);
                return path;
            }
            catch (Exception ex)
            {
                run.Ed.WriteMessage("\nExcel export failed: " + ex.Message);
                return null;
            }
        }

        private static void PlaceLabels(Run run)
        {
            var doc = run.Doc; var res = run.Res; var s = run.S;
            var db = doc.Database;
            double unit = run.Unit;
            double h = s.TextHeight * s.Scale / unit;
            var max = res.Max;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(s.Layer))
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord { Name = s.Layer, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 1) };
                    lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true);
                }
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                foreach (ObjectId id in ms)
                {
                    var e = tr.GetObject(id, OpenMode.ForRead) as DBText;
                    if (e != null && string.Equals(e.Layer, s.Layer, StringComparison.OrdinalIgnoreCase)) { e.UpgradeOpen(); e.Erase(); }
                }
                foreach (var sp in res.Spindles)
                {
                    double v = s.ShowDesignValues ? sp.DesignKn : sp.CharKn;
                    var t = new DBText
                    {
                        Position = new Point3d(sp.X / unit + h * 0.6, sp.Y / unit + h * 0.6, sp.MinZ / unit),
                        Height = h,
                        TextString = "S" + sp.Index + ": " + s.Fmt(v, "0.0") + " kN" + (sp == max ? " (MAX)" : ""),
                        Layer = s.Layer,
                        ColorIndex = sp == max ? 1 : (sp.Open ? 1 : (sp.Problems.Count > 0 && !sp.Confirmed ? 2 : 3)),
                    };
                    ms.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
                }
                tr.Commit();
            }
            run.Ed.WriteMessage("\nLabels placed on layer " + s.Layer + " (" + (s.ShowDesignValues ? "design" : "characteristic") + " values; red = maximum / open head, yellow = to check).");
        }

        // =====================================================================
        //  helper commands
        // =====================================================================
        public static void DumpCommand()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            string path;
            var s = LoadSpindleSettings(out path);
            var w = Plugin.LoadSettings(out path);
            var r = ed.GetEntity("\nPick a part (spindle, standard, beam, deck ...): ");
            if (r.Status != PromptStatus.OK) return;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var line in new SpindleScanner(doc.Database, s, w, tr).Dump(r.ObjectId)) ed.WriteMessage("\n" + line);
                tr.Commit();
            }
            ed.WriteMessage("\n");
        }

        public static void SettingsCommand()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            string path = null;
            try { LoadSpindleSettings(out path); } catch (Exception ex) { ed.WriteMessage("\nCannot read the spindle settings: " + ex.Message); }
            path = path ?? UserSettingsPath;
            ed.WriteMessage("\nSpindle settings : " + path +
                            "\nLoad distribution, part recognition, weights, load classes and gammas come from BeamCheck (BEAMLOADSETTINGS, BeamCheck.weights.csv)." +
                            "\nEdit the spindle settings with a text editor; changes apply at the next SPINDELLOAD.\n");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception) { }
        }

        private static string Trunc(string t, int n)
        {
            if (t == null) return "";
            return t.Length <= n ? t : t.Substring(0, n - 1) + ".";
        }
    }
}
#endif
