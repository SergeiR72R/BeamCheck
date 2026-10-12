using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace BeamCheck.Core.Tests
{
    /// <summary>The recognised parts around SRU 197 (a customer drawing, rebuilt from the BEAMLOAD log of the plugin).</summary>
    public class BigLogTests
    {
        private readonly ITestOutputHelper _out;
        public BigLogTests(ITestOutputHelper o) { _out = o; }

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

        private static ScaffoldElement ToElement(Rec r)
        {
            var kind = (ElementKind)Enum.Parse(typeof(ElementKind), r.Kind);
            double zm = (r.Z0 + r.Z1) / 2;
            bool st = kind == ElementKind.Standard;
            return new ScaffoldElement
            {
                Id = r.Id, Kind = kind, Article = r.Art, Description = r.Name,
                Start = new Vec3(r.X0, r.Y0, st ? r.Z0 : zm), End = new Vec3(r.X1, r.Y1, st ? r.Z1 : zm),
                Width = r.W, ZMin = r.Z0, ZMax = r.Z1, WeightKg = 5,
            };
        }

        private BeamLoadSession Run(BeamCheckSettings settings)
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(BigLogTests).Assembly.Location), "Fixtures", "log_beam_73CB0.json.gz");
            List<Rec> recs;
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
                recs = (List<Rec>)new DataContractJsonSerializer(typeof(List<Rec>)).ReadObject(gz);
            var beam = ToElement(recs.Single(r => r.Role == "beam"));
            var picked = recs.Where(r => r.Role == "picked").Select(ToElement).ToList();
            var cand = recs.Where(r => r.Role == "near").Select(ToElement).ToList();
            return new BeamLoadSession(settings, new AnalysisInput { Beam = beam, SelectedStandards = picked, Candidates = cand });
        }

        [Fact]
        public void Probe()
        {
            var s = Run(new BeamCheckSettings());
            _out.WriteLine($"columns {s.Topology.Columns.Count} levels {s.Topology.Levels.Count} upper {s.Topology.UpperBeams.Count} used {s.Topology.UsedElementIds.Count}");
            foreach (var l in s.Topology.Levels)
                _out.WriteLine($"level {l.Index} Z={l.Z:0} area {s.LevelArea(l):0.000}");
            foreach (var u in s.Topology.UpperBeams)
                _out.WriteLine($"upper {u.Beam.Id} {u.Beam.Article} {u.Beam.Description} L={u.Beam.PlanLength:0} Z {u.Beam.ZMin:0}..{u.Beam.ZMax:0} supports=[{string.Join(",", u.Supports.Select(x => x.Position.ToString("0") + (x.Column == null ? "(x)" : "(S)")))}] loaded=[{string.Join(",", u.Loaded.Select(x => x.Name + "@" + x.Position.ToString("0")))}]");
            var neg = s.Topology.Contributions.Where(c => c.Type == ContributionType.Deck && c.Share < 0).ToList();
            _out.WriteLine($"negative deck contributions: {neg.Count}, area {neg.Sum(c => c.AreaM2):0.000}; positive {s.Topology.Contributions.Where(c => c.Type == ContributionType.Deck && c.Share > 0).Sum(c => c.AreaM2):0.000}");
            foreach (var g in neg.GroupBy(c => c.ViaBeam?.Id).Take(10))
                _out.WriteLine($"  via beam {g.Key}: {g.Count()} contribs, area {g.Sum(c => c.AreaM2):0.000}");
            foreach (var p in s.Result.Points)
                _out.WriteLine($"{p.Name} A={p.AreaM2:0.00} Gk={p.Gk:0.00} Qk={p.Qk:0.00} transfer={p.TransferGk:0.00}+{p.TransferQk:0.00}");
        }
    }
}
