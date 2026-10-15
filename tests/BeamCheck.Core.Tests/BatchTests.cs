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
                _out.WriteLine($"{r.Name} {r.Id} {r.Description} L={r.Length:0} Z {r.ZMin:0}..{r.ZMax:0} rev={r.Reversed} supports={r.Supports.Count} loads={r.Loads.Count} cont={r.ContinuousSupport} maxF={r.MaxLoadFk:0.00} maxR={r.MaxReactionFk:0.00}");
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
        public void Reactions_add_up_to_the_loads_and_the_weight_of_the_beam()
        {
            var all = Load("log_beam_73CB0.json.gz");
            var reports = BatchAnalyzer.Run(all, new BeamCheckSettings(), new BatchOptions());
            foreach (var r in reports.Where(r => !r.ContinuousSupport && r.Loads.Count > 0))
            {
                double down = r.Loads.Sum(l => l.Fk) + r.SelfWeightKn;
                double up = r.Supports.Sum(s => s.Fk);
                Assert.True(Math.Abs(down - up) < 1e-6 + 1e-6 * down, $"{r.Name}: loads {down:0.000} reactions {up:0.000}");
            }
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
    }
}
