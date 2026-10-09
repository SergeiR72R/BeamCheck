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
using Teigha.Geometry;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
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
                var reader = new CadElementReader(tr, db, settings, classifier, toMm);
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

                    var segs = new List<BeamCheck.Core.Recognition.Segment>();
                    string periBlock = null;
                    reader.CollectSegments(ent, Matrix3d.Identity, segs, ref periBlock, 0);
                    sb.AppendLine("-- Axis segments (mm): " + segs.Count + "  PERI display block: " + (periBlock ?? "—") +
                                  "  shape guess: " + new BeamCheck.Core.Recognition.ShapeAnalyzer(settings).Guess(segs));
                    foreach (var s in segs.Take(12))
                        sb.AppendLine("   " + s.A + " → " + s.B + string.Format(CultureInfo.InvariantCulture, "  L={0:0}", s.Length));
                    DumpExplode(tr, sb, ent);

                    sb.AppendLine("-- Part properties read by the module:");
                    var partSig = new BeamCheck.Core.Recognition.ElementSignature();
                    ComProperties.AddPartProperties(partSig, ent);
                    foreach (var p in partSig.Properties)
                        sb.AppendLine("   " + p.Key + " = " + p.Value);

                    sb.AppendLine("-- Members declared by the vendor classes (what PERI's own assembly adds):");
                    DumpDeclaredMembers(sb, ent);

                    sb.AppendLine("-- COM (ActiveX) properties:");
                    var comSig = new BeamCheck.Core.Recognition.ElementSignature();
                    ComProperties.AddComProperties(comSig, ent);
                    foreach (var p in comSig.Properties)
                        sb.AppendLine("   " + p.Key + " = " + p.Value);

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

        /// <summary>What the object turns into when exploded (how PERI parts expose their graphics).</summary>
        private static void DumpExplode(Transaction tr, StringBuilder sb, Entity ent)
        {
            if (ent is BlockReference)
                return;
            var parts = new DBObjectCollection();
            try
            {
                ent.Explode(parts);
            }
            catch (System.Exception ex)
            {
                sb.AppendLine("-- Explode: not supported (" + ex.Message + ")");
                return;
            }

            sb.AppendLine("-- Explode: " + parts.Count + " object(s)");
            foreach (DBObject o in parts)
            {
                string line = "   " + o.GetType().Name;
                if (o is BlockReference br)
                    line += " '" + CadElementReader.EffectiveName(tr, br) + "' pos " + br.Position + " scale " + br.ScaleFactors + " rot " + br.Rotation.ToString("0.####", CultureInfo.InvariantCulture);
                sb.AppendLine(line);
                o.Dispose();
            }
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

        /// <summary>
        /// Properties and methods declared by the part's own classes (from the type itself up to,
        /// but excluding, the CAD base classes), plus the value of every parameterless
        /// weight/mass method or property — reveals whether PERI exposes weights.
        /// </summary>
        private static void DumpDeclaredMembers(StringBuilder sb, object obj)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = obj.GetType(); t != null && t != typeof(Entity) && t != typeof(object); t = t.BaseType)
            {
                string ns = t.Namespace ?? "";
                if (ns.StartsWith("Autodesk.", StringComparison.Ordinal) || ns.StartsWith("Teigha.", StringComparison.Ordinal) || ns.StartsWith("Bricscad.", StringComparison.Ordinal))
                    break;
                sb.AppendLine("   class " + t.FullName + "  (" + t.Assembly.GetName().Name + " " + t.Assembly.GetName().Version + ")");
                foreach (var m in t.GetMembers(flags).OrderBy(x => x.MemberType).ThenBy(x => x.Name))
                {
                    if (m is MethodBase mb && (mb.IsSpecialName || mb.IsConstructor))
                        continue;
                    string line = "      " + m.MemberType + " " + m.Name;
                    if (m is MethodInfo mi)
                        line += "(" + string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ") : " + mi.ReturnType.Name;
                    else if (m is PropertyInfo pi)
                        line += " : " + pi.PropertyType.Name;
                    else if (m is FieldInfo fi)
                        line += " : " + fi.FieldType.Name;

                    try
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(m.Name, "(?i)weight|gewicht|mass|kg|price|preis|category|kateg"))
                        {
                            if (m is PropertyInfo p2 && p2.GetIndexParameters().Length == 0)
                                line += "  = " + ComProperties.Format(p2.GetValue(obj, null));
                            else if (m is MethodInfo m2 && m2.GetParameters().Length == 0 && m2.ReturnType != typeof(void))
                                line += "  = " + ComProperties.Format(m2.Invoke(obj, null));
                        }
                    }
                    catch (System.Exception ex)
                    {
                        line += "  <" + (ex.InnerException ?? ex).GetType().Name + ">";
                    }

                    sb.AppendLine(line);
                }
            }
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
                    value = v == null ? "null" : v is string s ? s : v is IEnumerable ? ComProperties.Format(v) : Convert.ToString(v, CultureInfo.InvariantCulture);
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
