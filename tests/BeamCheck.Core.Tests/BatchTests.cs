using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BeamCheck.Core.Batch;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Report;
using BeamCheck.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace BeamCheck.Core.Tests
{
    /// <summary>Batch run (all beams, no picking) on parts rebuilt from the plugin log of a customer drawing.</summary>
    public class BatchTests
    {
        private readonly ITestOutputHelper _out;
        public BatchTests(ITestOutputHelper o) { _out = o; }

        [DataContract] private sealed class Rec
        {
            [DataMember(Name = "role")] public string Role { get; set; }
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "kind")] public string Kind { get; set; }
            [DataMember(Name = "art")] public string Art { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "x0")] public double X0 { get; set; }
            [DataMember(Name = "y0")] public double Y0 { get; set; }
            [DataMember(Name = "x1")] public double X1 { get; set; }
            [DataMember(Name = "y1")] public double Y1 { get; set; }
            [DataMember(Name = "z0")] public double Z0 { get; set; }
            [DataMember(Name = "z1")] public double Z1 { get; set; }
            [DataMember(Name = "w")] public double W { get; set; }
        }

        private static List<ScaffoldElement> Load(string fixture)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(BatchTests).Assembly.Location), "Fixtures", fixture);
            List<Rec> recs;
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
                recs = (List<Rec>)new DataContractJsonSerializer(typeof(List<Rec>)).ReadObject(gz);
            var classifier = new Recognition.ElementClassifier(new BeamCheckSettings());
            var list = new List<ScaffoldElement>();
            var seen = new HashSet<string>();
            foreach (var r in recs.Where(r => seen.Add(r.Id)))
            {
                var kind = (ElementKind)Enum.Parse(typeof(ElementKind), r.Kind);
                double zm = (r.Z0 + r.Z1) / 2;
                bool st = kind == ElementKind.Standard;
                var e = new ScaffoldElement
                {
                    Id = r.Id, Kind = kind, Article = r.Art, Description = r.Name,
                    Start = new Vec3(r.X0, r.Y0, st ? r.Z0 : zm), End = new Vec3(r.X1, r.Y1, st ? r.Z1 : zm),
                    Width = r.W, ZMin = r.Z0, ZMax = r.Z1,
                };
                e.WeightKg = classifier.DefaultWeightKg(kind, st ? e.ZMax - e.ZMin : e.Length, e.Width);
                list.Add(e);
            }

            return list;
        }

        [Fact]
        public void Every_beam_gets_a_unique_name_and_a_left_to_right_report()
        {
            var all = Load("log_beam_73CB0.json.gz");
            var settings = new BeamCheckSettings();
            var reports = BatchAnalyzer.Run(all, settings, new BatchOptions());

            _out.WriteLine("beams: " + reports.Count);
            foreach (var r in reports)
            {
                _out.WriteLine($"{r.Name} {r.Id} {r.Description} L={r.Length:0} Z {r.ZMin:0}..{r.ZMax:0} rev={r.Reversed} supports={r.Supports.Count} loads={r.Loads.Count} cont={r.ContinuousSupport} maxF={r.MaxLoadFk:0.00}");
                foreach (var e in r.Events)
                    _out.WriteLine($"    {e.Kind,-8} {e.Label,-3} pos={e.Position:0} d={e.Delta:0} Gk={e.Gk:0.00} Qk={e.Qk:0.00}");
            }

            Assert.True(reports.Count >= 10);
            Assert.Equal(reports.Count, reports.Select(r => r.Name).Distinct().Count());
            Assert.All(reports, r =>
            {
                var pos = r.Events.Select(e => e.Position).ToList();
                Assert.Equal(pos.OrderBy(x => x), pos);
                Assert.All(r.Events, e => Assert.InRange(e.Position, -s_tol, r.Length + s_tol));
                Assert.All(r.Events, e => Assert.True(e.Delta >= -1e-6));
                Assert.Equal(r.Loads.Count, r.Loads.Select(l => l.Label).Distinct().Count());
            });
        }

        private const double s_tol = 200;

        [Fact]
        public void Supports_are_pin_supports_without_values_and_stay_next_to_loads_above_them()
        {
            var reports = BatchAnalyzer.Run(Load("log_beam_73CB0.json.gz"), new BeamCheckSettings(), new BatchOptions());
            Assert.All(reports.SelectMany(r => r.Supports), s => Assert.Equal(0, s.Fk));
            // A load that goes straight down onto a support is still listed, with the support under it.
            Assert.Contains(reports, r => r.Supports.Any(s => r.Loads.Any(l => Math.Abs(l.Position - s.Position) < 1)));
            var r1 = reports.First(r => r.Supports.Any(s => r.Loads.Any(l => Math.Abs(l.Position - s.Position) < 1)));
            var ill = BeamIllustration.Build(r1, false, ",");
            Assert.DoesNotContain(ill.Texts, t => t.Text.StartsWith("R ="));
            Assert.Contains(ill.Texts, t => t.Text == r1.Supports[0].Label);
        }

        [Fact]
        public void Illustration_is_drawn_for_a_beam_with_loads_and_supports()
        {
            var all = Load("log_beam_73CB0.json.gz");
            var reports = BatchAnalyzer.Run(all, new BeamCheckSettings(), new BatchOptions());
            var r = reports.OrderByDescending(x => x.Loads.Count + x.Supports.Count).First();
            var ill = BeamIllustration.Build(r, false, ",");
            Assert.True(ill.Width > 0 && ill.Height > 0);
            Assert.Contains(ill.Texts, t => t.Text == "A");
            string svg = ill.ToSvg();
            Assert.StartsWith("<svg", svg);
            var dir = Environment.GetEnvironmentVariable("BEAMCHECK_OUT");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                foreach (var rep in reports)
                    File.WriteAllText(Path.Combine(dir, rep.Name + ".svg"), BeamIllustration.Build(rep, false, ",").ToSvg());
            }
        }

        [Fact]
        public void The_workbook_is_a_well_formed_xlsx_with_the_three_sheets()
        {
            var reports = BatchAnalyzer.Run(Load("log_beam_73CB0.json.gz"), new BeamCheckSettings(), new BatchOptions());
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
            var wb = BatchExcel.Build(reports, r => Tuple.Create(png, 300, 100));
            using (var ms = new MemoryStream())
            {
                wb.Save(ms);
                ms.Position = 0;
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
                        using (var st = e.Open())
                            System.Xml.Linq.XDocument.Load(st); // throws when not well-formed
                    Assert.Contains(zip.Entries, e => e.FullName == "xl/worksheets/sheet3.xml");
                    Assert.Equal(reports.Count, zip.Entries.Count(e => e.FullName.StartsWith("xl/media/")));
                }

                var dir = Environment.GetEnvironmentVariable("BEAMCHECK_OUT");
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    wb.Save(Path.Combine(dir, "batch.xlsx"));
                }
            }
        }

        [Fact]
        public void Navisworks_search_sets_name_every_beam_and_select_it_by_its_handle()
        {
            var reports = BatchAnalyzer.Run(Load("log_beam_73CB0.json.gz"), new BeamCheckSettings(), new BatchOptions());
            string xml = NavisworksExport.SearchSetsXml(reports);
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var sets = doc.Descendants("selectionset").ToList();
            Assert.Equal(reports.Count, sets.Count);
            var first = reports[0];
            var set = sets.First(x => ((string)x.Attribute("name")).StartsWith(first.Name));
            Assert.Equal(NavisworksExport.TopLevelHandle(first.Id), (string)set.Descendants("data").Single());
            Assert.Equal("AB", NavisworksExport.TopLevelHandle("AB/12"));
        }

        private static double[] Rotate(double[] q, double[] v)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double tx = 2 * (y * v[2] - z * v[1]), ty = 2 * (z * v[0] - x * v[2]), tz = 2 * (x * v[1] - y * v[0]);
            return new[] { v[0] + w * tx + (y * tz - z * ty), v[1] + w * ty + (z * tx - x * tz), v[2] + w * tz + (x * ty - y * tx) };
        }

        [Theory]
        [InlineData(-0.83, 0.5, -0.25)]       // a camera of a viewpoint exported from Navisworks looks like this
        [InlineData(0.62, 0.78, 0.11)]
        [InlineData(0, -1, 0)]
        [InlineData(0, 0, -1)]
        public void The_camera_quaternion_looks_along_the_requested_direction_with_z_up(double fx, double fy, double fz)
        {
            var q = NavisworksExport.LookAtQuaternion(new[] { fx, fy, fz });
            double l = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            var f = Rotate(q, new[] { 0.0, 0, -1 });
            Assert.Equal(fx / l, f[0], 6); Assert.Equal(fy / l, f[1], 6); Assert.Equal(fz / l, f[2], 6);
            var up = Rotate(q, new[] { 0.0, 1, 0 });
            Assert.True(up[2] > -1e-9);
            Assert.Equal(1, Math.Sqrt(q.Sum(c => c * c)), 6);
        }

        [Fact]
        public void Viewpoints_xml_has_one_named_view_per_beam_in_metres()
        {
            var reports = BatchAnalyzer.Run(Load("log_beam_73CB0.json.gz"), new BeamCheckSettings(), new BatchOptions());
            var doc = System.Xml.Linq.XDocument.Parse(NavisworksExport.ViewpointsXml(reports));
            var views = doc.Descendants("view").ToList();
            Assert.Equal(reports.Count, views.Count);
            var v = views.First(x => ((string)x.Attribute("name")).StartsWith(reports[0].Name));
            var pos = v.Descendants("pos3f").First();
            // The camera is within ~12 m of the beam (the drawing is in mm, the file in m).
            double cx = (double)pos.Attribute("x") * 1000, cy = (double)pos.Attribute("y") * 1000;
            Assert.True(Math.Sqrt(Math.Pow(cx - reports[0].CenterX, 2) + Math.Pow(cy - reports[0].CenterY, 2)) < 12500);
            Assert.Equal("m", (string)doc.Root.Attribute("units"));
        }

        [Fact]
        public void A_stack_through_the_beam_is_a_support_and_its_upper_part_is_still_a_load_at_the_same_place()
        {
            ScaffoldElement Std(string id, double x, double z0, double z1) => new ScaffoldElement
            {
                Id = id, Kind = ElementKind.Standard, Start = new Vec3(x, 0, z0), End = new Vec3(x, 0, z1), ZMin = z0, ZMax = z1, WeightKg = 8,
            };
            var beam = new ScaffoldElement
            {
                Id = "BM", Kind = ElementKind.Beam, Description = "SRU 147", Start = new Vec3(0, 0, 1120), End = new Vec3(1472, 0, 1120),
                Width = 162, ZMin = 1000, ZMax = 1240, WeightKg = 38,
            };
            var all = new List<ScaffoldElement> { beam, Std("A", 300, 0, 1100), Std("B", 300, 1100, 2000), Std("C", 1200, 1240, 2240) };
            var r = BatchAnalyzer.Run(all, new BeamCheckSettings(), new BatchOptions()).Single();

            var support = Assert.Single(r.Supports);
            Assert.Equal(300, support.Position, 0);
            Assert.Equal(2, r.Loads.Count);                       // the stack above the support and the stand at 1200
            Assert.Contains(r.Loads, l => Math.Abs(l.Position - support.Position) < 1);
            Assert.False(r.ContinuousSupport);
        }

        [Fact]
        public void Two_beams_joined_by_a_connector_are_one_long_beam_with_the_gap_included()
        {
            ScaffoldElement Beam(string id, double x0, double x1, string desc) => new ScaffoldElement
            {
                Id = id, Kind = ElementKind.Beam, Description = desc, Article = "1",
                Start = new Vec3(x0, 0, 1120), End = new Vec3(x1, 0, 1120), Width = 162, ZMin = 1000, ZMax = 1240, WeightKg = 100,
            };
            var connector = new ScaffoldElement
            {
                Id = "CN", Kind = ElementKind.BeamConnector, Description = "Kletterschienenverbinder RCS 97",
                Start = new Vec3(1900, 0, 1120), End = new Vec3(2865, 0, 1120), Width = 78, ZMin = 1000, ZMax = 1300, WeightKg = 10,
            };
            // Beam 1: 0..2000 (its end is inside the connector), beam 2: 2800..4800 with a gap of 800 mm between them.
            var all = new List<ScaffoldElement> { Beam("B1", 0, 2000, "RCS 198"), Beam("B2", 2800, 4800, "RCS 198"), connector,
                new ScaffoldElement { Id = "S", Kind = ElementKind.Standard, Start = new Vec3(500, 0, 0), End = new Vec3(500, 0, 1100), ZMin = 0, ZMax = 1100, WeightKg = 5 },
                new ScaffoldElement { Id = "T", Kind = ElementKind.Standard, Start = new Vec3(4000, 0, 0), End = new Vec3(4000, 0, 1100), ZMin = 0, ZMax = 1100, WeightKg = 5 } };
            var reports = BatchAnalyzer.Run(all, new BeamCheckSettings(), new BatchOptions());

            var r = Assert.Single(reports);
            Assert.Equal(4800, r.Length, 0);
            Assert.Equal(2, r.Supports.Count);
            Assert.Equal(500, r.Supports[0].Position, 0);
            Assert.Equal(4000, r.Supports[1].Position, 0);
            Assert.Equal(new[] { "B1", "B2", "CN" }, r.PartIds.OrderBy(x => x).ToArray());
            Assert.Contains("RCS 198 + RCS 198", r.Description);
            Assert.Equal(210, r.WeightKg, 6);
        }

        [Fact]
        public void A_rail_connector_is_not_taken_for_a_beam_although_its_name_says_RCS()
        {
            var sig = new Recognition.ElementSignature { EntityType = "Geo", DxfName = "PERI_AEC_DB_BAUTEIL" };
            sig.Add("COM:ArtNr", "111390");
            sig.Add("COM:Bezeichnung", "Kletterschienenverbinder RCS 97");
            Assert.Equal(ElementKind.BeamConnector, new Recognition.ElementClassifier(new BeamCheckSettings()).Classify(sig).Kind);
        }

        [Fact]
        public void A_stack_beside_the_beam_is_neither_a_support_nor_a_load()
        {
            ScaffoldElement Std(string id, double x, double y, double z0, double z1) => new ScaffoldElement
            {
                Id = id, Kind = ElementKind.Standard, Start = new Vec3(x, y, z0), End = new Vec3(x, y, z1), ZMin = z0, ZMax = z1, WeightKg = 8,
            };
            var beam = new ScaffoldElement
            {
                Id = "BM", Kind = ElementKind.Beam, Description = "RCS 148", Start = new Vec3(0, 0, 1120), End = new Vec3(1480, 0, 1120),
                Width = 82, ZMin = 1020, ZMax = 1220, WeightKg = 38,
            };
            // Under the beam and reaching it: a support at 300. Beside it by 100 mm (a stand with a console): nothing.
            var all = new List<ScaffoldElement>
            {
                beam, Std("A", 300, 20, 0, 1100), Std("B", 900, 100, 0, 1100), Std("C", 900, 100, 1100, 2000),
            };
            var r = BatchAnalyzer.Run(all, new BeamCheckSettings(), new BatchOptions()).Single();
            var support = Assert.Single(r.Supports);
            Assert.Equal(300, support.Position, 0);
            Assert.DoesNotContain(r.Loads, l => l.Position > 800);
        }

        [Fact]
        public void A_stack_hanging_from_the_beam_is_not_a_support_unless_it_ends_with_a_spindle_jack_or_pin()
        {
            ScaffoldElement Std(string id, double x, double z0, double z1, string art, string name) => new ScaffoldElement
            {
                Id = id, Kind = ElementKind.Standard, Start = new Vec3(x, 0, z0), End = new Vec3(x, 0, z1), ZMin = z0, ZMax = z1, WeightKg = 8,
                Article = art, Description = name,
            };
            var beam = new ScaffoldElement
            {
                Id = "BM", Kind = ElementKind.Beam, Description = "RCS 148", Start = new Vec3(0, 0, 3120), End = new Vec3(2000, 0, 3120),
                Width = 82, ZMin = 3020, ZMax = 3220, WeightKg = 38,
            };
            var all = new List<ScaffoldElement>
            {
                beam,
                // 300: stack ends at the ground with a jack (UJB 50) and a base plate: a support.
                Std("J", 300, 0, 505, "100412", "UJB 50"), Std("A", 300, 505, 3100, "101306", "UVR 100"),
                // 1200: a long standard hanging from the beam, its lower end is a plain standard: not a support.
                Std("H", 1200, 1000, 3100, "101306", "UVR 100"),
                // 1800: stack standing on a pin: a support.
                new ScaffoldElement { Id = "Z", Kind = ElementKind.Accessory, Article = "130681", Description = "UH Zapfen", Start = new Vec3(1800, 0, 450), End = new Vec3(1800, 40, 450), ZMin = 400, ZMax = 700, WeightKg = 0.2 },
                Std("P", 1800, 500, 3100, "101306", "UVR 100"),
            };
            var r = BatchAnalyzer.Run(all, new BeamCheckSettings(), new BatchOptions()).Single();
            Assert.Equal(2, r.Supports.Count);
            Assert.InRange(r.Supports[0].Position, 295, 305);
            Assert.InRange(r.Supports[1].Position, 1795, 1805);
        }
    }
}
