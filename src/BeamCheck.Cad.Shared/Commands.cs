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
    public sealed class Commands
    {
        /// <summary>
        /// Main command: pick a beam, pick standards, review loads in the dialog,
        /// then insert the 2D load scheme and the table into the drawing.
        /// </summary>
        [CommandMethod("BEAMLOAD", CommandFlags.Modal)]
        public void BeamLoad()
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
                ed.WriteMessage("\nОшибка чтения настроек BeamCheck: " + ex.Message);
                return;
            }

            var classifier = new ElementClassifier(settings);
            double toMm = CadUnits.ToMm(db, settings);

            var peo = new PromptEntityOptions("\nВыберите балку: ");
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return;

            var pso = new PromptSelectionOptions { MessageForAdding = "\nВыберите стойки над балкой (достаточно любой секции каждой стойки): " };
            var psr = ed.GetSelection(pso);
            if (psr.Status != PromptStatus.OK)
                return;

            var upperPrompt = new PromptSelectionOptions
            {
                MessageForAdding = "\nВыберите вышестоящие балки, которые опираются на эти стойки и несут другие стойки (Enter — не выбирать, найти автоматически): ",
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
                    ed.WriteMessage("\nНе удалось определить геометрию балки.");
                    return;
                }

                ed.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                    "\nБалка: {0}  ось {1} → {2}  L={3:0} мм  ширина {4:0}  верх Z={5:0}",
                    beam.DisplayName, beam.Start, beam.End, beam.PlanLength, beam.Width, beam.ZMax));

                var selected = new List<ScaffoldElement>();
                int shown = 0;
                foreach (SelectedObject so in psr.Value)
                {
                    var ent = (Entity)tr.GetObject(so.ObjectId, OpenMode.ForRead);
                    var found = reader.ReadPickedStandards(ent, out string info);
                    if (shown++ < 15)
                        ed.WriteMessage("\n  выбрано " + ent.Handle + " " + info + " → стоек: " + found.Count +
                                        string.Concat(found.Take(3).Select(f => string.Format(CultureInfo.InvariantCulture, "  [X={0:0} Y={1:0} Z {2:0}..{3:0}]", f.Start.X, f.Start.Y, f.ZMin, f.ZMax))));
                    selected.AddRange(found);
                }

                if (selected.Count == 0)
                {
                    ed.WriteMessage("\nСтойки не выбраны.");
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
                ed.WriteMessage($"\nНайдено элементов вокруг стоек: {candidates.Count} " +
                                $"(стоек {candidates.Count(c => c.Kind == ElementKind.Standard)}, " +
                                $"леджеров {candidates.Count(c => c.Kind == ElementKind.Ledger)}, " +
                                $"деков {candidates.Count(c => c.Kind == ElementKind.Deck)}).");

                session = new BeamLoadSession(settings, new AnalysisInput
                {
                    Beam = beam,
                    SelectedStandards = selected,
                    Candidates = candidates,
                });
                foreach (var kv in reader.TopLevelIds)
                    idMap[kv.Key] = kv.Value;
                tr.Commit();
            }

            RunDialogLoop(doc, session, classifier, toMm, idMap);
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
                        var ids = session.Topology.UsedElementIds.Where(idMap.ContainsKey).Select(i => idMap[i]).Distinct().ToList();
                        Highlight(db, ids, true);
                        ed.GetString(new PromptStringOptions($"\nПодсвечено учтённых объектов: {ids.Count}. Enter — вернуться в диалог: ") { AllowSpaces = true });
                        Highlight(db, ids, false);
                        break;

                    case FormAction.Exclude:
                    {
                        var res = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nВыберите объекты, которые НЕ учитывать: " });
                        if (res.Status != PromptStatus.OK)
                            break;
                        var picked = new HashSet<ObjectId>(res.Value.GetObjectIds());
                        var excluded = idMap.Where(kv => picked.Contains(kv.Value)).Select(kv => kv.Key).ToList();
                        session.Exclude(excluded);
                        ed.WriteMessage($"\nИсключено элементов: {excluded.Count}.");
                        break;
                    }

                    case FormAction.Add:
                        AddObjects(doc, session, classifier, toMm, idMap);
                        break;

                    case FormAction.Insert:
                        var pr = ed.GetPoint("\nТочка вставки схемы и таблицы: ");
                        if (pr.Status != PromptStatus.OK)
                            break;
                        var wcs = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                        ResultDrawer.Draw(db, session.Result, session.Settings, wcs, toMm);
                        ed.WriteMessage("\nСхема нагрузок и таблица вставлены.");
                        return;

                    default:
                        return;
                }
            }
        }

        private static void AddObjects(Document doc, BeamLoadSession session, ElementClassifier classifier, double toMm, Dictionary<string, ObjectId> idMap)
        {
            var ed = doc.Editor;
            var kwo = new PromptKeywordOptions("\nКак учитывать добавляемые объекты [Дек/Леджер/Стойка/Авто]: ", "Deck Ledger Standard Auto");
            var kw = ed.GetKeywords(kwo);
            if (kw.Status != PromptStatus.OK)
                return;

            ElementKind? kind = null;
            switch (kw.StringResult)
            {
                case "Deck": kind = ElementKind.Deck; break;
                case "Ledger": kind = ElementKind.Ledger; break;
                case "Standard": kind = ElementKind.Standard; break;
            }

            var res = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nВыберите объекты для добавления в расчёт: " });
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
            ed.WriteMessage($"\nДобавлено элементов: {added.Count}.");
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
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent) || IsAnnotation(ent))
                    continue;
                Extents3d ext;
                try
                {
                    ext = ent.GeometricExtents;
                }
                catch (System.Exception)
                {
                    continue;
                }

                if (ext.MaxPoint.X * toMm < x0 || ext.MinPoint.X * toMm > x1 ||
                    ext.MaxPoint.Y * toMm < y0 || ext.MinPoint.Y * toMm > y1 ||
                    ext.MaxPoint.Z * toMm < zMin)
                    continue;

                foreach (var e in reader.Read(ent))
                    if (e.Id != beam.Id)
                        result.Add(e);
            }

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
            var res = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nВыберите объекты PERI для анализа (балка, стойка, леджер, дек): " });
            if (res.Status != PromptStatus.OK)
                return;

            var settings = Plugin.LoadSettings(out _);
            string path = PeriDump.Write(doc, res.Value.GetObjectIds(), settings);
            ed.WriteMessage($"\nОтчёт сохранён: {path}\nПришлите этот файл разработчику модуля для настройки распознавания.");
            TryOpen(path);
        }

        [CommandMethod("BEAMLOADSETTINGS", CommandFlags.Modal)]
        public void OpenSettings()
        {
            var ed = CadApp.DocumentManager.MdiActiveDocument?.Editor;
            Plugin.LoadSettings(out string path);
            ed?.WriteMessage($"\nФайл настроек BeamCheck: {path}\nИзменения применяются при следующем запуске BEAMLOAD.");
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
