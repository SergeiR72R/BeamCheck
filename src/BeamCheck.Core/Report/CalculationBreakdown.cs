using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Settings;
using BeamCheck.Core.Model;

namespace BeamCheck.Core.Report
{
    /// <summary>
    /// The calculation path in plain text: what makes up Gk and Qk of the standards on the checked member, by kind of part,
    /// by deck level and by the beam that passes the load down. Every number is a sum over the contributions of the analysis,
    /// so the figures add up to the table in the dialog.
    /// </summary>
    public static class CalculationBreakdown
    {
        private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

        private static string Kn(double v) => v.ToString("0.00", De);

        private static string M2(double v) => v.ToString("0.000", De);

        public static List<string> Build(BeamLoadSession session)
        {
            var topo = session.Topology;
            var s = session.Settings;
            var o = session.Options;
            double q = s.GetServiceLoad(o.LoadClass);
            var lines = new List<string>();
            var contribs = topo.Contributions.Where(c => c.Column.IsMain).ToList();

            double Gk(IEnumerable<Contribution> cs) => LoadCalculator.KgToKn(cs.Sum(c => c.WeightKg));
            double Factor(Level l) => l != null && o.LevelFactors.TryGetValue(l.Index, out double f) ? f : 0;

            lines.Add("Rechenweg " + session.Result.BeamName + " — Träger " + topo.Beam.DisplayName + ", L = " + topo.BeamLength.ToString("0", De) + " mm");
            lines.Add("Lastklasse " + o.LoadClass + ": q = " + Kn(q) + " kN/m²; Fd = " + Kn(o.GammaG) + " · Gk + " + Kn(o.GammaQ) + " · Qk");
            lines.Add("Gk = Eigengewicht aller Teile (kg · 9,81), Qk = q · Belagfläche · Faktor der Ebene.");
            lines.Add("");

            // 1. By kind of part: directly on the standards or through a beam.
            lines.Add("1. Eigengewicht nach Teilen (kN)");
            lines.Add("Teil;direkt auf den Ständern;über obere Träger/Konsolen;Summe");
            var kinds = new[]
            {
                (ContributionType.StandardSelfWeight, "Ständer"),
                (ContributionType.LedgerSelfWeight, "Riegel (je Ende die Hälfte)"),
                (ContributionType.OtherSelfWeight, "Diagonalen/Zubehör"),
                (ContributionType.BeamSelfWeight, "Obere Träger (Anteil je Auflager)"),
                (ContributionType.Deck, "Beläge"),
            };
            double totDirect = 0, totVia = 0;
            foreach (var (type, name) in kinds)
            {
                var cs = contribs.Where(c => c.Type == type).ToList();
                double direct = Gk(cs.Where(c => c.ViaBeam == null));
                double via = Gk(cs.Where(c => c.ViaBeam != null));
                totDirect += direct;
                totVia += via;
                lines.Add(name + ";" + Kn(direct) + ";" + Kn(via) + ";" + Kn(direct + via));
            }

            lines.Add("Summe Gk;" + Kn(totDirect) + ";" + Kn(totVia) + ";" + Kn(totDirect + totVia));
            lines.Add("");

            // 2. By level: decks (area, weight, live load) and the other parts that sit in the band of the level.
            lines.Add("2. Nach Ebenen");
            lines.Add("Ebene;Höhe über OK Träger, mm;Belagfläche m²;davon über obere Träger m²;Faktor Nutzlast;Qk kN;Gk Belag kN;Gk Ständer/Riegel/Zubehör/Träger kN (Teile in dieser Ebene)");
            var levels = topo.Levels.OrderBy(l => l.Z).ToList();
            double totArea = 0, totQ = 0;
            foreach (var l in levels)
            {
                var decks = contribs.Where(c => c.Type == ContributionType.Deck && c.Level == l).ToList();
                double area = decks.Sum(c => c.AreaM2);
                double areaVia = decks.Where(c => c.ViaBeam != null).Sum(c => c.AreaM2);
                double f = Factor(l);
                double qk = q * area * f;
                double others = Gk(contribs.Where(c => c.Type != ContributionType.Deck && BandOf(levels, c, s) == l));
                totArea += area;
                totQ += qk;
                lines.Add(l.Index + ";" + l.Height.ToString("0", De) + ";" + M2(area) + ";" + M2(areaVia) + ";" + (f * 100).ToString("0", De) + " %;" + Kn(qk) + ";" + Kn(Gk(decks)) + ";" + Kn(others));
            }

            double below = Gk(contribs.Where(c => c.Type != ContributionType.Deck && BandOf(levels, c, s) == null));
            lines.Add("unter Ebene 1;;;;;;;" + Kn(below));
            lines.Add("Summe;;" + M2(totArea) + ";;;" + Kn(totQ) + ";;");
            lines.Add("");

            // 3. By beam that passes the load down (the beam resting directly on the standards).
            lines.Add("3. Übertragende Träger");
            var beams = contribs.Where(c => c.ViaBeam != null).GroupBy(c => c.ViaBeam.Id).ToList();
            if (beams.Count == 0)
                lines.Add("Keine Last kommt über obere Träger.");
            foreach (var g in beams)
            {
                var first = g.First().ViaBeam;
                var link = topo.UpperBeams.FirstOrDefault(u => u.Beam.Id == first.Id);
                lines.Add((link != null && link.IsPinLedger ? "Riegel mit Zapfen " : link != null && link.IsStackTransfer ? "Ständer auf dem Kopf, " : "Träger ") + first.DisplayName + " [" + first.Id + "]" + (link == null ? "" : ", " + link.Supports.Count + " Auflager, Positionen mm: " +
                          string.Join(" / ", link.Supports.Select(x => x.Position.ToString("0", De) + (x.Column == null ? " (außerhalb der Auswahl)" : "")))));
                lines.Add("Ebene;Fläche m²;Faktor;Qk kN;Gk Belag kN");
                double beamQ = 0;
                foreach (var l in levels)
                {
                    var decks = g.Where(c => c.Type == ContributionType.Deck && c.Level == l).ToList();
                    if (decks.Count == 0)
                        continue;
                    double area = decks.Sum(c => c.AreaM2), f = Factor(l), qk = q * area * f;
                    beamQ += qk;
                    lines.Add(l.Index + ";" + M2(area) + ";" + (f * 100).ToString("0", De) + " %;" + Kn(qk) + ";" + Kn(Gk(decks)));
                }

                double gk = Gk(g);
                lines.Add("Gk gesamt (Belag, Ständer, Riegel, Zubehör, Trägergewicht);" + Kn(gk) + ";Qk gesamt;" + Kn(beamQ));
                lines.Add("");
            }

            AddOrigins(lines, session, contribs, q, Factor);
            AddDistribution(lines, session, q, Factor);

            lines.Add("6. Ergebnis");
            foreach (var p in session.Result.Points)
                lines.Add(p.Name + ": Gk " + Kn(p.Gk) + " kN (davon über Träger " + Kn(p.TransferGk) + "), Qk " + Kn(p.Qk) + " kN (davon über Träger " + Kn(p.TransferQk) + "), Fk " + Kn(p.Fk) + " kN, Fd " + Kn(p.Fd) + " kN");
            lines.Add("Hinweis: Ist ein Teil über mehrere Träger gegangen, steht er beim untersten Träger (dem, der direkt auf den Ständern liegt).");
            return lines;
        }

        private static string Pct(double v) => (v * 100).ToString("0.0", De) + " %";

        private static string ColumnName(Column c) =>
            string.IsNullOrEmpty(c.Name) ? "Ständer bei (" + c.Axis.X.ToString("0", De) + "; " + c.Axis.Y.ToString("0", De) + ")" : c.Name;

        /// <summary>
        /// 4. Where every area comes from: for each stand that collects load (the standards of the checked member and the stands on upper
        /// beams or on pins), its decks per level: size, the ledger it lies on, the lever arm, and the share the stand takes.
        /// </summary>
        private static void AddOrigins(List<string> lines, BeamLoadSession session, List<Contribution> mainContribs, double q, Func<Level, double> factor)
        {
            var topo = session.Topology;
            var s = session.Settings;
            lines.Add("4. Herkunft der Belagflächen je Ständer und Ebene");
            lines.Add("Ein Belag liegt auf zwei Riegeln: je Riegel die Hälfte. Am Riegel teilt das Hebelgesetz die Last auf die zwei Enden: Anteil des Ständers = ½ · (1 − d / L),");
            lines.Add("d = Abstand des Belagauflagers vom Ständer entlang des Riegels, L = Länge des Riegels. Beläge auf Konsolen gehen ganz auf den Konsolenständer.");
            var byColumn = topo.Collected.Where(c => c.Type == ContributionType.Deck && c.Level != null).GroupBy(c => c.Column).ToList();
            foreach (var g in byColumn.OrderBy(x => x.Key.IsMain ? 0 : 1).ThenBy(x => x.Key.Position))
            {
                var col = g.Key;
                string where = col.IsMain
                    ? "auf dem geprüften Element, Position " + col.Position.ToString("0", De) + " mm"
                    : "auf " + col.SupportBeam.DisplayName + " [" + col.SupportBeam.Id + "] bei " + col.Position.ToString("0", De) + " mm";
                lines.Add("");
                lines.Add("Ständer " + ColumnName(col) + " — " + where + ", sammelt " + M2(g.Sum(c => c.AreaM2)) + " m² Belag");
                lines.Add("Ebene;Belag;Belag m²;Riegel;L mm;d mm;½·(1−d/L);Anteil laut Rechnung;Fläche am Ständer m²;Nutzlast des Ständers kN");
                foreach (var lv in g.Select(c => c.Level).Distinct().OrderBy(l => l.Z))
                {
                    double f = factor(lv);
                    foreach (var c in g.Where(x => x.Level == lv).OrderBy(x => x.Source.Id))
                    {
                        string led = "", len = "", d = "", formula = "";
                        var v = c.Via;
                        if (v != null && v.Kind == ElementKind.Console)
                        {
                            led = "Konsole " + v.DisplayName;
                        }
                        else if (v != null)
                        {
                            led = v.DisplayName + " [" + v.Id + "]";
                            double L = v.PlanLength;
                            double dS = Geometry.Vec3.DistanceXY(v.Start, col.Axis), dE = Geometry.Vec3.DistanceXY(v.End, col.Axis);
                            if (Math.Min(dS, dE) <= 250)
                            {
                                double d0, d1;
                                Geometry.PlanGeometry.ProjectXY(v.Start, v.End, c.Source.Start, out d0);
                                Geometry.PlanGeometry.ProjectXY(v.Start, v.End, c.Source.End, out d1);
                                var bearing = d0 <= d1 ? c.Source.Start : c.Source.End;
                                double t = Math.Max(0, Math.Min(1, Geometry.PlanGeometry.ProjectXY(v.Start, v.End, bearing, out _)));
                                double dist = dS <= dE ? t * L : (1 - t) * L;
                                len = L.ToString("0", De);
                                d = dist.ToString("0", De);
                                formula = Kn(0.5 * (1 - dist / L));
                            }
                            else
                            {
                                len = L.ToString("0", De);
                                d = "über weiteren Riegel";
                            }
                        }

                        lines.Add(lv.Index + ";" + c.Source.DisplayName + " [" + c.Source.Id + "];" + M2(c.Source.AreaM2) + ";" + led + ";" + len + ";" + d + ";" + formula + ";" +
                                  Kn(c.Share) + ";" + M2(c.AreaM2) + ";" + Kn(q * c.AreaM2 * f));
                    }
                }
            }

            lines.Add("");
        }

        /// <summary>
        /// 5. How the load of every stand on an upper beam or on a pin is divided among the supports of that beam or ledger, and what each
        /// stand carries itself and from stands above it.
        /// </summary>
        private static void AddDistribution(List<string> lines, BeamLoadSession session, double q, Func<Level, double> factor)
        {
            var topo = session.Topology;
            var s = session.Settings;
            lines.Add("5. Verteilung durch obere Träger und durch Riegel mit Zapfen");
            if (topo.Transfers.Count == 0)
            {
                lines.Add("Keine Last wird über obere Träger oder Zapfen übertragen.");
                lines.Add("");
                return;
            }

            lines.Add("Obere Träger: jeder Ständer auf dem Träger liegt zwischen zwei Auflagern; Anteil Auflager k = (p(k+1) − x) / (p(k+1) − p(k)), Anteil Auflager k+1 = (x − p(k)) / (p(k+1) − p(k)).");
            lines.Add("Ständer auf einem Zapfen im Riegel: die gesamte Last des Ständers geht auf den Riegel; Auflager sind seine zwei Enden, x = Lage des Zapfens vom Riegelanfang.");
            if (!s.UpperBeamAllowUplift)
                lines.Add("Negative Anteile (Last auf dem Kragarm entlastet das entfernte Auflager) werden nicht angesetzt (UpperBeamAllowUplift = aus).");

            var memo = new Dictionary<Column, (double gk, double qk)>();
            (double gk, double qk) Total(Column c, int depth)
            {
                if (memo.TryGetValue(c, out var m))
                    return m;
                double gk = LoadCalculator.KgToKn(topo.Collected.Where(x => x.Column == c).Sum(x => x.WeightKg));
                double qk = topo.Collected.Where(x => x.Column == c && x.Type == ContributionType.Deck && x.Level != null).Sum(x => q * x.AreaM2 * factor(x.Level));
                if (depth < 10)
                {
                    foreach (var tr in topo.Transfers)
                    {
                        for (int i = 0; i < tr.Link.Supports.Count; i++)
                        {
                            if (tr.Link.Supports[i].Column == c && tr.Fractions[i] > 1e-9)
                            {
                                var t = Total(tr.Stand, depth + 1);
                                gk += tr.Fractions[i] * t.gk;
                                qk += tr.Fractions[i] * t.qk;
                            }
                        }
                    }
                }

                return memo[c] = (gk, qk);
            }

            foreach (var link in topo.UpperBeams.Where(l => topo.Transfers.Any(t => t.Link == l)))
            {
                lines.Add("");
                string kind = link.IsPinLedger ? "Riegel mit Zapfen" : link.IsStackTransfer ? (link.IsManual ? "Vom Benutzer gewählt:" : "Versetzt auf dem Kopf:") : link.Beam.Kind == ElementKind.Console ? "Konsole" : "Träger";
                lines.Add(kind + " " + link.Beam.DisplayName + " [" + link.Beam.Id + "], L = " + link.Beam.PlanLength.ToString("0", De) + " mm, Auflager: " +
                          string.Join("; ", link.Supports.Select((x, i) => (i + 1) + " bei " + x.Position.ToString("0", De) + " mm" + (x.Column == null ? " (keine gewählte Stütze: Anteil geht verloren)" : " = " + ColumnName(x.Column)))));
                string head = "Ständer;Position x mm;" + (link.IsPinLedger ? "Zapfen;" : "") + "Last des Ständers Gk kN;Qk kN";
                for (int i = 0; i < link.Supports.Count; i++)
                    head += ";Anteil Auflager " + (i + 1) + ";kN (Gk+Qk) an Auflager " + (i + 1);
                lines.Add(head);
                foreach (var tr in topo.Transfers.Where(t => t.Link == link).OrderBy(t => t.Stand.Position))
                {
                    var t = Total(tr.Stand, 0);
                    string row = ColumnName(tr.Stand) + ";" + tr.Stand.Position.ToString("0", De) + ";" +
                                 (link.IsPinLedger ? (link.PinOf.TryGetValue(tr.Stand, out var pin) ? pin.DisplayName + " [" + pin.Id + "]" : "") + ";" : "") + Kn(t.gk) + ";" + Kn(t.qk);
                    for (int i = 0; i < tr.Fractions.Length; i++)
                        row += ";" + Pct(tr.Fractions[i]) + ";" + Kn(tr.Fractions[i] * (t.gk + t.qk));
                    lines.Add(row);
                }
            }

            lines.Add("");
            lines.Add("Prüfsumme: Last, die über die Stützen der geprüften Träger ankommt (Gk / Qk)");
            foreach (var p in session.Result.Points)
            {
                var t = Total(p.Column, 0);
                lines.Add(p.Name + ";" + Kn(t.gk) + ";" + Kn(t.qk) + ";Tabelle: " + Kn(p.Gk) + ";" + Kn(p.Qk));
            }

            lines.Add("");
        }

        /// <summary>The deck level whose band a part belongs to: the highest level at or below the middle of the part.</summary>
        private static Level BandOf(List<Level> levels, Contribution c, BeamCheckSettings s)
        {
            double mid = (c.Source.ZMin + c.Source.ZMax) / 2;
            Level best = null;
            foreach (var l in levels)
                if (l.Z <= mid + s.LevelZTolerance)
                    best = l;
            return best;
        }
    }
}
