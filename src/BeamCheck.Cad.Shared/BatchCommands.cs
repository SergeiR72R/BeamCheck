using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;
using BeamCheck.Core.Batch;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Recognition;
using BeamCheck.Core.Report;
using BeamCheck.Core.Settings;
using BeamCheck.Cad.UI;
#if BRICSCAD
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;
using CadApp = Bricscad.ApplicationServices.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace BeamCheck.Cad
{
    public sealed partial class Commands
    {
        private const string NamesLayer = "BEAMCHECK_NAMEN";

        /// <summary>
        /// Batch run: select the model, every beam is found, named and checked without picking anything; the result is an Excel
        /// workbook (beams, supports and loads with distances from the left edge, pictures) and a list to find each beam in the model.
        /// </summary>
        [CommandMethod("BEAMBATCH", CommandFlags.Modal)]
        public void BeamBatch()
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

            var kwo = new PromptKeywordOptions("\nAuswahl [Alles/Wählen] <Alles>: ") { AllowNone = true };
            kwo.Keywords.Add("All", "Alles", "Alles");
            kwo.Keywords.Add("Select", "Wählen", "Wählen");
            kwo.Keywords.Default = "All";
            var kw = ed.GetKeywords(kwo);
            if (kw.Status != PromptStatus.OK && kw.Status != PromptStatus.None)
                return;
            var sel = kw.Status == PromptStatus.OK && kw.StringResult == "Select"
                ? ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nModell wählen (alle Teile, die geprüft werden sollen): " })
                : ed.SelectAll();
            if (sel.Status != PromptStatus.OK || sel.Value.Count == 0)
            {
                ed.WriteMessage("\nNichts gewählt.");
                return;
            }

            var ids = sel.Value.GetObjectIds();
            bool makeExcel, makeLabels, makePictures, makeNavis;
            int loadClass;
            using (var form = new BatchOptionsForm(settings, ids.Length))
            {
                if (CadApp.ShowModalDialog(form) != DialogResult.OK)
                    return;
                makeExcel = form.MakeExcel;
                makeLabels = form.MakeLabels;
                makePictures = form.MakePictures;
                makeNavis = form.MakeNavisworks;
                loadClass = form.LoadClass;
            }

            var classifier = new ElementClassifier(settings);
            double toMm = CadUnits.ToMm(db, settings);
            var elements = new List<ScaffoldElement>();
            var idMap = new Dictionary<string, ObjectId>();
            List<BeamReport> reports;
            var watch = Stopwatch.StartNew();

            using (var progress = new ProgressForm("BeamCheck — Berechnung läuft"))
            {
                progress.Show();
                bool aborted = false;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var reader = new CadElementReader(tr, db, settings, classifier, toMm);
                    int n = 0;
                    foreach (var id in ids)
                    {
                        if (++n % 5 == 0 || n == ids.Length)
                            progress.Report("Objekte lesen", n, ids.Length, null);
                        if (progress.Cancelled)
                        {
                            aborted = true;
                            break;
                        }

                        try
                        {
                            if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent) || IsAnnotation(ent))
                                continue;
                            elements.AddRange(reader.Read(ent));
                        }
                        catch (System.Exception ex)
                        {
                            Plugin.Log("BEAMBATCH: object " + id + " skipped: " + ex.Message);
                        }
                    }

                    foreach (var kv in reader.TopLevelIds)
                        idMap[kv.Key] = kv.Value;
                    tr.Commit();
                }

                if (aborted)
                {
                    ed.WriteMessage("\nAbgebrochen.");
                    return;
                }

                int beamCount = BatchAnalyzer.FindBeams(elements).Count;
                if (beamCount == 0)
                {
                    progress.Close();
                    ed.WriteMessage("\nKeine Träger erkannt (" + elements.Count + " Teile gelesen). Namensregeln oder Artikeltabelle prüfen.");
                    return;
                }

                reports = BatchAnalyzer.Run(elements, settings, new BatchOptions { LoadClass = loadClass },
                    p => progress.Report("Träger berechnen", p.Done, p.Total, p.Current), () => progress.Cancelled);
                progress.Close();
            }

            Plugin.Log(string.Format(CultureInfo.InvariantCulture, "BEAMBATCH {0}: {1} Objekte, {2} Teile, {3} Träger in {4:0.0} s",
                Plugin.BuildTag, ids.Length, elements.Count, reports.Count, watch.Elapsed.TotalSeconds));
            foreach (var r in reports)
            {
                Plugin.Log(string.Format(CultureInfo.InvariantCulture, "  {0} {1} {2} L={3:0} Z {4:0}..{5:0} parts {6}", r.Name, r.Description, r.Article, r.Length, r.ZMin, r.ZMax, string.Join("+", r.PartIds)));
                foreach (var e in r.Events)
                    Plugin.Log(string.Format(CultureInfo.InvariantCulture, "      {0} {1} at {2:0} mm: {3}", e.Kind, e.Label, e.Position, e.Detail));
            }

            if (reports.Count == 0)
                return;

            string excelPath = null;
            if (makeExcel)
                excelPath = SaveWorkbook(db, settings, reports, ed);
            if (makeLabels)
                InsertNames(db, settings, reports, elements, toMm);
            if (makePictures)
                InsertPictures(doc, settings, reports, toMm, reports);

            if (makeNavis)
                WriteNavisworks(db, reports, excelPath, ed);
            var rows = ToRows(reports);
            WriteIndex(db, rows, ed);
            ed.WriteMessage(string.Format(CultureInfo.InvariantCulture, "\nBeamCheck: {0} Träger berechnet in {1:0} s.", reports.Count, watch.Elapsed.TotalSeconds) +
                            (excelPath != null ? "\nExcel: " + excelPath : ""));

            while (true)
            {
                BatchAction action;
                int index;
                string search;
                using (var form = new BatchResultForm(rows, true, excelPath))
                {
                    CadApp.ShowModalDialog(form);
                    action = form.DialogResult == DialogResult.OK ? form.Action : BatchAction.Close;
                    index = form.SelectedIndex;
                    search = form.SearchText;
                }

                if (action == BatchAction.Close)
                    return;
                switch (action)
                {
                    case BatchAction.Show:
                        if (index >= 0)
                            ShowInModel(doc, reports[index].Related.Where(idMap.ContainsKey).Select(i => idMap[i]).Distinct().ToList());
                        break;
                    case BatchAction.Illustrate:
                        if (index >= 0)
                            InsertPictures(doc, settings, new List<BeamReport> { reports[index] }, toMm, reports);
                        break;
                    case BatchAction.IllustrateAll:
                        InsertPictures(doc, settings, reports, toMm, reports);
                        break;
                    case BatchAction.OpenExcel:
                        TryOpen(excelPath, ed);
                        break;
                }
            }
        }

        /// <summary>Opens the list of beams of this drawing (from the last BEAMBATCH run) and shows the chosen one in the model.</summary>
        [CommandMethod("BEAMFIND", CommandFlags.Modal)]
        public void BeamFind()
        {
            var doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;
            var ed = doc.Editor;
            var db = doc.Database;

            var rows = ReadIndex(db);
            if (rows == null || rows.Count == 0)
            {
                ed.WriteMessage("\nKeine Trägerliste zu dieser Zeichnung. Zuerst BEAMBATCH ausführen (Zeichnung speichern, damit die Liste neben der Datei abgelegt wird).");
                return;
            }

            var opt = new PromptStringOptions("\nTrägername (z. B. B-014) oder Handle, Enter = Liste: ") { AllowSpaces = false };
            var res = ed.GetString(opt);
            if (res.Status != PromptStatus.OK && res.Status != PromptStatus.None)
                return;
            string text = (res.StringResult ?? "").Trim();

            while (true)
            {
                BeamRow row = null;
                if (text.Length > 0)
                {
                    row = rows.FirstOrDefault(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase))
                          ?? rows.FirstOrDefault(r => r.Handle.Split('+').Any(h => string.Equals(h.Trim(), text, StringComparison.OrdinalIgnoreCase)));
                    if (row == null)
                    {
                        ed.WriteMessage("\nTräger „" + text + "“ nicht gefunden — die Liste wird geöffnet.");
                        text = "";
                    }
                }

                if (row == null)
                {
                    int index;
                    using (var form = new BatchResultForm(rows, false, null))
                    {
                        CadApp.ShowModalDialog(form);
                        if (form.DialogResult != DialogResult.OK || form.Action != BatchAction.Show)
                            return;
                        index = form.SelectedIndex;
                    }

                    if (index < 0)
                        return;
                    row = rows[index];
                }

                var found = new List<ObjectId>();
                foreach (var h in row.Handle.Split('+'))
                {
                    try
                    {
                        var oid = db.GetObjectId(false, new Handle(Convert.ToInt64(h.Trim(), 16)), 0);
                        if (!oid.IsNull && !oid.IsErased)
                            found.Add(oid);
                    }
                    catch (System.Exception)
                    {
                        // A handle that no longer exists is skipped.
                    }
                }

                if (found.Count == 0)
                    ed.WriteMessage("\nDer Träger " + row.Name + " (Handle " + row.Handle + ") ist in der Zeichnung nicht mehr vorhanden.");
                else
                    ShowInModel(doc, found);
                text = "";
                // After showing, the list opens again for the next beam (Enter/Esc in the list ends the command).
                var again = ed.GetString(new PromptStringOptions("\nWeiterer Träger (Name/Handle, Enter = Liste, Esc = Ende): ") { AllowSpaces = false });
                if (again.Status != PromptStatus.OK && again.Status != PromptStatus.None)
                    return;
                text = (again.StringResult ?? "").Trim();
            }
        }

        // ---------------- helpers ----------------

        private static List<BeamRow> ToRows(IEnumerable<BeamReport> reports) => reports.Select(r => new BeamRow
        {
            Name = r.Name,
            Handle = string.Join("+", r.PartIds),
            Article = r.Article ?? "",
            Description = r.Description ?? "",
            Length = r.Length,
            Supports = r.Supports.Count,
            Loads = r.Loads.Count,
            MaxLoad = r.MaxLoadFk,
            Notes = string.Join(" | ", r.Notes),
        }).ToList();

        private static string IndexPath(Database db)
        {
            string file = db.Filename;
            if (string.IsNullOrEmpty(file) || !Path.IsPathRooted(file))
                return null;
            return Path.ChangeExtension(file, null) + ".beamcheck.json";
        }

        private static void WriteIndex(Database db, List<BeamRow> rows, Editor ed)
        {
            string path = IndexPath(db);
            if (path == null)
            {
                ed.WriteMessage("\nZeichnung ist nicht gespeichert: die Trägerliste für BEAMFIND wird nicht abgelegt.");
                return;
            }

            try
            {
                var index = new BeamIndex { Drawing = Path.GetFileName(db.Filename), Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Beams = rows };
                using (var fs = File.Create(path))
                    new DataContractJsonSerializer(typeof(BeamIndex)).WriteObject(fs, index);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nTrägerliste konnte nicht gespeichert werden: " + ex.Message);
            }
        }

        private static List<BeamRow> ReadIndex(Database db)
        {
            string path = IndexPath(db);
            if (path == null || !File.Exists(path))
                return null;
            try
            {
                using (var fs = File.OpenRead(path))
                    return ((BeamIndex)new DataContractJsonSerializer(typeof(BeamIndex)).ReadObject(fs)).Beams;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static string SaveWorkbook(Database db, BeamCheckSettings settings, List<BeamReport> reports, Editor ed)
        {
            string suggested = string.IsNullOrEmpty(db.Filename) ? "Traeger.xlsx" : Path.GetFileNameWithoutExtension(db.Filename) + "_Traeger.xlsx";
            string dir = string.IsNullOrEmpty(db.Filename) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(db.Filename);
            string path;
            using (var dlg = new SaveFileDialog
            {
                Title = "BeamCheck — Excel-Datei speichern",
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = suggested,
                InitialDirectory = dir,
                OverwritePrompt = true,
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK)
                    return null;
                path = dlg.FileName;
            }

            try
            {
                var wb = BatchExcel.Build(reports, r => BeamPng.Render(BeamIllustration.Build(r, settings.ShowDesignValues, settings.DecimalSeparator)));
                wb.Save(path);
                return path;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nExcel-Datei konnte nicht gespeichert werden: " + ex.Message);
                Plugin.Log("BEAMBATCH excel failed: " + ex);
                return null;
            }
        }

        /// <summary>Saved viewpoints for Navisworks, next to the workbook (or the drawing): one per beam, named like it.</summary>
        private static void WriteNavisworks(Database db, List<BeamReport> reports, string excelPath, Editor ed)
        {
            string dir = !string.IsNullOrEmpty(excelPath) ? Path.GetDirectoryName(excelPath)
                : string.IsNullOrEmpty(db.Filename) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(db.Filename);
            string name = (string.IsNullOrEmpty(db.Filename) ? "Zeichnung" : Path.GetFileNameWithoutExtension(db.Filename)) + "_Traeger_Ansichtspunkte.xml";
            try
            {
                string path = Path.Combine(dir, name);
                File.WriteAllText(path, NavisworksExport.ViewpointsXml(reports), new System.Text.UTF8Encoding(false));
                ed.WriteMessage("\nNavisworks-Ansichtspunkte: " + path + "\n  Navisworks: Gespeicherte Ansichtspunkte → Rechtsklick → Ansichtspunkte importieren; dann den Träger (B-001 …) doppelklicken.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nNavisworks-Datei konnte nicht gespeichert werden: " + ex.Message);
            }
        }

        private static void TryOpen(string path, Editor ed)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nDatei konnte nicht geöffnet werden: " + ex.Message);
            }
        }

        /// <summary>Writes the beam names into the model, above the middle of each beam.</summary>
        private static void InsertNames(Database db, BeamCheckSettings settings, List<BeamReport> reports, List<ScaffoldElement> elements, double toMm)
        {
            var byId = elements.Where(e => e.Kind == ElementKind.Beam).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
            double k = 1.0 / toMm;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                var layer = ResultDrawer.EnsureLayer(tr, db, NamesLayer);
                foreach (var r in reports)
                {
                    if (!byId.TryGetValue(r.Id, out var beam))
                        continue;
                    var mid = (beam.Start + beam.End) / 2;
                    var mt = new MText
                    {
                        Contents = r.Name,
                        TextHeight = settings.TextHeight * settings.Scale * k,
                        Location = new Point3d(mid.X * k, mid.Y * k, beam.ZMax * k + settings.TextHeight * settings.Scale * k),
                        Attachment = AttachmentPoint.BottomCenter,
                    };
                    mt.SetDatabaseDefaults(db);
                    mt.LayerId = layer;
                    mt.Color = Color.FromColorIndex(ColorMethod.ByAci, 2);
                    space.AppendEntity(mt);
                    tr.AddNewlyCreatedDBObject(mt, true);
                }

                tr.Commit();
            }
        }

        /// <summary>Draws the pictures of the given beams below each other from a picked point.</summary>
        private static void InsertPictures(Document doc, BeamCheckSettings settings, List<BeamReport> which, double toMm, List<BeamReport> all)
        {
            var ed = doc.Editor;
            var pr = ed.GetPoint("\nEinfügepunkt der Abbildung(en) (linke obere Ecke): ");
            if (pr.Status != PromptStatus.OK)
                return;
            var origin = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                double y = origin.Y;
                foreach (var r in which)
                {
                    var ill = BeamIllustration.Build(r, settings.ShowDesignValues, settings.DecimalSeparator);
                    double h = ill.Height / toMm;
                    IllustrationDrawer.Draw(tr, doc.Database, space, ill, new Point3d(origin.X, y - h, origin.Z), toMm);
                    y -= h + 2 * ill.TextHeight / toMm;
                }

                tr.Commit();
            }

            ed.WriteMessage("\n" + which.Count + " Abbildung(en) eingefügt (Layer " + IllustrationDrawer.LayerName + ").");
        }
    }
}
