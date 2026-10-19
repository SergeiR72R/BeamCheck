using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BeamCheck.Core.Batch;
using BeamCheck.Core.Settings;

namespace BeamCheck.Core.Proofs
{
    public enum BeamProfile
    {
        Unknown,
        Sru,
        Rcs,
        Gt24,
        Vt20,
    }

    /// <summary>One verification: design value against resistance.</summary>
    public sealed class ProofCheck
    {
        public string Name { get; set; }

        /// <summary>From the left edge, mm (null for checks that hold along the whole beam).</summary>
        public double? Position { get; set; }

        public double Ed { get; set; }

        public double Rd { get; set; }

        public string Unit { get; set; }

        /// <summary>Utilisation η = Ed / Rd (or the interaction value).</summary>
        public double Eta { get; set; }

        public string Note { get; set; }

        public bool Ok => Eta <= 1.0 + 1e-9;
    }

    public sealed class BeamProof
    {
        public BeamProfile Profile { get; set; }

        public bool Done { get; set; }

        public string Message { get; set; }

        public List<ProofCheck> Checks { get; } = new List<ProofCheck>();

        public List<string> Notes { get; } = new List<string>();

        public ProofCheck Governing => Checks.OrderByDescending(c => c.Eta).FirstOrDefault();

        public double MaxEta => Checks.Count == 0 ? 0 : Checks.Max(c => c.Eta);

        public bool Ok => Done && Checks.All(c => c.Ok);

        public string Verdict => !Done ? "kein Nachweis" : Ok ? "erfüllt" : "NICHT erfüllt";
    }

    /// <summary>
    /// Ultimate limit state proofs of the PERI standard beams, from the internal forces of the beam as a continuous beam on pin supports
    /// under the design loads Fd and its own weight × γG. Resistances as in "Beam proofs for SRU, RCS, GT24, VT20" (PERI static tool):
    /// SRU — ST_was_2024/00040 (2 × U120, S235), RCS — ST_kls_2016-00106 (2 × U200, S355, net section), GT 24 and VT 20 — MULTIFLEX
    /// instructions for assembly and use. No normal force: the beams carry vertical loads only.
    /// </summary>
    public static class BeamProofs
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // --- SRU: design resistances per cross-section (N kN, Vz kN, My kNcm) and interaction factors aw, ww ---
        private sealed class SruArea
        {
            public string Name;
            public double NRd, VRd, MRd, Aw, Ww;
        }

        private static readonly SruArea[] SruAreas =
        {
            new SruArea { Name = "Bereich A, Schnitt 4-4", NRd = 602.3, VRd = 113.39, MRd = 2834.01, Aw = 0.33, Ww = 0.22 },
            new SruArea { Name = "Bereich A, Schnitt 5-5", NRd = 593.67, VRd = 149.66, MRd = 2518.77, Aw = 0.44, Ww = 0.32 },
            new SruArea { Name = "Bereich B, Schnitt 2-2", NRd = 663.02, VRd = 148.45, MRd = 2873.75, Aw = 0.39, Ww = 0.23 },
        };

        // --- RCS: net section 2 × U200, S355, γM1 = 1.1 ---
        private const double RcsFy = 35.5, RcsGammaM1 = 1.1;
        private const double RcsWpl = 411.18;          // cm³, net
        private const double RcsVplRd = 432.9345769;   // kN, Av,z = Aw (net) · fy / (γM1 · √3)
        private const double RcsTw = 0.85, RcsHw = 17.7, RcsD1 = 2.1, RcsE1 = 5.5, RcsD2 = 2.6, RcsE2 = 5.5;
        private static double RcsMplRd => RcsWpl * RcsFy / RcsGammaM1; // kNcm

        /// <summary>MV,y,Rd of the RCS (kNcm): plastic moment with the web reduced by ρ for shear (EN 1993-1-1, 6.2.8 / Wagenknecht).</summary>
        public static double RcsMvRd(double rho) =>
            (RcsWpl - rho * 2 * RcsTw * (RcsHw * RcsHw / 4 - RcsD1 * RcsE1 - RcsD2 * RcsE2)) * RcsFy / RcsGammaM1;

        // --- GT 24: girder VRd 19.5 kN, MRd 10.5 kNm; supports by case (Fm,Rd kN, Mm,Rd kNm) ---
        private const double Gt24VRd = 19.5, Gt24MRd = 10.5;
        private static readonly double[] Gt24FmRd = { 24, 19.5, 42, 30 };
        private static readonly double[] Gt24MmRd = { 10.5, 6, 10.5, 6 };

        // --- VT 20: Rb,Rd 24 kN (end support, ≥ 15 cm overhang) or 33 kN (support ≥ 45 cm from the end), VRd 16.5 kN, MRd 7.5 kNm ---
        private const double Vt20VRd = 16.5, Vt20MRd = 7.5, Vt20RbEnd = 24, Vt20RbInner = 33;

        private static HashSet<string> Set(string list) => new HashSet<string>(list.Split(new[] { ' ', '|' }, StringSplitOptions.RemoveEmptyEntries));

        private static readonly HashSet<string> SruArticles = Set("103868 103871 123809 103874 103877 103886 125088 103889 103898 125812 103892 103929 103903 103906 103915 103918 103922 103925 103928 103943");
        private static readonly HashSet<string> RcsArticles = Set("114166 109469 112102 109470 112141 109471 109472 109610");
        private static readonly HashSet<string> Gt24Articles = Set("075100 075120 075150 075180 075210 075240 075270 075300 075330 075360 075390 075420 075450 075480 075510 075540 075570 075600");
        private static readonly HashSet<string> Vt20Articles = new HashSet<string>(
            Set("073710 073720 073730 073740 073750 073760 073770 073780 073790 073800 073810"           // VT 20
              + " 074890 074905 074910 074920 074930 074940 074950 074960 074970 074980 074990"          // VT 20K
              + " 073650 073651 073652 073653 073654 073655 073656 073657 073658 073659 073660"          // VT 20 Alpha
              + " 073990 073991 073992 073993 073994 073995 073996 073997 073998 073999"));              // Alpha 3S

        public static BeamProfile Recognise(string description, string article)
        {
            string d = description ?? "";
            if (Regex.IsMatch(d, @"(?i)(?<![A-Z0-9])SRU(?![A-Z])"))
                return BeamProfile.Sru;
            if (Regex.IsMatch(d, @"(?i)(?<![A-Z0-9])RCS(?![A-Z])"))
                return BeamProfile.Rcs;
            if (Regex.IsMatch(d, @"(?i)(?<![A-Z0-9])GT ?24(?!\d)"))
                return BeamProfile.Gt24;
            if (Regex.IsMatch(d, @"(?i)(?<![A-Z0-9])VT ?20(?!\d)"))
                return BeamProfile.Vt20;
            string a = (article ?? "").Trim();
            if (a.Length >= 6)
            {
                a = a.Substring(0, 6);
                if (SruArticles.Contains(a))
                    return BeamProfile.Sru;
                if (RcsArticles.Contains(a))
                    return BeamProfile.Rcs;
                if (Gt24Articles.Contains(a))
                    return BeamProfile.Gt24;
                if (Vt20Articles.Contains(a))
                    return BeamProfile.Vt20;
            }

            return BeamProfile.Unknown;
        }

        public static BeamProof Check(BeamReport r, BeamCheckSettings s)
        {
            var proof = new BeamProof { Profile = Recognise(r.Description, r.Article) };
            if (proof.Profile == BeamProfile.Unknown)
            {
                proof.Message = "Profil nicht erkannt (SRU, RCS, GT 24, VT 20)";
                return proof;
            }

            if (r.ContinuousSupport)
            {
                proof.Message = "liegt vollflächig auf — kein Biegenachweis";
                return proof;
            }

            double len = r.Length / 1000.0;
            var supports = r.Supports.Select(x => x.Position / 1000.0).ToList();
            var loads = r.Loads.Select(l => Tuple.Create(l.Position / 1000.0, l.Fd)).ToList();
            double q = len > 0 ? s.GammaG * r.SelfWeightKn / len : 0;
            var st = BeamStatics.Solve(len, supports, loads, q);
            if (!st.Stable)
            {
                proof.Message = st.Message;
                return proof;
            }

            proof.Done = true;
            if ((r.Description ?? "").Contains("Verbinder"))
                proof.Notes.Add("Mit Kletterschienenverbinder gestoßen: als durchgehender Träger gerechnet, der Stoß selbst wird nicht nachgewiesen.");
            proof.Notes.Add(string.Format(Inv, "Durchlaufträger auf {0} Gelenklagern, Lasten Fd, Eigengewicht {1:0.00} kN/m × γG {2:0.##}.", supports.Count, len > 0 ? r.SelfWeightKn / len : 0, s.GammaG));
            switch (proof.Profile)
            {
                case BeamProfile.Sru:
                    CheckSru(proof, st);
                    break;
                case BeamProfile.Rcs:
                    CheckRcs(proof, st);
                    break;
                case BeamProfile.Gt24:
                    CheckGirder(proof, st, r, s, true);
                    break;
                case BeamProfile.Vt20:
                    CheckGirder(proof, st, r, s, false);
                    break;
            }

            return proof;
        }

        private static ProofCheck C(string name, double? xm, double ed, double rd, string unit, double eta, string note = null) =>
            new ProofCheck { Name = name, Position = xm.HasValue ? xm * 1000.0 : null, Ed = ed, Rd = rd, Unit = unit, Eta = eta, Note = note };

        /// <summary>SRU: n, v, m and the interactions v-m and n-v-m (N = 0) of every cross-section, the worst one governs (where area A ends is not known from the model).</summary>
        private static void CheckSru(BeamProof proof, BeamStaticsResult st)
        {
            var mMax = st.MaxMoment;
            var vMax = st.MaxShear;
            foreach (var a in SruAreas)
            {
                proof.Checks.Add(C("v = |Vz,Ed| / Vz,Rd (" + a.Name + ")", vMax.X, vMax.VAbs, a.VRd, "kN", vMax.VAbs / a.VRd));
                proof.Checks.Add(C("m = |My,Ed| / My,Rd (" + a.Name + ")", mMax.X, Math.Abs(mMax.M), a.MRd / 100, "kNm", Math.Abs(mMax.M) * 100 / a.MRd));

                // Interaction at every point: ρ from the shear there.
                ProofCheck worst = null;
                foreach (var p in st.Points)
                {
                    double v = p.VAbs / a.VRd, m = Math.Abs(p.M) * 100 / a.MRd;
                    if (v > 1)
                        continue;
                    double rz = Math.Pow(2 * Math.Max(0.5, v) - 1, 2);
                    double vm = m / (1 - a.Ww * rz);
                    double nvm = m * (1 - 0.5 * a.Aw * (1 - rz) / (1 - rz * a.Aw)) / (1 - a.Ww * rz);
                    double eta = Math.Max(vm, nvm);
                    if (worst == null || eta > worst.Eta)
                        worst = C("v-m = my / (1 − ww · ρ) (" + a.Name + ")", p.X, Math.Abs(p.M), a.MRd / 100 * (1 - a.Ww * rz), "kNm", eta, string.Format(Inv, "ρ = {0:0.000}", rz));
                }

                if (worst != null)
                    proof.Checks.Add(worst);
            }

            proof.Notes.Add("SRU: Lage der Bereiche A/B im Träger unbekannt — jeder Schnitt wird mit den Schnittgrößen des ganzen Trägers geprüft (auf der sicheren Seite).");
        }

        /// <summary>RCS: v, m and v-m with the plastic moment reduced for shear (EN 1993-1-1, 6.2.8; net section).</summary>
        private static void CheckRcs(BeamProof proof, BeamStaticsResult st)
        {
            var mMax = st.MaxMoment;
            var vMax = st.MaxShear;
            proof.Checks.Add(C("v = |Vz,Ed| / Vpl,z,Rd", vMax.X, vMax.VAbs, RcsVplRd, "kN", vMax.VAbs / RcsVplRd));
            proof.Checks.Add(C("m = |My,Ed| / Mpl,y,Rd", mMax.X, Math.Abs(mMax.M), RcsMplRd / 100, "kNm", Math.Abs(mMax.M) * 100 / RcsMplRd));
            ProofCheck worst = null;
            foreach (var p in st.Points)
            {
                double v = p.VAbs / RcsVplRd;
                double rho = Math.Min(Math.Pow(2 * Math.Max(v, 0.5) - 1, 2), 1);
                double mv = RcsMvRd(rho);
                double eta = Math.Abs(p.M) * 100 / mv;
                if (worst == null || eta > worst.Eta)
                    worst = C("v-m = |My,Ed| / MV,y,Rd", p.X, Math.Abs(p.M), mv / 100, "kNm", eta, string.Format(Inv, "ρ = {0:0.000}", rho));
            }

            if (worst != null)
                proof.Checks.Add(worst);
        }

        /// <summary>GT 24 and VT 20: shear and moment of the girder, and at every support the reaction (and for GT 24 the moment there) by its case.</summary>
        private static void CheckGirder(BeamProof proof, BeamStaticsResult st, BeamReport r, BeamCheckSettings s, bool gt24)
        {
            var mMax = st.MaxMoment;
            var vMax = st.MaxShear;
            double vRd = gt24 ? Gt24VRd : Vt20VRd, mRd = gt24 ? Gt24MRd : Vt20MRd;
            proof.Checks.Add(C("v = |VEd| / VRd (Träger)", vMax.X, vMax.VAbs, vRd, "kN", vMax.VAbs / vRd));
            proof.Checks.Add(C("m = |MEd| / MRd (Träger)", mMax.X, Math.Abs(mMax.M), mRd, "kNm", Math.Abs(mMax.M) / mRd));

            for (int i = 0; i < r.Supports.Count; i++)
            {
                var sup = r.Supports[i];
                double x = sup.Position / 1000.0;
                double toEnd = Math.Min(sup.Position, r.Length - sup.Position); // mm
                double reaction = st.Reactions[i];
                double mSup = Math.Abs(st.SupportMoments[i]);
                bool outer = i == 0 || i == r.Supports.Count - 1;
                if (gt24)
                {
                    bool end = outer && toEnd <= s.GirderEndSupportOverhang;
                    int caseNo = end ? (s.Gt24FavourableDiagonals ? 1 : 2) : (s.Gt24FavourableDiagonals ? 3 : 4);
                    double fRd = Gt24FmRd[caseNo - 1], mmRd = Gt24MmRd[caseNo - 1];
                    string kind = end ? "Endauflager" : "Zwischenauflager/Kragarm";
                    proof.Checks.Add(C(sup.Label + ": f = Fm,Ed / Fm,Rd (Fall " + caseNo + ", " + kind + ")", x, reaction, fRd, "kN", reaction / fRd));
                    proof.Checks.Add(C(sup.Label + ": mm = |Mm,Ed| / Mm,Rd (Fall " + caseNo + ")", x, mSup, mmRd, "kNm", mSup / mmRd));
                    if (end && toEnd < 160)
                        proof.Notes.Add(string.Format(Inv, "GT 24, {0}: Überstand {1:0} mm < 16 cm (Mindestmaß am Endauflager).", sup.Label, toEnd));
                }
                else
                {
                    bool inner = toEnd >= 450;
                    double rb = inner ? Vt20RbInner : Vt20RbEnd;
                    proof.Checks.Add(C(sup.Label + ": r = Rb,Ed / Rb,Rd (Fall " + (inner ? 2 : 1) + (inner ? ", ≥ 45 cm vom Ende" : ", Endauflager") + ")", x, reaction, rb, "kN", reaction / rb));
                    if (toEnd < 150)
                        proof.Notes.Add(string.Format(Inv, "VT 20, {0}: Überstand {1:0} mm < 15 cm (Mindestmaß am Endauflager).", sup.Label, toEnd));
                }
            }

            if (gt24 && !s.Gt24FavourableDiagonals)
                proof.Notes.Add("GT 24: Lage der Diagonalen am Auflager aus dem Modell nicht bekannt — ungünstigerer Fall angesetzt (Fall 2 am Ende, Fall 4 innen).");
        }
    }
}
