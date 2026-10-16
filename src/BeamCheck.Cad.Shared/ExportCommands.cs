using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeamCheck.Core.Analysis;
#if BRICSCAD
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Geometry;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace BeamCheck.Cad
{
    public sealed partial class Commands
    {
        /// <summary>
        /// Copies every part that entered the calculation (checked member, picked standards, ledgers, decks, upper beams,
        /// consoles — minus what the user excluded) into a new DWG next to the drawing. Coordinates stay as they are.
        /// The copy takes the whole top-level object: a part inside an assembly block comes with the block.
        /// </summary>
        private static void ExportUsed(Document doc, BeamLoadSession session, Dictionary<string, ObjectId> idMap)
        {
            var ed = doc.Editor;
            var db = doc.Database;

            var keys = new HashSet<string>(session.Topology.UsedElementIds);
            keys.UnionWith(session.ManualIds);
            keys.Add(session.Input.Beam.Id);
            foreach (var st in session.Input.SelectedStandards)
                keys.Add(st.Id);
            keys.ExceptWith(session.Input.ExcludedIds);

            var ids = new ObjectIdCollection();
            var seen = new HashSet<ObjectId>();
            foreach (var key in keys)
            {
                if (idMap.TryGetValue(key, out var id) && !id.IsNull && !id.IsErased && seen.Add(id))
                    ids.Add(id);
            }

            if (ids.Count == 0)
            {
                ed.WriteMessage("\nKeine berücksichtigten Objekte zum Kopieren.");
                return;
            }

            string path = TargetPath(db, session);
            try
            {
                using (var dest = new Database(true, true))
                {
                    dest.Insunits = db.Insunits;
                    db.Wblock(dest, ids, Point3d.Origin, DuplicateRecordCloning.Ignore);
                    dest.SaveAs(path, DwgVersion.Current);
                }

                ed.WriteMessage("\n" + ids.Count + " berücksichtigte Objekte in neue DWG kopiert: " + path);
                Plugin.Log("BEAMEXPORT: " + ids.Count + " objects → " + path);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nDie DWG konnte nicht erstellt werden: " + ex.Message);
                Plugin.Log("BEAMEXPORT failed: " + ex);
            }
        }

        /// <summary>
        /// Writes the calculation path (Gk and Qk by kind of part, by level and by transferring beam) as a CSV next to the drawing:
        /// semicolon separated, UTF-8 with BOM, opens in Excel.
        /// </summary>
        private static void WriteBreakdown(Document doc, BeamLoadSession session)
        {
            var ed = doc.Editor;
            try
            {
                string path = Path.ChangeExtension(TargetPath(doc.Database, session).Replace("_Lastermittlung_", "_Rechenweg_"), ".csv");
                var lines = BeamCheck.Core.Report.CalculationBreakdown.Build(session);
                File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(true));
                foreach (var l in lines)
                    Plugin.Log("RECHENWEG " + l);
                ed.WriteMessage("\nRechenweg gespeichert: " + path);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nDer Rechenweg konnte nicht gespeichert werden: " + ex.Message);
            }
        }

        /// <summary>Next to the drawing (documents folder when it has not been saved): drawing name, beam, time stamp.</summary>
        private static string TargetPath(Database db, BeamLoadSession session)
        {
            string dir;
            string stem = "Zeichnung";
            if (!string.IsNullOrEmpty(db.Filename) && File.Exists(db.Filename))
            {
                dir = Path.GetDirectoryName(db.Filename);
                stem = Path.GetFileNameWithoutExtension(db.Filename);
            }
            else
            {
                dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            string beam = session.Input.Beam.DisplayName ?? session.Input.Beam.Id;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                beam = beam.Replace(c, '_');
                stem = stem.Replace(c, '_');
            }

            beam = beam.Replace(' ', '_');
            return Path.Combine(dir, stem + "_Lastermittlung_" + beam + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".dwg");
        }
    }
}
