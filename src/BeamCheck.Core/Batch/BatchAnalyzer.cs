using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;

namespace BeamCheck.Core.Batch
{
    public sealed class BatchOptions
    {
        /// <summary>0 = the default class of the settings.</summary>
        public int LoadClass { get; set; }
    }

    public sealed class BatchProgress
    {
        public int Done { get; set; }

        public int Total { get; set; }

        public string Current { get; set; }
    }

    /// <summary>
    /// Checks every beam of a model without any picking: finds the beams, names them, finds the standards standing on
    /// each, the supports under it, and reports supports and loads left to right.
    /// </summary>
    public static class BatchAnalyzer
    {
        public const string NamePrefix = "B-";

        /// <summary>Recognised beams (kind Beam), each once.</summary>
        public static List<ScaffoldElement> FindBeams(IEnumerable<ScaffoldElement> all)
        {
            var seen = new HashSet<string>();
            return all.Where(e => e.Kind == ElementKind.Beam && e.PlanLength > 1 && seen.Add(e.Id)).ToList();
        }

        /// <summary>Left edge of a beam: the end with the smaller X (smaller Y for a beam running along Y).</summary>
        public static bool IsReversed(ScaffoldElement beam)
        {
            double dx = beam.End.X - beam.Start.X;
            if (Math.Abs(dx) > 1)
                return dx < 0;
            return beam.End.Y < beam.Start.Y;
        }

        public static List<BeamReport> Run(IList<ScaffoldElement> all, BeamCheckSettings settings, BatchOptions options,
            Action<BatchProgress> progress = null, Func<bool> cancelled = null)
        {
            var parts = new Dictionary<string, List<string>>();
            all = JoinBeams(all, parts, settings);
            var beams = FindBeams(all)
                .OrderBy(b => Math.Round(b.ZMin / 50.0))
                .ThenBy(b => Math.Min(b.Start.X, b.End.X))
                .ThenBy(b => Math.Min(b.Start.Y, b.End.Y))
                .ToList();
            var reports = new List<BeamReport>();
            for (int i = 0; i < beams.Count; i++)
            {
                if (cancelled != null && cancelled())
                    break;
                string name = NamePrefix + (i + 1).ToString("000", CultureInfo.InvariantCulture);
                progress?.Invoke(new BatchProgress { Done = i, Total = beams.Count, Current = name });
                var report = AnalyzeBeam(name, beams[i], all, settings, options);
                if (parts.TryGetValue(beams[i].Id, out var ids))
                {
                    report.PartIds.AddRange(ids);
                    foreach (var id in ids)
                        report.Related.Add(id);
                }
                else
                {
                    report.PartIds.Add(beams[i].Id);
                }

                reports.Add(report);
            }

            progress?.Invoke(new BatchProgress { Done = reports.Count, Total = beams.Count, Current = null });
            return reports;
        }

        /// <summary>
        /// Beams joined end to end by a connector (Kletterschienenverbinder) act as one rigid, longer beam: from the start of the first to
        /// the end of the last, the gap for the connection included. Returns the elements with those beams replaced by the joined beam
        /// and the connectors removed; <paramref name="parts"/> maps the joined beam's id to the ids of everything it is made of.
        /// </summary>
        public static List<ScaffoldElement> JoinBeams(IList<ScaffoldElement> all, Dictionary<string, List<string>> parts, BeamCheckSettings s)
        {
            var beams = all.Where(e => e.Kind == ElementKind.Beam && e.PlanLength > 1).ToList();
            var connectors = all.Where(e => e.Kind == ElementKind.BeamConnector && e.PlanLength > 1).ToList();
            if (connectors.Count == 0 || beams.Count < 2)
                return all.ToList();

            const double axisTol = 150, endTol = 200, zTol = 250;
            // Union of the beams every connector touches.
            var group = beams.ToDictionary(b => b.Id, b => b.Id);
            string Find(string id)
            {
                while (group[id] != id)
                    id = group[id];
                return id;
            }

            var usedConnectors = new HashSet<string>();
            var connectorOf = new Dictionary<string, List<ScaffoldElement>>();
            foreach (var c in connectors)
            {
                var dir = (c.End - c.Start).PlanDirection();
                var touched = new List<ScaffoldElement>();
                foreach (var b in beams)
                {
                    var bd = (b.End - b.Start).PlanDirection();
                    if (Math.Abs(bd.DotXY(dir)) < 0.98 || Math.Abs((b.ZMin + b.ZMax) / 2 - (c.ZMin + c.ZMax) / 2) > zTol + Math.Max(b.ZMax - b.ZMin, c.ZMax - c.ZMin) / 2)
                        continue;
                    // One end of the beam lies within the connector (along its axis), on its axis.
                    foreach (var end in new[] { b.Start, b.End })
                    {
                        double t = PlanGeometry.ProjectXY(c.Start, c.End, end, out double off);
                        double along = t * c.PlanLength;
                        if (off <= axisTol && along >= -endTol && along <= c.PlanLength + endTol)
                        {
                            touched.Add(b);
                            break;
                        }
                    }
                }

                if (touched.Count < 2)
                    continue;
                usedConnectors.Add(c.Id);
                for (int i = 1; i < touched.Count; i++)
                    group[Find(touched[i].Id)] = Find(touched[0].Id);
                foreach (var b in touched)
                {
                    if (!connectorOf.TryGetValue(b.Id, out var list))
                        connectorOf[b.Id] = list = new List<ScaffoldElement>();
                    list.Add(c);
                }
            }

            var result = new List<ScaffoldElement>();
            var done = new HashSet<string>();
            var joined = new HashSet<string>();
            foreach (var g in beams.GroupBy(b => Find(b.Id)).Where(g => g.Count() > 1))
            {
                var members = g.ToList();
                // The far ends of the group along its axis.
                var dir = (members[0].End - members[0].Start).PlanDirection();
                var ends = members.SelectMany(b => new[] { b.Start, b.End }).OrderBy(p => p.DotXY(dir)).ToList();
                var first = members.OrderBy(b => Math.Min(b.Start.DotXY(dir), b.End.DotXY(dir))).First();
                var conns = members.SelectMany(b => connectorOf.TryGetValue(b.Id, out var l) ? l : new List<ScaffoldElement>()).Distinct().ToList();
                var merged = new ScaffoldElement
                {
                    Id = first.Id,
                    Kind = ElementKind.Beam,
                    Article = string.Join(" + ", members.Select(b => b.Article).Where(a => !string.IsNullOrEmpty(a)).Distinct()),
                    Description = string.Join(" + ", members.OrderBy(b => Math.Min(b.Start.DotXY(dir), b.End.DotXY(dir))).Select(b => b.Description ?? b.Id)) + " (Verbinder)",
                    Start = ends.First().WithZ((first.Start.Z + first.End.Z) / 2),
                    End = ends.Last().WithZ((first.Start.Z + first.End.Z) / 2),
                    Width = members.Max(b => b.Width),
                    ZMin = members.Min(b => b.ZMin),
                    ZMax = members.Max(b => b.ZMax),
                    WeightKg = members.Sum(b => b.WeightKg) + conns.Sum(c => c.WeightKg),
                };
                result.Add(merged);
                parts[merged.Id] = members.Select(b => b.Id).Concat(conns.Select(c => c.Id)).Distinct().ToList();
                foreach (var b in members)
                    joined.Add(b.Id);
                foreach (var c in conns)
                    done.Add(c.Id);
            }

            foreach (var e in all)
            {
                if (joined.Contains(e.Id) || done.Contains(e.Id))
                    continue;
                result.Add(e);
            }

            return result;
        }

        public static BeamReport AnalyzeBeam(string name, ScaffoldElement beam, IList<ScaffoldElement> all, BeamCheckSettings s, BatchOptions options)
        {
            bool rev = IsReversed(beam);
            double len = beam.PlanLength;
            int loadClass = options != null && options.LoadClass > 0 ? options.LoadClass : s.DefaultLoadClass;
            var report = new BeamReport
            {
                Name = name,
                Id = beam.Id,
                Article = beam.Article,
                Description = beam.Description,
                Length = len,
                StartX = beam.Start.X,
                StartY = beam.Start.Y,
                EndX = beam.End.X,
                EndY = beam.End.Y,
                ZMin = beam.ZMin,
                ZMax = beam.ZMax,
                Height = beam.ZMax - beam.ZMin,
                WeightKg = beam.WeightKg,
                Reversed = rev,
                LoadClass = loadClass,
                GammaG = s.GammaG,
                GammaQ = s.GammaQ,
                SelfWeightKn = LoadCalculator.KgToKn(beam.WeightKg),
            };
            double Left(double posFromStart) => Math.Max(0, Math.Min(len, rev ? len - posFromStart : posFromStart));

            var standards = all.Where(e => e.Kind == ElementKind.Standard).ToList();
            var analyzer = new ScaffoldAnalyzer(s);
            var supports = analyzer.FindSupports(beam, standards, all);

            // Standards standing on the beam (their foot at its top). A stack that passes through the beam still loads it with
            // the part above: that load is listed with the support under it.
            var picked = standards.Where(st => StandsOn(beam, st, s)).ToList();

            var points = new List<PointLoad>();
            var notes = new List<string>();
            if (picked.Count > 0)
            {
                var input = new AnalysisInput { Beam = beam, SelectedStandards = picked, Candidates = NearBy(all, beam, picked, s) };
                var session = new BeamLoadSession(s, input);
                session.Options.LoadClass = loadClass;
                session.Recalculate();
                points.AddRange(session.Result.Points);
                report.Related.UnionWith(session.Topology.UsedElementIds);
                notes.AddRange(session.Result.Warnings.Where(w => !w.StartsWith("ℹ", StringComparison.Ordinal)).Distinct());
            }
            else
            {
                notes.Add("Keine Ständer auf dem Träger.");
            }

            report.Related.Add(beam.Id);
            foreach (var sp in supports)
                foreach (var sec in sp.Column.Sections)
                    report.Related.Add(sec.Id);

            // Loads, left to right.
            foreach (var p in points.OrderBy(p => Left(p.Position)))
            {
                report.Loads.Add(new BeamEvent
                {
                    Kind = BeamEventKind.Load,
                    Position = Left(p.Position),
                    Gk = p.Gk,
                    Qk = p.Qk,
                    Fd = p.Fd,
                    AreaM2 = p.AreaM2,
                    Source = p.Name,
                    Detail = string.Format(CultureInfo.InvariantCulture, "stack Z {0:0}..{1:0}, offset from beam axis {2:0} mm, parts {3}",
                        p.Column.ZBottom, p.Column.ZTop, p.Column.OffsetFromBeamAxis, string.Join(",", p.Column.Sections.Select(c => c.Id))),
                });
            }

            report.Loads.Sort((a, b) => a.Position.CompareTo(b.Position));
            for (int i = 0; i < report.Loads.Count; i++)
                report.Loads[i].Label = LetterLabel(i);

            // Supports, left to right: always pin supports; shown even where a load goes straight down onto one.
            if (supports.Count == 0)
            {
                report.ContinuousSupport = true;
                notes.Add("Keine Ständer unter dem Träger: er liegt auf festem, ebenem Untergrund.");
            }
            else
            {
                var events = supports.Select(x => new BeamEvent
                {
                    Kind = BeamEventKind.Support,
                    Position = Left(x.Position),
                    Detail = string.Format(CultureInfo.InvariantCulture, "stack Z {0:0}..{1:0}, offset from beam axis {2:0} mm, parts {3}",
                        x.Column.ZBottom, x.Column.ZTop, x.Offset, string.Join(",", x.Column.Sections.Select(c => c.Id))),
                }).OrderBy(e => e.Position).ToList();
                for (int j = 0; j < events.Count; j++)
                    events[j].Label = "S" + (j + 1).ToString(CultureInfo.InvariantCulture);
                report.Supports.AddRange(events);
            }

            // Distances along the chain.
            double prev = 0;
            foreach (var e in report.Events.ToList())
            {
                e.Delta = e.Position - prev;
                prev = e.Position;
            }

            report.Notes.AddRange(notes);
            report.Proof = Proofs.BeamProofs.Check(report, s);
            return report;
        }

        private static bool StandsOn(ScaffoldElement beam, ScaffoldElement st, BeamCheckSettings s)
        {
            if (st.ZMax <= beam.ZMax + 1 || Math.Abs(st.ZMin - beam.ZMax) > s.ColumnOnBeamZTolerance)
                return false;
            double t = PlanGeometry.ProjectXY(beam.Start, beam.End, st.Start, out double offset);
            double pos = t * beam.PlanLength;
            return offset <= beam.Width / 2 + s.SupportPlanTolerance && pos >= -s.BeamPlanTolerance && pos <= beam.PlanLength + s.BeamPlanTolerance;
        }

        /// <summary>Elements within the search radius of the standards standing on the beam (a pre-filter, the analysis is the same).</summary>
        private static List<ScaffoldElement> NearBy(IList<ScaffoldElement> all, ScaffoldElement beam, List<ScaffoldElement> picked, BeamCheckSettings s)
        {
            double r = s.SearchRadius;
            double x0 = picked.Min(p => p.Start.X) - r, x1 = picked.Max(p => p.Start.X) + r;
            double y0 = picked.Min(p => p.Start.Y) - r, y1 = picked.Max(p => p.Start.Y) + r;
            double z0 = beam.ZMin - r;
            return all.Where(e => e.Id != beam.Id
                                  && Math.Max(e.Start.X, e.End.X) >= x0 && Math.Min(e.Start.X, e.End.X) <= x1
                                  && Math.Max(e.Start.Y, e.End.Y) >= y0 && Math.Min(e.Start.Y, e.End.Y) <= y1
                                  && Math.Max(e.ZMax, Math.Max(e.Start.Z, e.End.Z)) >= z0).ToList();
        }

        /// <summary>A, B … Z, AA, AB …</summary>
        public static string LetterLabel(int index)
        {
            var sb = new System.Text.StringBuilder();
            index++;
            while (index > 0)
            {
                index--;
                sb.Insert(0, (char)('A' + index % 26));
                index /= 26;
            }

            return sb.ToString();
        }
    }
}
