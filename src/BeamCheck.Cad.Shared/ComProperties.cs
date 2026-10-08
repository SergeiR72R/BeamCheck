using System;
using System.ComponentModel;
using System.Globalization;
using BeamCheck.Core.Recognition;
#if BRICSCAD
using Teigha.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace BeamCheck.Cad
{
    /// <summary>
    /// Reads the COM/ActiveX properties of custom objects — the same ones the Properties palette shows.
    /// PERI CAD parts are custom (AEC-based) entities that keep article data in their own binary
    /// format; their COM wrapper is the documented way to get at it without PERI's SDK.
    /// Only used for objects that have no managed wrapper of their own.
    /// </summary>
    internal static class ComProperties
    {
        public static bool IsCustomObject(Entity ent)
        {
            var t = ent.GetType();
            return t == typeof(Entity) || t.Name.IndexOf("Imp", StringComparison.Ordinal) >= 0 || ent is ProxyEntity;
        }

        public static void AddTo(ElementSignature sig, Entity ent)
        {
            if (!IsCustomObject(ent))
                return;
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
