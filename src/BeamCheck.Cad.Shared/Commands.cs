using System;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Model;
using BeamCheck.Core.Recognition;
using BeamCheck.Core.Report;
using BeamCheck.Core.Settings;
using BeamCheck.Cad.UI;
#if BRICSCAD
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;
using CadApp = Bricscad.ApplicationServices.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace BeamCheck.Cad
{
    public sealed partial class Commands
    {
        /// <summary>
        /// Main command: pick a beam, pick standards, review loads in the dialog,
        /// then insert the 2D load scheme and the table into the drawing.
        /// </summary>
        [CommandMethod("BEAMLOAD", CommandFlags.Modal)]
        public void BeamLoad() => BeamLoadCore(false);

        /// <summary>
        /// Service command: the same selection as BEAMLOAD (beam, standards, upper beams), then no dialog: every part
        /// that entered the calculation is copied into a separate DWG file next to the drawing.
        /// </summary>
        [CommandMethod("BEAMEXPORT", CommandFlags.Modal)]
        public void BeamExport() => BeamLoadCore(true);

        private void BeamLoadCore(bool exportOnly)
        {
            var doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;
            var ed = doc.Editor;
            var db = doc.Database;

            BeamCheckSettings settings;
            try
            {
                settings = Plugin.LoadSettings(out _);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nFehler beim Lesen der BeamCheck-Einstellungen: " + ex.Message);
                return;
            }

            var classifier = new ElementClassifier(settings);
            double toMm = CadUnits.ToMm(db, settings);

            var peo = new PromptEntityOptions("\nTräger oder Riegel wählen: ");
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return;

            var pso = new PromptSelectionOptions { MessageForAdding = "\nStänder über dem Träger wählen (ein beliebiger Abschnitt je Ständer genügt): " };
            var psr = ed.GetSelection(pso);
            if (psr.Status != PromptStatus.OK)
                return;

            var upperPrompt = new PromptSelectionOptions
            {
                MessageForAdding = "\nObere Träger wählen, die auf diesen Ständern aufliegen und weitere Ständer tragen (Enter — nicht wählen, automatisch suchen): ",
            };
            var upperRes = ed.GetSelection(upperPrompt);
            var upperSel = upperRes.Status == PromptStatus.OK ? upperRes.Value.GetObjectIds() : null;

            var idMap = new Dictionary<string, ObjectId>();
            BeamLoadSession session;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var reader = new CadElementReader(tr, db, settings, classifier, toMm);
                var beamEnt = (Entity)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                var beam = reader.Read(beamEnt, ElementKind.Beam).FirstOrDefault();
                if (beam == null)
                {
                    ed.WriteMessage("\nDie Geometrie des Trägers konnte nicht ermittelt werden.");
                    return;
                }

                // The user may pick a beam or a ledger: what the part says it is (name, else shape) decides.
                var natural = reader.Read(beamEnt, null).FirstOrDefault();
                bool isLedger = natural != null && natural.Kind == ElementKind.Ledger;
                ed.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                    "\n{6}: {0}  Achse {1} → {2}  L={3:0} mm  Breite {4:0}  Oberkante Z={5:0}",
                    beam.DisplayName, beam.Start, beam.End, beam.PlanLength, beam.Width, beam.ZMax, isLedger ? "Riegel" : "Träger"));
                if (isLedger)
                    ed.WriteMessage("\n  als Riegel erkannt: Beläge, die darauf liegen, werden als einzelne Punkte D1… berücksichtigt.");

                var selected = new List<ScaffoldElement>();
                int shown = 0;
                foreach (SelectedObject so in psr.Value)
                {
                    var ent = (Entity)tr.GetObject(so.ObjectId, OpenMode.ForRead);
                    var found = reader.ReadPickedStandards(ent, out string info);
                    if (shown++ < 15)
                        ed.WriteMessage("\n  gewählt " + ent.Handle + " " + info + " → Ständer: " + found.Count +
                                        string.Concat(found.Take(3).Select(f => string.Format(CultureInfo.InvariantCulture, "  [X={0:0} Y={1:0} Z {2:0}..{3:0}]", f.Start.X, f.Start.Y, f.ZMin, f.ZMax))));
                    selected.AddRange(found);
                }

                if (selected.Count == 0)
                {
                    ed.WriteMessage("\nKeine Ständer gewählt.");
                    return;
                }

                // Beams standing on the columns (or on the stands above) pass their load down; the user may pick
                // them explicitly, the rest are found by name rules and by position.
                var manualBeams = new List<ScaffoldElement>();
                if (upperSel != null)
                {
                    foreach (var id in upperSel)
                    {
                        var ub = reader.Read((Entity)tr.GetObject(id, OpenMode.ForRead), ElementKind.Beam);
                        manualBeams.AddRange(ub);
                    }
                }

                var candidates = ScanCandidates(tr, db, reader, beam, selected, manualBeams, settings, toMm);
                foreach (var mb in manualBeams)
                    if (candidates.All(c => c.Id != mb.Id))
                        candidates.Add(mb);
                ed.WriteMessage($"\nElemente rund um die Ständer gefunden: {candidates.Count} " +
                                $"(Ständer {candidates.Count(c => c.Kind == ElementKind.Standard)}, " +
                                $"Riegel {candidates.Count(c => c.Kind == ElementKind.Ledger)}, " +
                                $"Beläge {candidates.Count(c => c.Kind == ElementKind.Deck)}).");

                session = new BeamLoadSession(settings, new AnalysisInput
                {
                    Beam = beam,
                    SelectedStandards = selected,
                    Candidates = candidates,
                    DirectDecks = isLedger,
                });
                if (isLedger)
                {
                    session.Options.BeamName = "Riegel " + beam.DisplayName;
                    session.Recalculate();
                }
                foreach (var kv in reader.TopLevelIds)
                    idMap[kv.Key] = kv.Value;
                LogRecognition(session, beam, selected, candidates);
                tr.Commit();
            }

            if (exportOnly)
            {
                session.Recalculate();
                ExportUsed(doc, session, idMap);
                return;
            }

            RunDialogLoop(doc, session, classifier, toMm, idMap);
        }

        /// <summary>Writes what was recognised around the picked standards (kind, article, name, extents) to BeamCheck.log.</summary>
        private static void LogRecognition(BeamLoadSession session, ScaffoldElement beam, List<ScaffoldElement> selected, List<ScaffoldElement> candidates)
        {
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string F(ScaffoldElement e) => string.Format(inv, "{0} {1} art={2} name={3} ({4:0},{5:0})→({6:0},{7:0}) Z {8:0}..{9:0} L={10:0} W={11:0}",
                    e.Id, e.Kind, e.Article, e.Description, e.Start.X, e.Start.Y, e.End.X, e.End.Y, e.ZMin, e.ZMax, e.PlanLength, e.Width);
                Plugin.Log("BEAMLOAD " + Plugin.BuildTag + ": beam " + F(beam));
                foreach (var e in selected)
                    Plugin.Log("  picked " + F(e));
                foreach (var e in candidates.Where(c => selected.Any(s => BeamCheck.Core.Geometry.Vec3.DistanceXY(s.Start, (c.Start + c.End) / 2) <= 3000)).OrderBy(c => c.Kind.ToString()))
                    Plugin.Log("  near " + F(e));
                foreach (var u in session.Topology.UpperBeams)
                    Plugin.Log(string.Format(inv, "  upper beam {0}: supports {1}, loaded {2}", u.Beam.Id, u.Supports.Count, u.Loaded.Count));
            }
            catch (System.Exception ex)
            {
                Plugin.Log("recognition log failed: " + ex.Message);
            }
        }

        private static void RunDialogLoop(Document doc, BeamLoadSession session, ElementClassifier classifier, double toMm, Dictionary<string, ObjectId> idMap)
        {
            var ed = doc.Editor;
            var db = doc.Database;

            while (true)
            {
                FormAction action;
                using (var form = new BeamLoadForm(session))
                {
                    CadApp.ShowModalDialog(form);
                    action = form.Action;
                }

                switch (action)
                {
                    case FormAction.Highlight:
                        var ids = session.Topology.UsedElementIds.Concat(session.ManualIds).Where(idMap.ContainsKey).Select(i => idMap[i]).Distinct().ToList();
                        ShowInModel(doc, ids);
                        break;

                    case FormAction.Exclude:
                    {
                        var res = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nObjekte wählen, die NICHT berücksichtigt werden sollen: " });
                        if (res.Status != PromptStatus.OK)
                            break;
                        var picked = new HashSet<ObjectId>(res.Value.GetObjectIds());
                        var excluded = idMap.Where(kv => picked.Contains(kv.Value)).Select(kv => kv.Key).ToList();
                        session.Exclude(excluded);
                        ed.WriteMessage($"\nElemente ausgeschlossen: {excluded.Count}.");
                        break;
                    }

                    case FormAction.Add:
                        AddObjects(doc, session, classifier, toMm, idMap);
                        break;

                    case FormAction.ExportDwg:
                        ExportUsed(doc, session, idMap);
                        break;

                    case FormAction.Insert:
                        EndIsolation(ed);
                        var pr = ed.GetPoint("\nEinfügepunkt für Schema und Tabelle: ");
                        if (pr.Status != PromptStatus.OK)
                            break;
                        var wcs = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                        ResultDrawer.Draw(db, session.Result, session.Settings, wcs, toMm);
                        ed.WriteMessage("\nLastschema und Tabelle eingefügt.");
                        return;

                    default:
                        EndIsolation(ed);
                        return;
                }
            }
        }

        private static bool s_isolated;

        /// <summary>
        /// Shows the objects that entered the calculation: highlighted, with the camera free to orbit, pan and zoom, and
        /// optionally isolated (everything else hidden) until "Alles zeigen". Returns when the user continues.
        /// </summary>
        private static void ShowInModel(Document doc, List<ObjectId> ids)
        {
            var ed = doc.Editor;
            var db = doc.Database;
            if (ids.Count == 0)
            {
                ed.WriteMessage("\nKeine berücksichtigten Objekte zum Anzeigen.");
                return;
            }

            while (true)
            {
                Highlight(db, ids, true);
                var kwo = new PromptKeywordOptions("\n" + ids.Count + " berücksichtigte Objekte markiert. Option [Drehen/Isolieren/Alles zeigen/Zoom/Weiter] <Weiter>: ") { AllowNone = true };
                kwo.Keywords.Add("Orbit", "Drehen", "Drehen");
                kwo.Keywords.Add("Isolate", "Isolieren", "Isolieren");
                kwo.Keywords.Add("Show", "Alles", "Alles zeigen");
                kwo.Keywords.Add("Zoom", "Zoom", "Zoom");
                kwo.Keywords.Add("Continue", "Weiter", "Weiter");
                kwo.Keywords.Default = "Continue";
                var res = ed.GetKeywords(kwo);
                Highlight(db, ids, false);
                if (res.Status != PromptStatus.OK || res.StringResult == "Continue")
                    return;

                try
                {
                    switch (res.StringResult)
                    {
                        case "Orbit":
                            // The objects stay selected while the view is turned (Enter or Esc ends the orbit).
                            ed.SetImpliedSelection(ids.ToArray());
                            ed.Command("_.3DORBIT");
                            ed.SetImpliedSelection(new ObjectId[0]);
                            break;

                        case "Isolate":
                            ed.SetImpliedSelection(ids.ToArray());
                            ed.Command("_.ISOLATEOBJECTS");
                            s_isolated = true;
                            break;

                        case "Show":
                            EndIsolation(ed);
                            break;

                        case "Zoom":
                            ZoomTo(ed, db, ids);
                            break;
                    }
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage("\nDer Befehl konnte nicht ausgeführt werden: " + ex.Message);
                }
            }
        }

        /// <summary>Brings the hidden objects back after an isolation (also before the scheme is inserted).</summary>
        private static void EndIsolation(Editor ed)
        {
            if (!s_isolated)
                return;
            try
            {
                ed.Command("_.UNISOLATEOBJECTS");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nUNISOLATEOBJECTS fehlgeschlagen: " + ex.Message);
            }

            s_isolated = false;
        }

        private static void ZoomTo(Editor ed, Database db, List<ObjectId> ids)
        {
            bool any = false;
            var ext = new Extents3d();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Entity ent))
                        continue;
                    try
                    {
                        ext.AddExtents(ent.GeometricExtents);
                        any = true;
                    }
                    catch (System.Exception)
                    {
                        // An object without extents does not take part in the zoom.
                    }
                }

                tr.Commit();
            }

            if (!any)
                return;
            var inv = ed.CurrentUserCoordinateSystem.Inverse();
            var a = ext.MinPoint.TransformBy(inv);
            var b = ext.MaxPoint.TransformBy(inv);
            ed.Command("_.ZOOM", "_W", a, b);
        }

        private static void AddObjects(Document doc, BeamLoadSession session, ElementClassifier classifier, double toMm, Dictionary<string, ObjectId> idMap)
        {
            var ed = doc.Editor;
            var kwo = new PromptKeywordOptions("\nWie sollen die hinzugefügten Objekte gewertet werden [Belag/Riegel/Ständer/Konsole/Auto]: ", "Deck Ledger Standard Console Auto");
            var kw = ed.GetKeywords(kwo);
            if (kw.Status != PromptStatus.OK)
                return;

            ElementKind? kind = null;
            switch (kw.StringResult)
            {
                case "Deck": kind = ElementKind.Deck; break;
                case "Ledger": kind = ElementKind.Ledger; break;
                case "Standard": kind = ElementKind.Standard; break;
                case "Console": kind = ElementKind.Console; break;
            }

            var res = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nObjekte zum Hinzufügen zur Berechnung wählen: " });
            if (res.Status != PromptStatus.OK)
                return;

            var added = new List<ScaffoldElement>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var reader = new CadElementReader(tr, doc.Database, session.Settings, classifier, toMm);
                foreach (var id in res.Value.GetObjectIds())
                    added.AddRange(reader.Read((Entity)tr.GetObject(id, OpenMode.ForRead), kind));
                foreach (var kv in reader.TopLevelIds)
                    idMap[kv.Key] = kv.Value;
                tr.Commit();
            }

            session.Add(added);
            ed.WriteMessage("\n" + session.StatusMessage + " Berechnung aktualisiert — Ergebnis im Dialog.");
        }

        /// <summary>All recognised elements in model space within the search radius of the selected standards.</summary>
        private static List<ScaffoldElement> ScanCandidates(Transaction tr, Database db, CadElementReader reader, ScaffoldElement beam,
            List<ScaffoldElement> selected, List<ScaffoldElement> extra, BeamCheckSettings settings, double toMm)
        {
            double r = settings.SearchRadius;
            var pts = selected.Select(s => s.Start).Concat(extra.SelectMany(b => new[] { b.Start, b.End })).ToList();
            double x0 = pts.Min(p => p.X) - r, x1 = pts.Max(p => p.X) + r;
            double y0 = pts.Min(p => p.Y) - r, y1 = pts.Max(p => p.Y) + r;
            double zMin = beam.ZMin - r;

            var result = new List<ScaffoldElement>();
            int noExtents = 0;
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent) || IsAnnotation(ent))
                    continue;
                // An object whose extents cannot be taken is not skipped silently: it is read anyway and
                // judged by its geometry later.
                Extents3d ext;
                bool hasExtents = true;
                try
                {
                    ext = ent.GeometricExtents;
                }
                catch (System.Exception)
                {
                    ext = default(Extents3d);
                    hasExtents = false;
                    noExtents++;
                }

                if (hasExtents &&
                    (ext.MaxPoint.X * toMm < x0 || ext.MinPoint.X * toMm > x1 ||
                     ext.MaxPoint.Y * toMm < y0 || ext.MinPoint.Y * toMm > y1 ||
                     ext.MaxPoint.Z * toMm < zMin))
                    continue;

                foreach (var e in reader.Read(ent))
                    if (e.Id != beam.Id)
                        result.Add(e);
            }

            if (noExtents > 0)
                CadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nObjekte ohne Ausdehnung (ohne Bereichsfilter gelesen): " + noExtents);
            return result;
        }

        private static bool IsAnnotation(Entity ent) =>
            ent is Dimension || ent is DBText || ent is MText || ent is Table || ent is Hatch || ent is Leader || ent is MLeader;

        private static void Highlight(Database db, IEnumerable<ObjectId> ids, bool on)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Entity ent))
                        continue;
                    if (on)
                        ent.Highlight();
                    else
                        ent.Unhighlight();
                }

                tr.Commit();
            }
        }

        /// <summary>Writes everything the module can read from the selected objects to a text report.</summary>
        [CommandMethod("PERIDUMP", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void PeriDumpCommand()
        {
            var doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;
            var ed = doc.Editor;
            var res = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nPERI-Objekte zur Analyse wählen (Träger, Ständer, Riegel, Belag): " });
            if (res.Status != PromptStatus.OK)
                return;

            var settings = Plugin.LoadSettings(out _);
            string path = PeriDump.Write(doc, res.Value.GetObjectIds(), settings);
            ed.WriteMessage($"\nBericht gespeichert: {path}\nBitte diese Datei an den Entwickler des Moduls senden, um die Erkennung anzupassen.");
            TryOpen(path);
        }

        [CommandMethod("BEAMLOADSETTINGS", CommandFlags.Modal)]
        public void OpenSettings()
        {
            var ed = CadApp.DocumentManager.MdiActiveDocument?.Editor;
            Plugin.LoadSettings(out string path);
            ed?.WriteMessage($"\nBeamCheck-Einstellungsdatei: {path}\nÄnderungen gelten beim nächsten Start von BEAMLOAD.");
            TryOpen(path);
        }

        private static void TryOpen(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.Exception)
            {
                // No associated program: the path was printed to the command line.
            }
        }
    }
}
