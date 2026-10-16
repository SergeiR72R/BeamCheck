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

        /// <summary>
        /// Elements the user added by hand: a deck listed here is attached to the nearest ledger however
        /// loosely it sits (the user has decided that it belongs to the structure).
        /// </summary>
        public ISet<string> ForcedIds { get; set; } = new HashSet<string>();

        /// <summary>
        /// Decks that lie on the checked member itself (a ledger carries decks directly) load it as point loads at their
        /// bearing, named D1, D2…
        /// </summary>
        public bool DirectDecks { get; set; }
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
        /// <summary>How far a head or foot plate may reach below the underside of a beam without being a support under it, mm.</summary>
        private const double FootAllowance = 200;

        private readonly BeamCheckSettings _s;
        private List<ScaffoldElement> _ledgers = new List<ScaffoldElement>();
        private List<Column> _standRuns = new List<Column>();
        private readonly Dictionary<string, Column> _consoleRoots = new Dictionary<string, Column>();
        private List<ScaffoldElement> _consoleList = new List<ScaffoldElement>();
        private readonly HashSet<string> _pinLedgerIds = new HashSet<string>();
        private readonly HashSet<(BeamLink, Column)> _recordedTransfers = new HashSet<(BeamLink, Column)>();
        private readonly List<Column> _directContacts = new List<Column>();
        private ScaffoldElement _member;

        public ScaffoldAnalyzer(BeamCheckSettings settings)
        {
            _s = settings;
        }

        public Topology Analyze(AnalysisInput input)
        {
            if (input.Beam == null)
                throw new ArgumentException("Beam is required.", nameof(input));

            var topo = new Topology { Beam = input.Beam };
            _upliftWarned.Clear();
            _consoleRoots.Clear();
            _pinLedgerIds.Clear();
            _recordedTransfers.Clear();
            _directContacts.Clear();
            _member = input.DirectDecks ? input.Beam : null;
            bool Active(ScaffoldElement e) => e != null && !input.ExcludedIds.Contains(e.Id);

            // What the user picked as a standard wins over how the same object was classified during the scan.
            var picked = input.SelectedStandards.Where(Active).ToList();
            var pickedIds = new HashSet<string>(picked.Select(e => e.Id));
            var combined = input.Candidates.Where(Active).Where(e => !pickedIds.Contains(e.Id)).ToList();
            combined.AddRange(picked);
            var all = Deduplicate(combined).ToList();

            var standards = all.Where(e => e.Kind == ElementKind.Standard).ToList();
            BuildColumns(topo, input.SelectedStandards.Where(Active).ToList(), standards);
            if (topo.Columns.Count == 0)
            {
                topo.Warnings.Add("Über dem Träger wurde kein Ständer gefunden.");
                return topo;
            }

            // Columns that carry load: those on the beam under check plus, found below, those standing on
            // upper beams that rest on them. Their load reaches the beam through the upper beams.
            var active = new List<Column>(topo.Columns);
            var upper = all.Where(e => e.Kind == ElementKind.Beam && !IsSameBeam(e, input.Beam)).ToList();
            upper.AddRange(FindBeamsPassingThroughColumns(all, topo.Columns, input.Beam, upper));
            // A console is a cantilever with a single support, its root: a beam that takes everything to one column.
            var consoles = all.Where(e => e.Kind == ElementKind.Console).ToList();
            _consoleList = consoles;
            upper.AddRange(consoles);
            // Upper beams and ledgers with pins feed each other: a beam can lie on a stack that stands on a pin, a ledger end can sit on a stand on a beam.
            for (int round = 0; round < 4; round++)
            {
                int before = topo.UpperBeams.Count + active.Count;
                FindUpperBeams(topo, upper, standards, active);
                FindPinLedgers(topo, all, standards, active);
                if (topo.UpperBeams.Count + active.Count == before)
                    break;
            }

            foreach (var link in topo.UpperBeams)
            {
                if (link.Beam.Kind == ElementKind.Console)
                    _consoleRoots[link.Beam.Id] = link.Supports.Select(x => x.Column).FirstOrDefault(c => c != null);
            }

            var sink = new List<Contribution>();
            foreach (var c in active)
            {
                foreach (var sec in c.Sections)
                {
                    sink.Add(new Contribution { Column = c, Source = sec, Type = ContributionType.StandardSelfWeight, Share = 1 });
                    topo.UsedElementIds.Add(sec.Id);
                }
            }

            // A ledger that carries stacks on pins stays a ledger (its decks and weight are counted as for any ledger).
            var beamIds = new HashSet<string>(topo.UpperBeams.Where(l => !l.IsPinLedger).Select(l => l.Beam.Id));
            var ledgers = all.Where(e => e.Kind == ElementKind.Ledger && !beamIds.Contains(e.Id) && !IsSameBeam(e, input.Beam)).ToList();
            _ledgers = ledgers;
            _standRuns = BuildRuns(standards);
            AddLinearSelfWeight(topo, active, sink, ledgers, ContributionType.LedgerSelfWeight);
            AddLinearSelfWeight(topo, active, sink, all.Where(e => (e.Kind == ElementKind.Diagonal || e.Kind == ElementKind.Accessory) && !beamIds.Contains(e.Id)).ToList(), ContributionType.OtherSelfWeight);
            AddDecks(topo, active, sink, all.Where(e => e.Kind == ElementKind.Deck).ToList(), ledgers.Concat(consoles).ToList(), input.ForcedIds);
            AddUpperBeamWeights(topo, sink);

            topo.Warnings.Add(DescribeNeighbourhood(all, active));

            topo.Collected.AddRange(sink);

            // Pass everything standing on upper beams down to the columns of the beam under check.
            foreach (var c in sink)
                topo.Contributions.AddRange(Resolve(c, topo, 0));
            PruneLevels(topo);
            AddDirectContacts(topo);
            return topo;
        }

        /// <summary>Points where decks lie on the checked member itself, after the columns, in order along it (D1, D2…).</summary>
        private void AddDirectContacts(Topology topo)
        {
            var used = new HashSet<Column>(topo.Contributions.Select(c => c.Column));
            var contacts = _directContacts.Where(used.Contains).OrderBy(c => c.Position).ToList();
            for (int i = 0; i < contacts.Count; i++)
                contacts[i].Name = "D" + (i + 1).ToString(CultureInfo.InvariantCulture);
            topo.Columns.AddRange(contacts);
            if (contacts.Count > 0)
                topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "ℹ Beläge, die direkt auf dem geprüften Element liegen: {0} Auflager (Punkte D1…D{0}), je mit der Hälfte von Gewicht und Nutzlast des Belags.", contacts.Count));
        }

        /// <summary>What was recognised around the columns (≤ 3 m): kinds with a few part names, to spot misrecognition.</summary>
        private string DescribeNeighbourhood(List<ScaffoldElement> all, List<Column> columns)
        {
            var near = all.Where(e => columns.Any(c =>
                Vec3.DistanceXY(c.Axis, (e.Start + e.End) / 2) <= 3000 && e.ZMax >= c.ZBottom - 300 && e.ZMin <= c.ZTop + 300)).ToList();
            var groups = near.GroupBy(e => e.Kind).OrderBy(g => g.Key.ToString())
                .Select(g => g.Key + "×" + g.Count() + " (" + string.Join(", ", g.Select(x => x.DisplayName).Distinct().Take(3)) + ")");
            return "ℹ Rund um die Ständer (≤3 m) erkannt: " + (near.Count == 0 ? "nichts" : string.Join("; ", groups)) + ".";
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

        /// <summary>
        /// The stacks a beam rests on (under it, or around which it is set in a head): the stack and the position along the beam
        /// (from its start), left to right, coincident ones merged. The same rule that carries the load of upper beams.
        /// </summary>
        public List<BeamSupport> FindSupports(ScaffoldElement beam, IEnumerable<ScaffoldElement> standards, IEnumerable<ScaffoldElement> others = null)
        {
            var otherList = others == null ? new List<ScaffoldElement>() : others.ToList();
            var link = new BeamLink { Beam = beam };
            foreach (var run in BuildRuns(standards.Where(e => e.Kind == ElementKind.Standard).ToList()))
            {
                if (!(run.ZBottom < beam.ZMin - FootAllowance && beam.ZMin <= run.ZTop + _s.UpperBeamBearingTolerance))
                    continue;
                if (!OnBeamFootprint(beam, run.Axis, out double pos))
                    continue;
                double t = PlanGeometry.ProjectXY(beam.Start, beam.End, run.Axis, out double offset);
                // Under the beam, not beside it: a stand next to the beam (with a console, say) does not carry it.
                if (offset > beam.Width / 2 + _s.SupportPlanTolerance)
                    continue;
                // A stack that hangs from the beam (its lower end is free) is not a support.
                if (!StandsOnSomething(run, otherList))
                    continue;
                link.Supports.Add(new BeamSupport { Column = run, Position = pos, Offset = offset });
            }

            link.Supports.Sort((a, b) => a.Position.CompareTo(b.Position));
            MergeCoincidentSupports(link);
            return link.Supports;
        }

        /// <summary>
        /// Whether the lower end of a stack stands on something: its lowest parts, or parts at its foot, include a spindle, jack, swivel
        /// nut, base plate or pin. Stacks whose parts carry no article or name at all are not judged (true).
        /// </summary>
        private bool StandsOnSomething(Column run, List<ScaffoldElement> others)
        {
            if (string.IsNullOrEmpty(_s.SupportEndPartPattern))
                return true;
            var rx = new System.Text.RegularExpressions.Regex(_s.SupportEndPartPattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var foot = new List<ScaffoldElement>();
            foreach (var sec in run.Sections)
                if (sec.ZMin <= run.ZBottom + 5)
                    foot.Add(sec);
            foreach (var o in others)
            {
                if (o.Kind == ElementKind.Standard || o.Kind == ElementKind.Deck || o.Kind == ElementKind.Beam)
                    continue;
                var mid = (o.Start + o.End) / 2;
                if (Vec3.DistanceXY(mid, run.Axis) <= 60 && o.ZMin >= run.ZBottom - 150 && o.ZMin <= run.ZBottom + 300)
                    foot.Add(o);
            }

            bool anyKnown = false;
            foreach (var e in foot)
            {
                string text = (e.Article ?? "") + " " + (e.Description ?? "");
                if (text.Trim().Length == 0)
                    continue;
                anyKnown = true;
                if (rx.IsMatch(text))
                    return true;
            }

            return !anyKnown;
        }

        private bool OnBeamFootprint(ScaffoldElement beam, Vec3 p, out double position)
        {
            double t = PlanGeometry.ProjectXY(beam.Start, beam.End, p, out double offset);
            double len = beam.PlanLength;
            position = t * len;
            return offset <= beam.Width / 2 + _s.BeamPlanTolerance && position >= -_s.BeamPlanTolerance && position <= len + _s.BeamPlanTolerance;
        }

        /// <summary>A ledger- or box-sized profile (e.g. an SRU section the name rules do not know) is a beam, not a ledger,
        /// when a selected column passes through it away from its ends: ledgers only meet standards at their ends.</summary>
        private IEnumerable<ScaffoldElement> FindBeamsPassingThroughColumns(List<ScaffoldElement> all, List<Column> columns, ScaffoldElement mainBeam, List<ScaffoldElement> known)
        {
            const double endClearance = 200;
            foreach (var e in all)
            {
                // A box-shaped beam (wider than a ledger) is read as an accessory, so both kinds are candidates.
                // Only parts whose article is unknown: a ledger or accessory that PERI names (UH 175 +, UBK…) is what its name says.
                if ((e.Kind != ElementKind.Ledger && e.Kind != ElementKind.Accessory) || !string.IsNullOrEmpty(e.Article) || IsSameBeam(e, mainBeam) || known.Any(k => k.Id == e.Id))
                    continue;
                double len = e.PlanLength;
                if (len < 800 || e.Width > 400 || e.ZMax - e.ZMin > 600)
                    continue;
                foreach (var c in columns)
                {
                    double t = PlanGeometry.ProjectXY(e.Start, e.End, c.Axis, out double off);
                    double pos = t * len;
                    if (off > Math.Max(e.Width / 2, 50) + _s.BeamPlanTolerance || pos < endClearance || pos > len - endClearance)
                        continue;
                    if (c.ZBottom < e.ZMin - endClearance && e.ZMin <= c.ZTop + _s.UpperBeamBearingTolerance)
                    {
                        yield return e;
                        break;
                    }
                }
            }
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
            var done = new HashSet<string>(topo.UpperBeams.Select(l => l.Beam.Id));
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
                        // A stack that starts at the beam (its head or a post on it) stands on the beam, it does not carry it.
                        if (!(run.ZBottom < u.ZMin - FootAllowance && u.ZMin <= run.ZTop + _s.UpperBeamBearingTolerance))
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
                    MergeCoincidentSupports(link);

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
                        u.Kind == ElementKind.Console
                            ? "ℹ Konsole {0} ist an einem Ständer befestigt, darauf stehen {3} Ständer — ihre gesamte Last geht in diesen Ständer."
                            : "ℹ Träger {0} liegt auf {1} Ständern ({2} davon gewählt), darauf stehen {3} Ständer — deren Last wird auf die gewählten Ständer übertragen.",
                        u.DisplayName, link.Supports.Count, link.Supports.Count(x => x.Column != null), link.Loaded.Count));
                    if (link.Loaded.Count == 0 && u.Kind != ElementKind.Console)
                        topo.Warnings.Add("ℹ Auf dem Träger " + u.DisplayName + " wurden keine Ständer gefunden, nur sein Eigengewicht wird berücksichtigt.");
                    if (u.WeightKg <= 0)
                        topo.Warnings.Add("Gewicht " + (u.Kind == ElementKind.Console ? "der Konsole " : "des Trägers ") + u.DisplayName + " unbekannt (0 kg) — Artikel in BeamCheck.weights.csv ergänzen.");
                }

                if (!progress)
                    break;
            }
        }

        /// <summary>
        /// Several runs of one stack (head, extension, spindle…) all reach the beam: they are one support. Two supports a few
        /// millimetres apart would make the lever rule divide by almost zero.
        /// </summary>
        private static void MergeCoincidentSupports(BeamLink link)
        {
            const double sameSupport = 100;
            var merged = new List<BeamSupport>();
            foreach (var sup in link.Supports)
            {
                var last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (last != null && sup.Position - last.Position < sameSupport)
                {
                    if (last.Column == null)
                        last.Column = sup.Column;
                }
                else
                {
                    merged.Add(sup);
                }
            }

            link.Supports.Clear();
            link.Supports.AddRange(merged);
        }

        private void AddUpperBeamWeights(Topology topo, List<Contribution> sink)
        {
            const int n = 40;
            foreach (var link in topo.UpperBeams)
            {
                if (link.IsPinLedger)
                    continue; // a ledger: its weight is counted with the ledgers
                var pos = link.Supports.Select(x => x.Position).ToArray();
                double len = link.Beam.PlanLength;
                var share = new double[pos.Length];
                for (int j = 0; j < n; j++)
                {
                    var f = Shares(pos, (j + 0.5) / n * len, link.Beam, topo);
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
        /// A stack standing on a pin (UH Zapfen, UHA Zapfen) that sits in the middle of a ledger puts its whole load on that ledger, at the pin;
        /// the ledger gives it to its two ends by the lever rule, and the ends go on as usual (standards of the checked member, stands on upper beams).
        /// The ledger is handled like an upper beam with two supports, its ends.
        /// </summary>
        private void FindPinLedgers(Topology topo, List<ScaffoldElement> all, List<ScaffoldElement> standards, List<Column> active)
        {
            if (string.IsNullOrEmpty(_s.PinPattern))
                return;
            var rx = new System.Text.RegularExpressions.Regex(_s.PinPattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var pins = all.Where(e => e.Kind == ElementKind.Accessory && rx.IsMatch((e.Article ?? "") + " " + (e.Description ?? ""))).ToList();
            if (pins.Count == 0)
                return;

            var runs = BuildRuns(standards);
            var taken = new HashSet<string>(topo.UpperBeams.Select(l => l.Beam.Id));
            var ledgers = all.Where(e => e.Kind == ElementKind.Ledger && e.PlanLength > 1 && !taken.Contains(e.Id) && e.Id != topo.Beam.Id).ToList();
            double tol = _s.PinPlanTolerance;

            for (int pass = 0; pass < 3; pass++)
            {
                bool progress = false;
                foreach (var l in ledgers)
                {
                    if (_pinLedgerIds.Contains(l.Id))
                        continue;
                    var supports = new List<BeamSupport>
                    {
                        new BeamSupport { Column = FindColumnAt(active, l.Start), Position = 0 },
                        new BeamSupport { Column = FindColumnAt(active, l.End), Position = l.PlanLength },
                    };
                    if (supports.All(x => x.Column == null))
                        continue;

                    var link = new BeamLink { Beam = l, IsPinLedger = true };
                    foreach (var pin in pins)
                    {
                        var mid = (pin.Start + pin.End) / 2;
                        double t = PlanGeometry.ProjectXY(l.Start, l.End, mid, out double off);
                        double along = t * l.PlanLength;
                        if (off > Math.Max(l.Width / 2, 30) + tol || along < _s.LedgerOnLedgerEndClearance || along > l.PlanLength - _s.LedgerOnLedgerEndClearance)
                            continue;
                        if (pin.ZMax < l.ZMin - 50 || pin.ZMin > l.ZMax + 150)
                            continue;
                        foreach (var run in runs)
                        {
                            if (Vec3.DistanceXY(run.Axis, mid) > tol || run.ZBottom < pin.ZMin - 50 || run.ZBottom > pin.ZMax + 100)
                                continue;
                            if (active.Any(c => SameStack(c, run, _s.ColumnXYTolerance)) || link.Loaded.Any(c => SameStack(c, run, _s.ColumnXYTolerance)))
                                continue;
                            run.SupportBeam = l;
                            run.Position = along;
                            run.OnBeam = true;
                            run.Name = string.Format(CultureInfo.InvariantCulture, "{0}/{1:0}", l.DisplayName, along);
                            link.Loaded.Add(run);
                            link.PinOf[run] = pin;
                            topo.UsedElementIds.Add(pin.Id);
                        }
                    }

                    if (link.Loaded.Count == 0)
                        continue;
                    link.Supports.AddRange(supports);
                    foreach (var run in link.Loaded)
                        active.Add(run);
                    _pinLedgerIds.Add(l.Id);
                    topo.UpperBeams.Add(link);
                    topo.UsedElementIds.Add(l.Id);
                    progress = true;
                    topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "ℹ Riegel {0}: {1} Ständer stehen auf Zapfen — ihre gesamte Last geht auf den Riegel und nach der Lage des Zapfens zwischen den Auflagern ({2}) auf dessen Enden.",
                        l.DisplayName, link.Loaded.Count, string.Join(" / ", link.Loaded.Select(c => c.Position.ToString("0", CultureInfo.InvariantCulture) + " mm"))));
                }

                if (!progress)
                    break;
            }
        }

        /// <summary>
        /// <see cref="Distribute"/> for the analysis: a load on an overhang relieves the far support (negative share). Unless
        /// <see cref="BeamCheckSettings.UpperBeamAllowUplift"/> is set that relief is not credited (on the safe side).
        /// </summary>
        private double[] Shares(double[] pos, double x, ScaffoldElement beam, Topology topo)
        {
            var f = Distribute(pos, x);
            if (_s.UpperBeamAllowUplift)
                return f;
            bool clamped = false;
            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] < 0)
                {
                    f[i] = 0;
                    clamped = true;
                }
            }

            if (clamped && _upliftWarned.Add(beam.Id))
                topo.Warnings.Add("ℹ Träger " + beam.DisplayName + ": Last auf dem Kragarm entlastet das weiter entfernte Auflager — die Entlastung wird nicht angesetzt (auf der sicheren Seite). Einschalten in den Einstellungen: UpperBeamAllowUplift.");
            return f;
        }

        private readonly HashSet<string> _upliftWarned = new HashSet<string>();

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

            var f = Shares(link.Supports.Select(x => x.Position).ToArray(), c.Column.Position, link.Beam, topo);
            if (_recordedTransfers.Add((link, c.Column)))
                topo.Transfers.Add(new TransferInfo { Link = link, Stand = c.Column, Fractions = (double[])f.Clone() });
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
                        ? string.Format(CultureInfo.InvariantCulture, "Ständer im Punkt ({0:0}; {1:0}): keine Abschnitte unter den erkannten Elementen gefunden.", seed.X, seed.Y)
                        : string.Format(CultureInfo.InvariantCulture,
                            "Ständer im Punkt ({0:0}; {1:0}) liegt ganz unter der Trägeroberkante: Z {2:0}..{3:0} mm, Trägeroberkante {4:0} mm — prüfen, welcher Träger gewählt wurde.",
                            seed.X, seed.Y, here.Min(h => h.ZMin), here.Max(h => h.ZMax), beamTop));
                    continue;
                }

                col.Sections.Sort((a, b) => a.ZMin.CompareTo(b.ZMin));
                double lowest = col.Sections[0].ZMin;
                KeepContinuousStack(col.Sections, beamTop);
                if (col.Sections.Count == 0)
                {
                    topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "Ständer im Punkt ({0:0}; {1:0}): Ständerfuß {2:0} mm über der Trägeroberkante (Toleranz {3:0} mm) — der Ständer steht nicht auf dem Träger.",
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
                        "{0}: Ständer steht nicht auf dem Träger (Abstand von der Achse {1:0} mm, Ständerfuß {2:0} mm über der Trägeroberkante) — Auswahl prüfen.",
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
                    foreach (var r in ResolveNode(topo, columns, e, end, 0.5, 0))
                    {
                        sink.Add(new Contribution { Column = r.Column, Source = e, Type = type, Share = r.Share, ViaBeam = r.ViaBeam });
                        topo.UsedElementIds.Add(e.Id);
                    }
                }
            }
        }

        private struct NodeShare
        {
            public Column Column;
            public double Share;

            /// <summary>Set when the load first goes through a console (a cantilever fixed to <see cref="Column"/>).</summary>
            public ScaffoldElement ViaBeam;
        }

        /// <summary>
        /// Where the load at a ledger end goes. A ledger end normally sits on a standard. A crossbar ledger can
        /// also hang on another ledger through a UHA connector (node in the middle of the supporting ledger):
        /// then its load goes to that ledger by the lever rule and on to the standards at ITS ends.
        /// </summary>
        private List<NodeShare> ResolveNode(Topology topo, List<Column> columns, ScaffoldElement from, Vec3 node, double share, int depth)
        {
            var result = new List<NodeShare>();
            var col = FindColumnAt(columns, node);
            if (col != null)
            {
                result.Add(new NodeShare { Column = col, Share = share });
                return result;
            }

            // A ledger fixed to a console, at its tip or on either side of it: the console is a cantilever, so the
            // end load goes down the standard the console is fixed to.
            var console = FindConsoleAt(node, out var root);
            if (console != null)
            {
                result.Add(new NodeShare { Column = root, Share = share, ViaBeam = console });
                return result;
            }

            // An end that stands on ANY standard is a stand node, never a connector on a passing ledger.
            if (depth >= _s.LedgerOnLedgerDepth || !_s.LedgerOnLedgerEnabled || IsOnAnyStandard(node))
                return result;
            var support = FindSupportingLedger(columns, from, node, out double t);
            if (support == null)
                return result;

            result.AddRange(ResolveNode(topo, columns, support, support.Start, share * (1 - t), depth + 1));
            result.AddRange(ResolveNode(topo, columns, support, support.End, share * t, depth + 1));
            // The supporting ledger counts only if the load really reaches the selected standards through it.
            if (result.Count > 0)
                topo.UsedElementIds.Add(support.Id);
            return result;
        }

        /// <summary>The console (with a root standard in this check) that a ledger end at <paramref name="node"/> is fixed to.</summary>
        private ScaffoldElement FindConsoleAt(Vec3 node, out Column root)
        {
            root = null;
            ScaffoldElement best = null;
            double bestDist = double.MaxValue;
            foreach (var kv in _consoleList)
            {
                var c = kv;
                if (!_consoleRoots.TryGetValue(c.Id, out var r) || r == null)
                    continue;
                if (node.Z < c.ZMin - _s.ConsoleLedgerTolerance || node.Z > c.ZMax + _s.ConsoleLedgerTolerance)
                    continue;
                double along = PlanGeometry.ProjectXY(c.Start, c.End, node, out double d) * c.PlanLength;
                if (along < -_s.ConsoleLedgerTolerance || along > c.PlanLength + _s.ConsoleLedgerTolerance)
                    continue; // beyond the console's ends: the projection onto its line is not on the part
                if (d > _s.ConsoleLedgerTolerance || d >= bestDist)
                    continue;
                best = c;
                bestDist = d;
                root = r;
            }

            return best;
        }

        private bool IsOnAnyStandard(Vec3 p)
        {
            foreach (var run in _standRuns)
            {
                if (Vec3.DistanceXY(run.Axis, p) <= _s.NodeTolerance &&
                    p.Z >= run.ZBottom - _s.NodeTolerance && p.Z <= run.ZTop + _s.NodeTolerance)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The ledger on which the end of <paramref name="from"/> rests: crossing it (not running along it), at about
        /// the same height, the end within the node tolerance of its axis, clear of its own ends, and the supporting ledger itself
        /// attached to a selected standard (the chain never wanders off through the scaffold).
        /// </summary>
        private ScaffoldElement FindSupportingLedger(List<Column> columns, ScaffoldElement from, Vec3 node, out double t)
        {
            ScaffoldElement best = null;
            double bestDist = double.MaxValue;
            t = 0;
            var fd = (from.End - from.Start).PlanDirection();
            foreach (var l in _ledgers)
            {
                if (ReferenceEquals(l, from) || l.Id == from.Id || l.PlanLength < 1)
                    continue;
                var ld = (l.End - l.Start).PlanDirection();
                if (Math.Abs(ld.DotXY(fd)) > 0.5)
                    continue;
                if (Math.Abs(from.ZMax - l.ZMax) > _s.LedgerOnLedgerHeight)
                    continue;
                // Only a ledger attached to one of the selected standards can carry a hanging ledger for them.
                if (FindColumnAt(columns, l.Start) == null && FindColumnAt(columns, l.End) == null)
                    continue;
                double tt = PlanGeometry.ProjectXY(l.Start, l.End, node, out double dist);
                if (dist > _s.NodeTolerance || dist >= bestDist)
                    continue;
                double along = tt * l.PlanLength;
                if (along < _s.LedgerOnLedgerEndClearance || along > l.PlanLength - _s.LedgerOnLedgerEndClearance)
                    continue;
                best = l;
                bestDist = dist;
                t = tt;
            }

            return best;
        }

        private void AddDecks(Topology topo, List<Column> columns, List<Contribution> sink, List<ScaffoldElement> decks, List<ScaffoldElement> ledgers, ISet<string> forced)
        {
            var deckContribs = new List<Contribution>();
            if (_member != null)
                ledgers = ledgers.Concat(new[] { _member }).ToList();
            int nearMissing = 0, halfSupported = 0, snapped = 0;
            double maxSnap = 0;
            var samples = new List<string>();
            double seat = CalibrateDeckSeat(decks, ledgers);
            if (decks.Count > 0)
                topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "ℹ Belagsitz: Oberkante Belag über Oberkante Riegel um {0:0} mm (Toleranz ±{1:0} mm in der Höhe, ≤{2:0} mm im Grundriss).",
                    seat, _s.DeckHeightTolerance, _s.DeckBearingTolerance));

            foreach (var deck in decks)
            {
                var found = new List<Contribution>();
                int resolved = 0;

                bool isForced = forced != null && forced.Contains(deck.Id);
                foreach (var bearing in new[] { deck.Start, deck.End })
                {
                    var ledger = FindBearingLedger(deck, bearing, ledgers, out double t, seat);
                    if (ledger == null && (isForced || _s.DeckSnapEnabled))
                    {
                        // Not seated on a ledger (modelled loosely): carried by the nearest one instead.
                        double plan = isForced ? Math.Max(_s.DeckSnapDistance, 1500) : _s.DeckSnapDistance;
                        double height = isForced ? Math.Max(_s.DeckSnapHeight, 1000) : _s.DeckSnapHeight;
                        ledger = FindBearingLedger(deck, bearing, ledgers, out t, seat, plan, height);
                        if (ledger != null)
                        {
                            snapped++;
                            PlanGeometry.ProjectXY(ledger.Start, ledger.End, bearing, out double gap);
                            maxSnap = Math.Max(maxSnap, gap);
                        }
                    }

                    if (ledger == null)
                        continue;

                    resolved++;
                    t = Math.Max(0, Math.Min(1, t));
                    if (ReferenceEquals(ledger, _member))
                    {
                        AddDirectShare(topo, found, deck, ledger, t, 0.5);
                        continue;
                    }

                    if (ledger.Kind == ElementKind.Console)
                    {
                        AddConsoleShare(topo, found, deck, ledger, 0.5);
                        continue;
                    }

                    AddDeckShare(topo, columns, found, deck, ledger, ledger.Start, 0.5 * (1 - t));
                    AddDeckShare(topo, columns, found, deck, ledger, ledger.End, 0.5 * t);
                }

                if (resolved < 2 && IsNearColumns(deck, columns))
                {
                    if (resolved == 0)
                        nearMissing++;
                    else
                        halfSupported++;
                    if (samples.Count < 4)
                        samples.Add(DescribeMiss(deck, ledgers, seat));
                }

                if (found.Count == 0)
                    continue;

                deckContribs.AddRange(found);
                topo.UsedElementIds.Add(deck.Id);
            }

            if (snapped > 0)
                topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "ℹ {0} Belagauflager liegen nicht auf einem Riegel (Toleranz {1:0} mm), sie wurden dem nächsten Riegel zugeordnet — Abweichung im Grundriss bis {2:0} mm.",
                    snapped, _s.DeckBearingTolerance, maxSnap));
            if (nearMissing + halfSupported > 0)
            {
                topo.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Beläge neben den Ständern ohne Riegel unter dem Auflager: {0} ohne Auflager, {1} mit einem Auflager — ihre Last wird nur teilweise oder gar nicht angesetzt. Beispiele (bis zum nächsten Riegel: im Grundriss / in der Höhe gegenüber dem normalen Sitz): {2}.",
                    nearMissing, halfSupported, string.Join("; ", samples)));
            }

            BuildLevels(topo, deckContribs);
            sink.AddRange(deckContribs);
        }

        private bool IsNearColumns(ScaffoldElement deck, List<Column> columns) =>
            columns.Any(c => Vec3.DistanceXY(c.Axis, (deck.Start + deck.End) / 2) <= _s.NodeTolerance + deck.PlanLength + 500
                             && deck.ZMin >= c.ZBottom - 300 && deck.ZMin <= c.ZTop + 300);

        /// <summary>For a deck without a ledger: how far away the nearest ledger is (plan distance to its axis, height difference).</summary>
        private string DescribeMiss(ScaffoldElement deck, List<ScaffoldElement> ledgers, double seat)
        {
            var parts = new List<string>();
            foreach (var end in new[] { deck.Start, deck.End })
            {
                ScaffoldElement best = null;
                double bestPlan = double.MaxValue;
                foreach (var l in ledgers)
                {
                    if (Math.Abs(deck.ZMax - l.ZMax) > 3000)
                        continue;
                    PlanGeometry.ProjectXY(l.Start, l.End, end, out double d);
                    if (d < bestPlan)
                    {
                        bestPlan = d;
                        best = l;
                    }
                }

                parts.Add(best == null
                    ? "kein Riegel"
                    : string.Format(CultureInfo.InvariantCulture, "{0:0} / {1:+0;-0;0} mm", bestPlan, deck.ZMax - best.ZMax - seat));
            }

            return deck.DisplayName + " Z=" + deck.ZMin.ToString("0", CultureInfo.InvariantCulture) + " [" + string.Join(" | ", parts) + "]";
        }

        /// <summary>A deck lying on the checked member: its half load acts at the bearing, on a point of its own.</summary>
        private void AddDirectShare(Topology topo, List<Contribution> target, ScaffoldElement deck, ScaffoldElement member, double t, double share)
        {
            var axis = member.Start + (member.End - member.Start) * t;
            var contact = new Column
            {
                Axis = axis.WithZ(deck.ZMax),
                ZBottom = deck.ZMax,
                ZTop = deck.ZMax,
                Position = t * member.PlanLength,
                OnBeam = true,
            };
            _directContacts.Add(contact);
            target.Add(new Contribution { Column = contact, Source = deck, Via = member, Type = ContributionType.Deck, Share = share });
        }

        /// <summary>A deck lying on a console: the console is a cantilever, so all of it goes down the standard it is fixed to.</summary>
        private void AddConsoleShare(Topology topo, List<Contribution> target, ScaffoldElement deck, ScaffoldElement console, double share)
        {
            if (!_consoleRoots.TryGetValue(console.Id, out var root) || root == null)
                return; // fixed to a standard that is not part of this check
            target.Add(new Contribution { Column = root, Source = deck, Via = console, ViaBeam = console, Type = ContributionType.Deck, Share = share });
            topo.UsedElementIds.Add(console.Id);
        }

        private void AddDeckShare(Topology topo, List<Column> columns, List<Contribution> target, ScaffoldElement deck, ScaffoldElement ledger, Vec3 node, double share)
        {
            if (share <= 1e-9)
                return;
            foreach (var r in ResolveNode(topo, columns, ledger, node, share, 0))
            {
                target.Add(new Contribution { Column = r.Column, Source = deck, Via = ledger, Type = ContributionType.Deck, Share = r.Share, ViaBeam = r.ViaBeam });
                topo.UsedElementIds.Add(ledger.Id);
                if (r.ViaBeam != null)
                    topo.UsedElementIds.Add(r.ViaBeam.Id);
            }
        }

        /// <summary>
        /// Ledger under one bearing edge of a deck; t = position of the bearing along the ledger.
        /// Plan distance to the ledger axis is limited by <see cref="BeamCheckSettings.DeckBearingTolerance"/>.
        /// Height: with <paramref name="nominalDz"/> the deck must sit within ±DeckHeightTolerance of it;
        /// without it (calibration) any height inside the DeckOverLedgerMin..Max window is taken.
        /// </summary>
        private ScaffoldElement FindBearingLedger(ScaffoldElement deck, Vec3 bearing, List<ScaffoldElement> ledgers, out double t, double? nominalDz = null, double? planTol = null, double? heightTol = null)
        {
            ScaffoldElement best = null;
            double bestDist = double.MaxValue;
            t = 0;

            foreach (var l in ledgers)
            {
                // Top of the deck against top of the ledger: the same on every ledger type and whether the part
                // was read from its axis lines or from its bounding box.
                double dz = deck.ZMax - l.ZMax;
                bool heightOk = nominalDz.HasValue
                    ? Math.Abs(dz - nominalDz.Value) <= (heightTol ?? _s.DeckHeightTolerance)
                    : dz >= _s.DeckOverLedgerMin && dz <= _s.DeckOverLedgerMax;
                if (!heightOk)
                    continue;

                // A deck rests on a crossbar: the ledger runs across the deck, never along it.
                var ld = (l.End - l.Start).PlanDirection();
                var dd = (deck.End - deck.Start).PlanDirection();
                if (ld.LengthXY > 0 && dd.LengthXY > 0 && Math.Abs(ld.DotXY(dd)) > 0.5)
                    continue;

                double tt = PlanGeometry.ProjectXY(l.Start, l.End, bearing, out double dist);
                if (dist > (planTol ?? _s.DeckBearingTolerance) || dist >= bestDist)
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

        /// <summary>
        /// How far the top of a deck sits above the TOP of the ledger it lies on, in this model (median over decks
        /// that lie on a ledger within the plan tolerance and inside the broad height window). The reference is
        /// the ledger's top face, so a taller (reinforced) ledger carries its decks higher and still matches.
        /// </summary>
        private double CalibrateDeckSeat(List<ScaffoldElement> decks, List<ScaffoldElement> ledgers)
        {
            var dzs = new List<double>();
            foreach (var deck in decks)
            {
                foreach (var bearing in new[] { deck.Start, deck.End })
                {
                    var l = FindBearingLedger(deck, bearing, ledgers, out _);
                    if (l != null)
                        dzs.Add(deck.ZMax - l.ZMax);
                }
            }

            if (dzs.Count == 0)
                return (_s.DeckOverLedgerMin + _s.DeckOverLedgerMax) / 2;
            dzs.Sort();
            return dzs[dzs.Count / 2];
        }

        private void BuildLevels(Topology topo, List<Contribution> deckContribs)
        {
            var zs = deckContribs.Select(c => c.Source.ZMin).OrderBy(z => z).ToList();
            foreach (double z in zs)
            {
                if (topo.Levels.Count == 0 || z - topo.Levels[topo.Levels.Count - 1].Z > _s.LevelZTolerance)
                    topo.Levels.Add(new Level { Index = topo.Levels.Count + 1, Z = z, Height = z - topo.Beam.ZMax });
            }

            foreach (var c in deckContribs)
                c.Level = topo.Levels.OrderBy(l => Math.Abs(l.Z - c.Source.ZMin)).First();
        }
    }
}
