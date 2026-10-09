using System;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BeamCheck.Core.Recognition;
#if BRICSCAD
using Teigha.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace BeamCheck.Cad
{
    /// <summary>
    /// Reads what a PERI part says about itself.
    ///
    /// PERI CAD parts are instances of managed classes from PERI's own assembly
    /// (<c>PERI.DatabaseServices.Part</c>). They expose ArtNr, Name, Width/Depth/Height (m) and
    /// Categories as ordinary .NET properties — read here by reflection, so no reference to PERI's
    /// assembly is needed and the same code works for PERI CAD 24 (AutoCAD) and 25/26 (BricsCAD).
    /// Unknown custom objects fall back to their COM (ActiveX) properties.
    /// </summary>
    internal static class ComProperties
    {
        /// <summary>Properties of PERI part classes copied into the signature (key "PART:&lt;name&gt;").</summary>
        private static readonly string[] PartProperties = { "ArtNr", "Name", "Width", "Depth", "Height", "Categories", "Mark" };

        public static bool IsVendorObject(Entity ent)
        {
            string ns = ent.GetType().Namespace ?? "";
            return !(ns.StartsWith("Autodesk.", StringComparison.Ordinal) || ns.StartsWith("Teigha.", StringComparison.Ordinal) ||
                     ns.StartsWith("Bricscad.", StringComparison.Ordinal) || ns.StartsWith("System", StringComparison.Ordinal));
        }

        public static bool IsCustomObject(Entity ent)
        {
            var t = ent.GetType();
            return t == typeof(Entity) || t.Name.IndexOf("Imp", StringComparison.Ordinal) >= 0 || ent is ProxyEntity;
        }

        public static void AddTo(ElementSignature sig, Entity ent)
        {
            if (IsVendorObject(ent))
                AddPartProperties(sig, ent);
            if (IsCustomObject(ent))
                AddComProperties(sig, ent);
        }

        public static void AddPartProperties(ElementSignature sig, object obj)
        {
            foreach (var name in PartProperties)
            {
                try
                {
                    var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null || p.GetIndexParameters().Length > 0)
                        continue;
                    sig.Add("PART:" + name, Format(p.GetValue(obj, null)));
                }
                catch (Exception)
                {
                    // A property that throws is simply not available for this part.
                }
            }
        }

        public static string Format(object v)
        {
            if (v == null)
                return null;
            if (v is string s)
                return s;
            if (v is IEnumerable e)
                return string.Join(", ", e.Cast<object>().Take(30).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static void AddComProperties(ElementSignature sig, Entity ent)
        {
            object com;
            try
            {
                // Late-bound so the code compiles against both AutoCAD and BricsCAD.
                com = ent.GetType().GetProperty("AcadObject")?.GetValue(ent, null);
            }
            catch (Exception)
            {
                return;
            }

            if (com == null)
                return;

            try
            {
                foreach (PropertyDescriptor pd in TypeDescriptor.GetProperties(com))
                {
                    string value;
                    try
                    {
                        var v = pd.GetValue(com);
                        if (v == null || v.GetType().IsCOMObject || v is Array)
                            continue;
                        value = Convert.ToString(v, CultureInfo.InvariantCulture);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    sig.Add("COM:" + pd.Name, value);
                }
            }
            catch (Exception)
            {
                // COM type information not available on this host/runtime.
            }
        }
    }
}
