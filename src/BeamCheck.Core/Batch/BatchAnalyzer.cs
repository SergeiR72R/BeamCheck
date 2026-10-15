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
                reports.Add(AnalyzeBeam(name, beams[i], all, settings, options));
            }

            progress?.Invoke(new BatchProgress { Done = reports.Count, Total = beams.Count, Current = null });
            return reports;
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
            var supports = analyzer.FindSupports(beam, standards);

            // Standards standing on the beam (their foot at its top). A stack that passes through the beam is its support.
            var picked = standards.Where(st => StandsOn(beam, st, s) && !supports.Any(sp => Vec3.DistanceXY(sp.Column.Axis, st.Start) <= s.ColumnXYTolerance)).ToList();

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
                });
            }

            report.Loads.Sort((a, b) => a.Position.CompareTo(b.Position));
            for (int i = 0; i < report.Loads.Count; i++)
                report.Loads[i].Label = LetterLabel(i);

            // Supports, left to right, with their reactions by the lever rule per span (as the load transfer through beams).
            if (supports.Count == 0)
            {
                report.ContinuousSupport = true;
                notes.Add("Keine Ständer unter dem Träger: er liegt auf festem, ebenem Untergrund.");
            }
            else
            {
                var pos = supports.Select(x => x.Position).ToArray();
                var reaction = new double[pos.Length];
                var reactionG = new double[pos.Length];
                var reactionQ = new double[pos.Length];
                foreach (var p in points)
                {
                    var f = ScaffoldAnalyzer.Distribute(pos, p.Position);
                    for (int j = 0; j < pos.Length; j++)
                    {
                        reactionG[j] += p.Gk * f[j];
                        reactionQ[j] += p.Qk * f[j];
                    }
                }

                const int n = 40;
                for (int k = 0; k < n; k++)
                {
                    var f = ScaffoldAnalyzer.Distribute(pos, (k + 0.5) / n * len);
                    for (int j = 0; j < pos.Length; j++)
                        reactionG[j] += report.SelfWeightKn * f[j] / n;
                }

                var events = new List<BeamEvent>();
                for (int j = 0; j < pos.Length; j++)
                {
                    reaction[j] = reactionG[j] + reactionQ[j];
                    events.Add(new BeamEvent
                    {
                        Kind = BeamEventKind.Support,
                        Position = Left(pos[j]),
                        Gk = reactionG[j],
                        Qk = reactionQ[j],
                        Fd = s.GammaG * reactionG[j] + s.GammaQ * reactionQ[j],
                    });
                }

                events.Sort((a, b) => a.Position.CompareTo(b.Position));
                for (int j = 0; j < events.Count; j++)
                    events[j].Label = "S" + (j + 1).ToString(CultureInfo.InvariantCulture);
                report.Supports.AddRange(events);
                if (events.Any(e => e.Fk < -0.005))
                    notes.Add("Ein Auflager wird entlastet (negative Auflagerkraft, Abheben): Kragarm-Last nach Hebelgesetz.");
            }

            // Distances along the chain.
            double prev = 0;
            foreach (var e in report.Events.ToList())
            {
                e.Delta = e.Position - prev;
                prev = e.Position;
            }

            report.Notes.AddRange(notes);
            return report;
        }

        private static bool StandsOn(ScaffoldElement beam, ScaffoldElement st, BeamCheckSettings s)
        {
            if (st.ZMax <= beam.ZMax + 1 || Math.Abs(st.ZMin - beam.ZMax) > s.ColumnOnBeamZTolerance)
                return false;
            double t = PlanGeometry.ProjectXY(beam.Start, beam.End, st.Start, out double offset);
            double pos = t * beam.PlanLength;
            return offset <= beam.Width / 2 + s.BeamPlanTolerance && pos >= -s.BeamPlanTolerance && pos <= beam.PlanLength + s.BeamPlanTolerance;
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
