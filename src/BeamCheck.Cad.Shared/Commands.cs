using System;
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

            var idMap = new Dictionary<string, ObjectId>();
            BeamLoadSession session;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var reader = new CadElementReader(tr, classifier, toMm);
                var beamEnt = (Entity)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                var beam = reader.Read(beamEnt, ElementKind.Beam).FirstOrDefault();
                if (beam == null)
                {
                    ed.WriteMessage("\nНе удалось определить геометрию балки.");
                    return;
                }

                var selected = new List<ScaffoldElement>();
                foreach (SelectedObject so in psr.Value)
                {
                    var ent = (Entity)tr.GetObject(so.ObjectId, OpenMode.ForRead);
                    var found = reader.Read(ent).Where(x => x.Kind == ElementKind.Standard).ToList();
                    // The user explicitly picked it: take an unrecognised object as a standard.
                    if (found.Count == 0)
                        found = reader.Read(ent, ElementKind.Standard);
                    selected.AddRange(found);
                }

                if (selected.Count == 0)
                {
                    ed.WriteMessage("\nСтойки не выбраны.");
                    return;
                }

                var candidates = ScanCandidates(tr, db, reader, beam, selected, settings, toMm);
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
                var reader = new CadElementReader(tr, classifier, toMm);
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
            List<ScaffoldElement> selected, BeamCheckSettings settings, double toMm)
        {
            double r = settings.SearchRadius;
            double x0 = selected.Min(s => s.Start.X) - r, x1 = selected.Max(s => s.Start.X) + r;
            double y0 = selected.Min(s => s.Start.Y) - r, y1 = selected.Max(s => s.Start.Y) + r;
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
                    if (e.Kind != ElementKind.Beam)
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
