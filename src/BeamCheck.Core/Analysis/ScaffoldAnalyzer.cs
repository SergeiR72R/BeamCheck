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

            var all = Deduplicate(input.Candidates.Where(Active)).ToList();
            var ids = new HashSet<string>(all.Select(e => e.Id));
            foreach (var s in input.SelectedStandards.Where(Active))
                if (ids.Add(s.Id))
                    all.Add(s);

            BuildColumns(topo, input.SelectedStandards.Where(Active).ToList(), all.Where(e => e.Kind == ElementKind.Standard).ToList());
            if (topo.Columns.Count == 0)
            {
                topo.Warnings.Add("Не найдено ни одной стойки над балкой.");
                return topo;
            }

            foreach (var c in topo.Columns)
            {
                foreach (var sec in c.Sections)
                {
                    topo.Contributions.Add(new Contribution { Column = c, Source = sec, Type = ContributionType.StandardSelfWeight, Share = 1 });
                    topo.UsedElementIds.Add(sec.Id);
                }
            }

            var ledgers = all.Where(e => e.Kind == ElementKind.Ledger).ToList();
            AddLinearSelfWeight(topo, ledgers, ContributionType.LedgerSelfWeight);
            AddLinearSelfWeight(topo, all.Where(e => e.Kind == ElementKind.Diagonal || e.Kind == ElementKind.Accessory).ToList(), ContributionType.OtherSelfWeight);
            AddDecks(topo, all.Where(e => e.Kind == ElementKind.Deck).ToList(), ledgers);

            return topo;
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
                    continue;

                col.Sections.Sort((a, b) => a.ZMin.CompareTo(b.ZMin));
                KeepContinuousStack(col.Sections, beamTop);
                if (col.Sections.Count == 0)
                    continue;
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

        private Column FindColumnAt(Topology topo, Vec3 p)
        {
            Column best = null;
            double bestDist = double.MaxValue;
            foreach (var c in topo.Columns)
            {
                double d = Vec3.DistanceXY(c.Axis, p);
                if (d > _s.NodeTolerance || d >= bestDist)
                    continue;
                if (p.Z < c.ZBottom - _s.NodeTolerance || p.Z > c.ZTop + _s.NodeTolerance)
                    continue;
                best = c;
                bestDist = d;
            }

            return best;
        }

        private void AddLinearSelfWeight(Topology topo, List<ScaffoldElement> elements, ContributionType type)
        {
            foreach (var e in elements)
            {
                foreach (var end in new[] { e.Start, e.End })
                {
                    var col = FindColumnAt(topo, end);
                    if (col == null)
                        continue;
                    topo.Contributions.Add(new Contribution { Column = col, Source = e, Type = type, Share = 0.5 });
                    topo.UsedElementIds.Add(e.Id);
                }
            }
        }

        private void AddDecks(Topology topo, List<ScaffoldElement> decks, List<ScaffoldElement> ledgers)
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
                    AddDeckShare(topo, found, deck, ledger, ledger.Start, 0.5 * (1 - t));
                    AddDeckShare(topo, found, deck, ledger, ledger.End, 0.5 * t);
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
            topo.Contributions.AddRange(deckContribs);
        }

        private void AddDeckShare(Topology topo, List<Contribution> target, ScaffoldElement deck, ScaffoldElement ledger, Vec3 node, double share)
        {
            if (share <= 1e-9)
                return;
            var col = FindColumnAt(topo, node);
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
