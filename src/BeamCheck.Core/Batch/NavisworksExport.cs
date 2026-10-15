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

        /// <summary>
        /// Saved viewpoints (Navisworks: Gespeicherte Ansichtspunkte → import): one per beam, named like it, the camera looking at the
        /// beam from the side and a little from above. Positions are in the file's units (m), taken from the drawing in mm.
        /// Camera convention checked against viewpoints exported from Navisworks: forward = rotation · (0, 0, −1), up = rotation · (0, 1, 0).
        /// </summary>
        public static string ViewpointsXml(IEnumerable<BeamReport> reports)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("0.0000000000", inv);
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>");
            sb.AppendLine("<exchange xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"http://download.autodesk.com/us/navisworks/schemas/nw-exchange-12.0.xsd\" units=\"m\" filename=\"\" filepath=\"\">");
            sb.AppendLine("  <viewpoints>");
            foreach (var r in reports)
            {
                double dx = r.EndX - r.StartX, dy = r.EndY - r.StartY, len = System.Math.Sqrt(dx * dx + dy * dy);
                if (len < 1)
                    continue;
                // Target: middle of the beam. Camera: beside it (perpendicular to its axis), 20° above, far enough to see the whole beam.
                double tx = r.CenterX, ty = r.CenterY, tz = r.CenterZ;
                double nx = -dy / len, ny = dx / len;
                double dist = System.Math.Max(2500, System.Math.Min(12000, len * 0.9 + 1500));
                double el = 20 * System.Math.PI / 180;
                double px = tx + nx * dist * System.Math.Cos(el), py = ty + ny * dist * System.Math.Cos(el), pz = tz + dist * System.Math.Sin(el);
                double[] q = LookAtQuaternion(new[] { tx - px, ty - py, tz - pz });
                double focal = dist / 1000.0;
                string title = r.Name + (string.IsNullOrEmpty(r.Description) ? "" : " " + r.Description);
                sb.AppendLine("    <view name=\"" + Esc(title) + "\" guid=\"" + System.Guid.NewGuid().ToString() + "\">");
                sb.AppendLine("      <viewpoint tool=\"none\" render=\"shaded\" lighting=\"user\" focal=\"" + N(focal) + "\" linear=\"14.5042122249\" angular=\"0.7853981634\" fov=\"0.7853980000\">");
                sb.AppendLine("        <camera projection=\"persp\" near=\"" + N(focal * 0.0249) + "\" far=\"" + N(focal * 24.9) + "\" aspect=\"1.7000000000\" height=\"0.7853980000\">");
                sb.AppendLine("          <position>");
                sb.AppendLine("            <pos3f x=\"" + N(px / 1000) + "\" y=\"" + N(py / 1000) + "\" z=\"" + N(pz / 1000) + "\"/>");
                sb.AppendLine("          </position>");
                sb.AppendLine("          <rotation>");
                sb.AppendLine("            <quaternion a=\"" + N(q[0]) + "\" b=\"" + N(q[1]) + "\" c=\"" + N(q[2]) + "\" d=\"" + N(q[3]) + "\"/>");
                sb.AppendLine("          </rotation>");
                sb.AppendLine("        </camera>");
                sb.AppendLine("        <viewer radius=\"0.3000000000\" height=\"1.8000000000\" actual_height=\"1.8000000000\" eye_height=\"0.1500000000\" avatar=\"construction_worker\" camera_mode=\"first\" first_to_third_angle=\"0.0000000000\" first_to_third_distance=\"3.0000000000\" first_to_third_param=\"1.0000000000\" first_to_third_correction=\"1\" collision_detection=\"0\" auto_crouch=\"0\" gravity=\"0\" gravity_value=\"9.8000000000\" terminal_velocity=\"50.0000000000\"/>");
                sb.AppendLine("        <up>");
                sb.AppendLine("          <vec3f x=\"0.0000000000\" y=\"0.0000000000\" z=\"1.0000000000\"/>");
                sb.AppendLine("        </up>");
                sb.AppendLine("      </viewpoint>");
                sb.AppendLine("    </view>");
            }

            sb.AppendLine("  </viewpoints>");
            sb.AppendLine("</exchange>");
            return sb.ToString();
        }

        /// <summary>Rotation (x, y, z, w) of a camera that looks along <paramref name="forward"/> with world Z up; forward = R·(0,0,−1), up = R·(0,1,0).</summary>
        public static double[] LookAtQuaternion(double[] forward)
        {
            double fl = System.Math.Sqrt(forward[0] * forward[0] + forward[1] * forward[1] + forward[2] * forward[2]);
            double fx = forward[0] / fl, fy = forward[1] / fl, fz = forward[2] / fl;
            // right = f × up(0,0,1)
            double rx = fy, ry = -fx, rz = 0;
            double rl = System.Math.Sqrt(rx * rx + ry * ry);
            if (rl < 1e-9)
            {
                rx = 1; ry = 0; rl = 1;
            }

            rx /= rl; ry /= rl;
            // up = right × f
            double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
            // Matrix columns: X = right, Y = up, Z = −f.
            double m00 = rx, m10 = ry, m20 = rz;
            double m01 = ux, m11 = uy, m21 = uz;
            double m02 = -fx, m12 = -fy, m22 = -fz;
            double tr = m00 + m11 + m22, x, y, z, w;
            if (tr > 0)
            {
                double s = System.Math.Sqrt(tr + 1) * 2;
                w = 0.25 * s; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = System.Math.Sqrt(1 + m00 - m11 - m22) * 2;
                w = (m21 - m12) / s; x = 0.25 * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                double s = System.Math.Sqrt(1 + m11 - m00 - m22) * 2;
                w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25 * s; z = (m12 + m21) / s;
            }
            else
            {
                double s = System.Math.Sqrt(1 + m22 - m00 - m11) * 2;
                w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25 * s;
            }

            return new[] { x, y, z, w };
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
