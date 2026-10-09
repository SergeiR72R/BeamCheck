using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;

namespace BeamCheck.Core.Analysis
{
    public sealed class AnalysisInput
    {
        public ScaffoldElement Beam { get; set; }

        /// <summary>Standard sections the user picked (any section of a column is enough).</summary>
        public IList<ScaffoldElement> SelectedStandards { get; set; } = new List<ScaffoldElement>();

        /// <summary>All recognised elements around the beam (standards, ledgers, decks, …).</summary>
        public IList<ScaffoldElement> Candidates { get; set; } = new List<ScaffoldElement>();

        /// <summary>Elements the user excluded manually.</summary>
        public ISet<string> ExcludedIds { get; set; } = new HashSet<string>();
    }

    /// <summary>
    /// Builds columns from the selected standards and finds how ledgers and decks
    /// of every level hand their load to those columns.
    ///
    /// Load path: deck → its two bearing ledgers (½ each) → ledger end nodes
    /// (by lever rule along the ledger) → standards. A deck in a regular bay
    /// therefore gives ¼ of its load to each corner standard; missing decks,
    /// edge bays and unequal bays are handled automatically.
    /// </summary>
    public sealed class ScaffoldAnalyzer
    {
        private readonly BeamCheckSettings _s;

        public ScaffoldAnalyzer(BeamCheckSettings settings)
        {
            _s = settings;
        }

        public Topology Analyze(AnalysisInput input)
        {
            if (input.Beam == null)
                throw new ArgumentException("Beam is required.", nameof(input));

            var topo = new Topology { Beam = input.Beam };
            bool Active(ScaffoldElement e) => e != null && !input.ExcludedIds.Contains(e.Id);

            var combined = input.Candidates.Where(Active).ToList();
            var ids = new HashSet<string>(combined.Select(e => e.Id));
            foreach (var s in input.SelectedStandards.Where(Active))
                if (ids.Add(s.Id))
                    combined.Add(s);
            var all = Deduplicate(combined).ToList();

            var standards = all.Where(e => e.Kind == ElementKind.Standard).ToList();
            BuildColumns(topo, input.SelectedStandards.Where(Active).ToList(), standards);
            if (topo.Columns.Count == 0)
            {
                topo.Warnings.Add("Не найдено ни одной стойки над балкой.");
                return topo;
            }

            // Columns that carry load: those on the beam under check plus, found below, those standing on
            // upper beams that rest on them. Their load reaches the beam through the upper beams.
            var active = new List<Column>(topo.Columns);
            var upper = all.Where(e => e.Kind == ElementKind.Beam && !IsSameBeam(e, input.Beam)).ToList();
            FindUpperBeams(topo, upper, standards, active);

            var sink = new List<Contribution>();
            foreach (var c in active)
            {
                foreach (var sec in c.Sections)
                {
                    sink.Add(new Contribution { Column = c, Source = sec, Type = ContributionType.StandardSelfWeight, Share = 1 });
                    topo.UsedElementIds.Add(sec.Id);
                }
            }

            var ledgers = all.Where(e => e.Kind == ElementKind.Ledger).ToList();
            AddLinearSelfWeight(topo, active, sink, ledgers, ContributionType.LedgerSelfWeight);
            AddLinearSelfWeight(topo, active, sink, all.Where(e => e.Kind == ElementKind.Diagonal || e.Kind == ElementKind.Accessory).ToList(), ContributionType.OtherSelfWeight);
            AddDecks(topo, active, sink, all.Where(e => e.Kind == ElementKind.Deck).ToList(), ledgers);
            AddUpperBeamWeights(topo, sink);

            // Pass everything standing on upper beams down to the columns of the beam under check.
            foreach (var c in sink)
                topo.Contributions.AddRange(Resolve(c, topo, 0));
            PruneLevels(topo);
            return topo;
        }

        private static bool IsSameBeam(ScaffoldElement a, ScaffoldElement b) =>
            a.Id == b.Id || (Vec3.DistanceXY(a.Start, b.Start) < 20 && Vec3.DistanceXY(a.End, b.End) < 20 && Math.Abs(a.Start.Z - b.Start.Z) < 20);

        // ---------------- upper beams ----------------

        /// <summary>All vertical stacks (runs of continuous standards on one axis) of the candidates.</summary>
        private List<Column> BuildRuns(List<ScaffoldElement> standards)
        {
            var runs = new List<Column>();
            var clusters = new List<List<ScaffoldElement>>();
            foreach (var st in standards)
            {
                var cl = clusters.FirstOrDefault(c => Vec3.DistanceXY(c[0].Start, st.Start) <= _s.ColumnXYTolerance);
                if (cl == null)
                    clusters.Add(new List<ScaffoldElement> { st });
                else
                    cl.Add(st);
            }

            foreach (var cl in clusters)
            {
                var sorted = cl.OrderBy(x => x.ZMin).ToList();
                Column cur = null;
                foreach (var sec in sorted)
                {
                    if (cur == null || sec.ZMin > cur.ZTop + _s.ColumnGapTolerance)
                    {
                        cur = new Column { ZBottom = sec.ZMin, ZTop = sec.ZMax };
                        runs.Add(cur);
                    }

                    cur.Sections.Add(sec);
                    cur.ZTop = Math.Max(cur.ZTop, sec.ZMax);
                }
            }

            foreach (var r in runs)
                r.Axis = new Vec3(r.Sections.Average(x => x.Start.X), r.Sections.Average(x => x.Start.Y), r.ZBottom);
            return runs;
        }

        private bool OnBeamFootprint(ScaffoldElement beam, Vec3 p, out double position)
        {
            double t = PlanGeometry.ProjectXY(beam.Start, beam.End, p, out double offset);
            double len = beam.PlanLength;
            position = t * len;
            return offset <= beam.Width / 2 + _s.BeamPlanTolerance && position >= -_s.BeamPlanTolerance && position <= len + _s.BeamPlanTolerance;
        }

        private static bool SameStack(Column a, Column b, double tol) =>
            Vec3.DistanceXY(a.Axis, b.Axis) <= tol && a.ZBottom <= b.ZTop && b.ZBottom <= a.ZTop;

        /// <summary>
        /// Finds beams that rest on active columns and the stands that stand on them, repeatedly
        /// (a stand on an upper beam can itself carry another beam).
        /// </summary>
        private void FindUpperBeams(Topology topo, List<ScaffoldElement> beams, List<ScaffoldElement> standards, List<Column> active)
        {
            if (beams.Count == 0)
                return;
            var runs = BuildRuns(standards);
            var done = new HashSet<string>();
            double tol = _s.ColumnXYTolerance;

            for (int pass = 0; pass < 5; pass++)
            {
                bool progress = false;
                foreach (var u in beams)
                {
                    if (done.Contains(u.Id))
                        continue;

                    var link = new BeamLink { Beam = u };
                    foreach (var run in runs)
                    {
                        // The beam rests on (or inside the head of) this stack.
                        if (!(run.ZBottom < u.ZMin && u.ZMin <= run.ZTop + _s.UpperBeamBearingTolerance))
                            continue;
                        if (!OnBeamFootprint(u, run.Axis, out double pos))
                            continue;
                        var carrier = active.FirstOrDefault(c => SameStack(c, run, tol));
                        link.Supports.Add(new BeamSupport { Column = carrier, Position = pos });
                    }

                    if (!link.Supports.Any(x => x.Column != null))
                        continue;

                    done.Add(u.Id);
                    progress = true;
                    link.Supports.Sort((a, b) => a.Position.CompareTo(b.Position));

                    // Stands on top of the upper beam.
                    foreach (var run in runs)
                    {
                        double gap = run.ZBottom - u.ZMax;
                        if (gap < -_s.ColumnOnBeamZTolerance || gap > _s.ColumnOnBeamZTolerance)
                            continue;
                        if (!OnBeamFootprint(u, run.Axis, out double pos))
                            continue;
                        if (active.Any(c => SameStack(c, run, tol)) || link.Supports.Any(x => x.Column != null && SameStack(x.Column, run, tol)))
                            continue;
                        run.SupportBeam = u;
                        run.Position = pos;
                        run.OnBeam = true;
                        run.Name = string.Format(CultureInfo.InvariantCulture, "{0}/{1:0}", u.DisplayName, pos);
                        link.Loaded.Add(run);
                        active.Add(run);
                    }

                    topo.UpperBeams.Add(link);
                    topo.UsedElementIds.Add(u.Id);
                    topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "ℹ Балка {0} опирается на {1} стоек ({2} из выбранных), на ней стоит {3} стоек — их нагрузка передана на выбранные стойки.",
                        u.DisplayName, link.Supports.Count, link.Supports.Count(x => x.Column != null), link.Loaded.Count));
                    if (link.Loaded.Count == 0)
                        topo.Warnings.Add("ℹ На балке " + u.DisplayName + " стоек не найдено, учтён только её вес.");
                    if (u.WeightKg <= 0)
                        topo.Warnings.Add("Вес балки " + u.DisplayName + " неизвестен (0 кг) — добавьте артикул в BeamCheck.weights.csv.");
                }

                if (!progress)
                    break;
            }
        }

        private void AddUpperBeamWeights(Topology topo, List<Contribution> sink)
        {
            const int n = 40;
            foreach (var link in topo.UpperBeams)
            {
                var pos = link.Supports.Select(x => x.Position).ToArray();
                double len = link.Beam.PlanLength;
                var share = new double[pos.Length];
                for (int j = 0; j < n; j++)
                {
                    var f = Distribute(pos, (j + 0.5) / n * len);
                    for (int i = 0; i < pos.Length; i++)
                        share[i] += f[i] / n;
                }

                for (int i = 0; i < pos.Length; i++)
                {
                    if (link.Supports[i].Column == null || Math.Abs(share[i]) < 1e-9)
                        continue;
                    sink.Add(new Contribution { Column = link.Supports[i].Column, Source = link.Beam, Type = ContributionType.BeamSelfWeight, Share = share[i], ViaBeam = link.Beam });
                }
            }
        }

        /// <summary>
        /// Fractions of a unit load at <paramref name="x"/> that each support takes, treating every
        /// span as simply supported (lever rule) and an overhang by statics of the adjacent span.
        /// </summary>
        public static double[] Distribute(double[] pos, double x)
        {
            int n = pos.Length;
            var f = new double[n];
            if (n == 0)
                return f;
            if (n == 1 || pos[n - 1] - pos[0] < 1)
            {
                f[0] = 1;
                return f;
            }

            if (x < pos[0])
            {
                double l = pos[1] - pos[0], d = pos[0] - x;
                f[0] = 1 + d / l;
                f[1] = -d / l;
                return f;
            }

            if (x > pos[n - 1])
            {
                double l = pos[n - 1] - pos[n - 2], d = x - pos[n - 1];
                f[n - 1] = 1 + d / l;
                f[n - 2] = -d / l;
                return f;
            }

            for (int k = 0; k < n - 1; k++)
            {
                if (x <= pos[k + 1] || k == n - 2)
                {
                    double l = pos[k + 1] - pos[k];
                    if (l < 1)
                    {
                        f[k] = 1;
                        return f;
                    }

                    f[k] = (pos[k + 1] - x) / l;
                    f[k + 1] = (x - pos[k]) / l;
                    return f;
                }
            }

            return f;
        }

        /// <summary>Moves a contribution on an upper-beam stand down to the columns of the beam under check.</summary>
        private IEnumerable<Contribution> Resolve(Contribution c, Topology topo, int depth)
        {
            if (c.Column.IsMain)
            {
                yield return c;
                yield break;
            }

            var link = topo.UpperBeams.FirstOrDefault(l => l.Beam == c.Column.SupportBeam);
            if (link == null || depth > 8)
                yield break;

            var f = Distribute(link.Supports.Select(x => x.Position).ToArray(), c.Column.Position);
            for (int i = 0; i < f.Length; i++)
            {
                if (link.Supports[i].Column == null || Math.Abs(f[i]) < 1e-9)
                    continue; // taken by a stand outside the selection
                var nc = c.Clone();
                nc.Column = link.Supports[i].Column;
                nc.Share = c.Share * f[i];
                nc.ViaBeam = link.Beam;
                foreach (var r in Resolve(nc, topo, depth + 1))
                    yield return r;
            }
        }

        /// <summary>Drops levels that carry nothing after the transfer and renumbers the rest from the bottom.</summary>
        private static void PruneLevels(Topology topo)
        {
            var used = new HashSet<Level>(topo.Contributions.Where(c => c.Level != null).Select(c => c.Level));
            topo.Levels.RemoveAll(l => !used.Contains(l));
            topo.Levels.Sort((a, b) => a.Z.CompareTo(b.Z));
            for (int i = 0; i < topo.Levels.Count; i++)
                topo.Levels[i].Index = i + 1;
        }

        /// <summary>
        /// Drops geometric duplicates (same kind and axis within ~10 mm), e.g. a part that is
        /// reachable both directly and through an assembly/group object.
        /// </summary>
        private static IEnumerable<ScaffoldElement> Deduplicate(IEnumerable<ScaffoldElement> elements)
        {
            var seen = new HashSet<string>();
            foreach (var e in elements)
            {
                string a = Key(e.Start), b = Key(e.End);
                string key = e.Kind + "|" + (string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a);
                if (seen.Add(key))
                    yield return e;
            }
        }

        private static string Key(Vec3 p) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0},{2:0}", Math.Round(p.X / 10), Math.Round(p.Y / 10), Math.Round(p.Z / 10));

        private void BuildColumns(Topology topo, List<ScaffoldElement> selected, List<ScaffoldElement> standards)
        {
            var beam = topo.Beam;
            double beamTop = beam.ZMax;
            double tol = _s.ColumnXYTolerance;

            var seeds = new List<Vec3>();
            foreach (var s in selected)
            {
                var p = s.Start;
                if (!seeds.Any(q => Vec3.DistanceXY(q, p) <= tol))
                    seeds.Add(p);
            }

            foreach (var seed in seeds)
            {
                var col = new Column();
                foreach (var st in standards)
                {
                    if (Vec3.DistanceXY(st.Start, seed) > tol)
                        continue;
                    // Only what stands above the beam is carried by it.
                    if (st.ZMax <= beamTop + 1)
                        continue;
                    col.Sections.Add(st);
                }

                if (col.Sections.Count == 0)
                {
                    var here = standards.Where(st => Vec3.DistanceXY(st.Start, seed) <= tol).ToList();
                    topo.Warnings.Add(here.Count == 0
                        ? string.Format(CultureInfo.InvariantCulture, "Стойка в точке ({0:0}; {1:0}): секции не найдены среди распознанных элементов.", seed.X, seed.Y)
                        : string.Format(CultureInfo.InvariantCulture,
                            "Стойка в точке ({0:0}; {1:0}) целиком ниже верха балки: Z {2:0}..{3:0} мм, верх балки {4:0} мм — проверьте, какую балку выбрали.",
                            seed.X, seed.Y, here.Min(h => h.ZMin), here.Max(h => h.ZMax), beamTop));
                    continue;
                }

                col.Sections.Sort((a, b) => a.ZMin.CompareTo(b.ZMin));
                double lowest = col.Sections[0].ZMin;
                KeepContinuousStack(col.Sections, beamTop);
                if (col.Sections.Count == 0)
                {
                    topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "Стойка в точке ({0:0}; {1:0}): низ стойки на {2:0} мм выше верха балки (допуск {3:0} мм) — стойка не опирается на балку.",
                        seed.X, seed.Y, lowest - beamTop, _s.ColumnOnBeamZTolerance));
                    continue;
                }
                col.ZBottom = col.Sections.Min(x => x.ZMin);
                col.ZTop = col.Sections.Max(x => x.ZMax);
                double ax = col.Sections.Average(x => x.Start.X);
                double ay = col.Sections.Average(x => x.Start.Y);
                col.Axis = new Vec3(ax, ay, col.ZBottom);

                double t = PlanGeometry.ProjectXY(beam.Start, beam.End, col.Axis, out double offset);
                double len = beam.PlanLength;
                col.Position = t * len;
                col.OffsetFromBeamAxis = offset;
                col.OnBeam = offset <= beam.Width / 2 + _s.BeamPlanTolerance
                             && col.Position >= -_s.BeamPlanTolerance
                             && col.Position <= len + _s.BeamPlanTolerance
                             && col.ZBottom - beamTop <= _s.ColumnOnBeamZTolerance
                             && col.ZBottom - beamTop >= -_s.ColumnOnBeamZTolerance;
                topo.Columns.Add(col);
            }

            topo.Columns.Sort((a, b) => a.Position.CompareTo(b.Position));
            for (int i = 0; i < topo.Columns.Count; i++)
            {
                var c = topo.Columns[i];
                c.Name = "S" + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (!c.OnBeam)
                {
                    topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}: стойка не опирается на балку (смещение от оси {1:0} мм, низ стойки на {2:0} мм выше верха балки) — проверьте выбор.",
                        c.Name, c.OffsetFromBeamAxis, c.ZBottom - beamTop));
                }
            }
        }

        /// <summary>
        /// Keeps only the sections stacked continuously upward from the lowest one standing on the beam.
        /// A gap means the standard above belongs to another structure (it stands on something else).
        /// </summary>
        private void KeepContinuousStack(List<ScaffoldElement> sections, double beamTop)
        {
            var stack = new List<ScaffoldElement>();
            double top = double.NaN;
            foreach (var sec in sections)
            {
                if (stack.Count == 0)
                {
                    if (sec.ZMin - beamTop > _s.ColumnOnBeamZTolerance)
                        break;
                }
                else if (sec.ZMin > top + _s.ColumnGapTolerance)
                {
                    break;
                }

                stack.Add(sec);
                top = double.IsNaN(top) ? sec.ZMax : Math.Max(top, sec.ZMax);
            }

            sections.Clear();
            sections.AddRange(stack);
        }

        private Column FindColumnAt(List<Column> columns, Vec3 p)
        {
            Column best = null;
            double bestDist = double.MaxValue;
            bool bestInside = false;
            foreach (var c in columns)
            {
                double d = Vec3.DistanceXY(c.Axis, p);
                if (d > _s.NodeTolerance)
                    continue;
                if (p.Z < c.ZBottom - _s.NodeTolerance || p.Z > c.ZTop + _s.NodeTolerance)
                    continue;
                // Where two stacks meet at a beam, a node belongs to the one whose Z range really contains it.
                bool inside = p.Z >= c.ZBottom && p.Z <= c.ZTop;
                if (best == null || (inside && !bestInside) || (inside == bestInside && d < bestDist))
                {
                    best = c;
                    bestDist = d;
                    bestInside = inside;
                }
            }

            return best;
        }

        private void AddLinearSelfWeight(Topology topo, List<Column> columns, List<Contribution> sink, List<ScaffoldElement> elements, ContributionType type)
        {
            foreach (var e in elements)
            {
                foreach (var end in new[] { e.Start, e.End })
                {
                    var col = FindColumnAt(columns, end);
                    if (col == null)
                        continue;
                    sink.Add(new Contribution { Column = col, Source = e, Type = type, Share = 0.5 });
                    topo.UsedElementIds.Add(e.Id);
                }
            }
        }

        private void AddDecks(Topology topo, List<Column> columns, List<Contribution> sink, List<ScaffoldElement> decks, List<ScaffoldElement> ledgers)
        {
            var deckContribs = new List<Contribution>();

            foreach (var deck in decks)
            {
                var found = new List<Contribution>();
                bool unresolvedBearing = false;

                foreach (var bearing in new[] { deck.Start, deck.End })
                {
                    var ledger = FindBearingLedger(deck, bearing, ledgers, out double t);
                    if (ledger == null)
                    {
                        unresolvedBearing = true;
                        continue;
                    }

                    t = Math.Max(0, Math.Min(1, t));
                    AddDeckShare(topo, columns, found, deck, ledger, ledger.Start, 0.5 * (1 - t));
                    AddDeckShare(topo, columns, found, deck, ledger, ledger.End, 0.5 * t);
                }

                if (found.Count == 0)
                    continue;

                if (unresolvedBearing)
                {
                    topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "Дек {0}: не найден леджер под одной из опор — учтена только найденная половина нагрузки.", deck.DisplayName));
                }

                deckContribs.AddRange(found);
                topo.UsedElementIds.Add(deck.Id);
            }

            BuildLevels(topo, deckContribs);
            sink.AddRange(deckContribs);
        }

        private void AddDeckShare(Topology topo, List<Column> columns, List<Contribution> target, ScaffoldElement deck, ScaffoldElement ledger, Vec3 node, double share)
        {
            if (share <= 1e-9)
                return;
            var col = FindColumnAt(columns, node);
            if (col == null)
                return;
            target.Add(new Contribution { Column = col, Source = deck, Via = ledger, Type = ContributionType.Deck, Share = share });
            topo.UsedElementIds.Add(ledger.Id);
        }

        /// <summary>Ledger under one bearing edge of a deck; t = position of the bearing along the ledger.</summary>
        private ScaffoldElement FindBearingLedger(ScaffoldElement deck, Vec3 bearing, List<ScaffoldElement> ledgers, out double t)
        {
            ScaffoldElement best = null;
            double bestDist = double.MaxValue;
            t = 0;

            foreach (var l in ledgers)
            {
                double ledgerZ = (l.Start.Z + l.End.Z) / 2;
                double dz = deck.ZMin - ledgerZ;
                if (dz < _s.DeckOverLedgerMin || dz > _s.DeckOverLedgerMax)
                    continue;

                double tt = PlanGeometry.ProjectXY(l.Start, l.End, bearing, out double dist);
                if (dist > _s.DeckBearingTolerance || dist >= bestDist)
                    continue;
                double slack = deck.Width / 2 / Math.Max(1, l.PlanLength);
                if (tt < -slack || tt > 1 + slack)
                    continue;

                best = l;
                bestDist = dist;
                t = tt;
            }

            return best;
        }

        private void BuildLevels(Topology topo, List<Contribution> deckContribs)
        {
            var zs = deckContribs.Select(c => c.Source.ZMin).OrderBy(z => z).ToList();
            foreach (double z in zs)
            {
                if (topo.Levels.Count == 0 || z - topo.Levels[topo.Levels.Count - 1].Z > _s.LevelZTolerance)
                    topo.Levels.Add(new Level { Index = topo.Levels.Count + 1, Z = z });
            }

            foreach (var c in deckContribs)
                c.Level = topo.Levels.OrderBy(l => Math.Abs(l.Z - c.Source.ZMin)).First();
        }
    }
}
