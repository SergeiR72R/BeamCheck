#if BRICSCAD
using Teigha.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using BeamCheck.Core.Settings;

namespace BeamCheck.Cad
{
    internal static class CadUnits
    {
        /// <summary>Factor drawing unit → mm, from INSUNITS (fallback from settings).</summary>
        public static double ToMm(Database db, BeamCheckSettings settings)
        {
            switch (db.Insunits)
            {
                case UnitsValue.Millimeters: return 1;
                case UnitsValue.Centimeters: return 10;
                case UnitsValue.Decimeters: return 100;
                case UnitsValue.Meters: return 1000;
                case UnitsValue.Inches: return 25.4;
                case UnitsValue.Feet: return 304.8;
                default: return settings.FallbackUnitToMm > 0 ? settings.FallbackUnitToMm : 1;
            }
        }
    }
}
