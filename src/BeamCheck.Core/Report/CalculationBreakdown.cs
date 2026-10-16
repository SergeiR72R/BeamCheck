using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Settings;

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
                lines.Add("Träger " + first.DisplayName + " [" + first.Id + "]" + (link == null ? "" : ", " + link.Supports.Count + " Auflager, Positionen mm: " +
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

            lines.Add("4. Ergebnis");
            foreach (var p in session.Result.Points)
                lines.Add(p.Name + ": Gk " + Kn(p.Gk) + " kN (davon über Träger " + Kn(p.TransferGk) + "), Qk " + Kn(p.Qk) + " kN (davon über Träger " + Kn(p.TransferQk) + "), Fk " + Kn(p.Fk) + " kN, Fd " + Kn(p.Fd) + " kN");
            lines.Add("Hinweis: Ist ein Teil über mehrere Träger gegangen, steht er beim untersten Träger (dem, der direkt auf den Ständern liegt).");
            return lines;
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
