using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using BeamCheck.Core.Batch;

namespace BeamCheck.Core.Batch
{
    /// <summary>
    /// Search sets for Navisworks (Sets → Import Search Sets…): one set per beam, named like the beam (B-001 …), selecting the
    /// beam by its CAD handle. In NWF/NWC files made from the model the set is picked in the Sets window and
    /// "Focus Items" (or the F key) frames the beam.
    /// The DWG handle is shown by Navisworks on the "Entity Handle" tab (property "Value"); if a Navisworks version names it
    /// differently, change <see cref="HandleCategory"/> / <see cref="HandleProperty"/> in the settings or replace the template.
    /// </summary>
    public static class NavisworksExport
    {
        public const string HandleCategory = "Entity Handle";
        public const string HandleProperty = "Value";

        public static string SearchSetsXml(IEnumerable<BeamReport> reports, string category = HandleCategory, string property = HandleProperty, string folder = "BeamCheck")
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>");
            sb.AppendLine("<exchange xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"http://download.autodesk.com/us/navisworks/schemas/nw-exchange-12.0.xsd\" units=\"mm\" filename=\"\" filepath=\"\">");
            sb.AppendLine(" <selectionsets>");
            sb.AppendLine("  <viewfolder name=\"" + Esc(folder) + "\">");
            foreach (var r in reports)
            {
                string handle = TopLevelHandle(r.Id);
                string title = r.Name + (string.IsNullOrEmpty(r.Description) ? "" : " " + r.Description);
                sb.AppendLine("   <selectionset name=\"" + Esc(title) + "\">");
                sb.AppendLine("    <findspec mode=\"all\" disjoint=\"0\">");
                sb.AppendLine("     <conditions>");
                sb.AppendLine("      <condition test=\"equals\" flags=\"10\">");
                sb.AppendLine("       <category><name>" + Esc(category) + "</name></category>");
                sb.AppendLine("       <property><name>" + Esc(property) + "</name></property>");
                sb.AppendLine("       <value><data type=\"wstring\">" + Esc(handle) + "</data></value>");
                sb.AppendLine("      </condition>");
                sb.AppendLine("     </conditions>");
                sb.AppendLine("     <locator>/</locator>");
                sb.AppendLine("    </findspec>");
                sb.AppendLine("   </selectionset>");
            }

            sb.AppendLine("  </viewfolder>");
            sb.AppendLine(" </selectionsets>");
            sb.AppendLine("</exchange>");
            return sb.ToString();
        }

        /// <summary>The handle of the top-level CAD object (element ids of nested parts look like "AB/12").</summary>
        public static string TopLevelHandle(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "";
            int i = id.IndexOf('/');
            return i < 0 ? id : id.Substring(0, i);
        }

        /// <summary>Plain list for the Excel sheet: name → handle to type into Navisworks "Find Items".</summary>
        public static IEnumerable<string[]> Lookup(IEnumerable<BeamReport> reports) =>
            reports.Select(r => new[] { r.Name, TopLevelHandle(r.Id) });

        private static string Esc(string s) => SecurityElement.Escape(s ?? "");
    }
}
