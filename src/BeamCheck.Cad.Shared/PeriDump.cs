using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BeamCheck.Core.Recognition;
using BeamCheck.Core.Settings;
#if BRICSCAD
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace BeamCheck.Cad
{
    /// <summary>
    /// Diagnostic report of how PERI CAD stores its components: entity type, block, attributes,
    /// dynamic properties, XData, extension dictionaries and all public .NET properties.
    /// Used to tune the recognition rules in the settings file.
    /// </summary>
    internal static class PeriDump
    {
        public static string Write(Document doc, ObjectId[] ids, BeamCheckSettings settings)
        {
            var db = doc.Database;
            var classifier = new ElementClassifier(settings);
            double toMm = CadUnits.ToMm(db, settings);
            var sb = new StringBuilder();
            sb.AppendLine("BeamCheck PERIDUMP " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("Drawing: " + db.Filename);
            sb.AppendLine("Host: " + System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
            sb.AppendLine("Runtime: " + Environment.Version + "  INSUNITS: " + db.Insunits + "  unit→mm: " + toMm.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var reader = new CadElementReader(tr, classifier, toMm);
                int n = 0;
                foreach (var id in ids)
                {
                    var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    sb.AppendLine(new string('=', 80));
                    sb.AppendLine("#" + (++n) + "  " + ent.GetType().FullName + "  handle " + ent.Handle);

                    var sig = CadElementReader.ReadSignature(tr, ent);
                    sb.AppendLine("DXF name : " + sig.DxfName);
                    sb.AppendLine("Block    : " + sig.BlockName);
                    sb.AppendLine("Layer    : " + sig.Layer);
                    try
                    {
                        var ext = ent.GeometricExtents;
                        sb.AppendLine("Extents  : " + ext.MinPoint + " – " + ext.MaxPoint);
                    }
                    catch (System.Exception ex)
                    {
                        sb.AppendLine("Extents  : n/a (" + ex.Message + ")");
                    }

                    if (ent is BlockReference br)
                    {
                        sb.AppendLine("Position : " + br.Position + "  rotation " + br.Rotation.ToString("0.####", CultureInfo.InvariantCulture) + "  scale " + br.ScaleFactors);
                        sb.AppendLine("Normal   : " + br.Normal);
                        DumpBlockContent(tr, sb, br, 1);
                    }

                    sb.AppendLine("-- Signature properties (attributes / dynamic / XData / Xrecords):");
                    foreach (var p in sig.Properties)
                        sb.AppendLine("   " + p.Key + " = " + p.Value);

                    var cls = classifier.Classify(sig);
                    sb.AppendLine("-- Classification: kind=" + cls.Kind + "  article=" + cls.Article + "  weight=" +
                                  (cls.WeightKg.HasValue ? cls.WeightKg.Value.ToString(CultureInfo.InvariantCulture) + " kg" : "?") + "  descr=" + cls.Description);
                    foreach (var el in reader.Read(ent))
                    {
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "   element {0}: {1} start {2} end {3} width {4:0.#} Z {5:0.#}..{6:0.#} mm, {7:0.##} kg{8}",
                            el.Id, el.Kind, el.Start, el.End, el.Width, el.ZMin, el.ZMax, el.WeightKg, el.WeightIsEstimated ? " (est.)" : ""));
                    }

                    sb.AppendLine("-- .NET properties:");
                    DumpReflection(sb, ent);
                }

                tr.Commit();
            }

            string dir = string.IsNullOrEmpty(db.Filename) || !File.Exists(db.Filename)
                ? Path.GetTempPath()
                : Path.GetDirectoryName(db.Filename);
            string path = Path.Combine(dir, "PERIDUMP_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt");
            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch (System.Exception)
            {
                path = Path.Combine(Path.GetTempPath(), Path.GetFileName(path));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }

            return path;
        }

        private static void DumpBlockContent(Transaction tr, StringBuilder sb, BlockReference br, int depth)
        {
            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            var counts = new SortedDictionary<string, int>();
            var nested = new List<BlockReference>();
            foreach (ObjectId id in btr)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead);
                string key = obj.GetType().Name;
                if (obj is BlockReference child)
                {
                    key += " '" + CadElementReader.EffectiveName(tr, child) + "'";
                    nested.Add(child);
                }

                counts.TryGetValue(key, out int c);
                counts[key] = c + 1;
            }

            string indent = new string(' ', depth * 3);
            sb.AppendLine(indent + "Block content of '" + btr.Name + "': " + string.Join(", ", counts.Select(kv => kv.Value + "× " + kv.Key)));
            if (depth < 3)
                foreach (var child in nested.Take(20))
                    DumpBlockContent(tr, sb, child, depth + 1);
        }

        private static void DumpReflection(StringBuilder sb, object obj)
        {
            foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                    continue;
                string value;
                try
                {
                    var v = prop.GetValue(obj, null);
                    value = v == null ? "null" : v is string s ? s : v is IEnumerable e && !(v is string) ? "[" + v.GetType().Name + "]" : Convert.ToString(v, CultureInfo.InvariantCulture);
                }
                catch (System.Exception ex)
                {
                    value = "<" + (ex.InnerException ?? ex).GetType().Name + ">";
                }

                if (value != null && value.Length > 200)
                    value = value.Substring(0, 200) + "…";
                sb.AppendLine("   " + prop.Name + " = " + value);
            }
        }
    }
}
