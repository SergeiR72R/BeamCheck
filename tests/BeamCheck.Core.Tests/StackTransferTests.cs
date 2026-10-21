using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    /// <summary>
    /// BEAMLOAD: a column ending in a head spindle carries the next stack even when that stack is modelled offset (beside the head,
    /// or above a gap): found automatically within StackTransferPlanDistance / StackTransferHeight, otherwise linked by the user.
    /// </summary>
    public class StackTransferTests
    {
        private const double Tol = 0.002;
        private int _id;
        private readonly List<ScaffoldElement> _all = new List<ScaffoldElement>();

        private ScaffoldElement E(ElementKind k, double x, double y, double z0, double z1, double kg, string name)
        {
            var e = new ScaffoldElement
            {
                Id = "T" + (++_id), Kind = k, Start = new Vec3(x, y, z0), End = new Vec3(x, y, z1), Width = 48, ZMin = z0, ZMax = z1, WeightKg = kg, Description = name,
            };
            _all.Add(e);
            return e;
        }

        private ScaffoldElement Std(double x, double y, double z0, double z1) => E(ElementKind.Standard, x, y, z0, z1, 20, "Vertikalstiel UVR 200");

        private ScaffoldElement Spindle(double x, double y, double z0, double z1, string name) => E(ElementKind.Standard, x, y, z0, z1, 6, name);

        private static double KN(double kg) => kg * 9.81 / 1000;

        /// <summary>Beam along X (top at Z 0), two columns on it ending in head spindles; on each head the next stack offset by (dx, dz).</summary>
        private (BeamLoadSession session, List<ScaffoldElement> lower, List<ScaffoldElement> heads, List<ScaffoldElement> upperFeet) Build(BeamCheckSettings s, double dx, double dz)
        {
            var beam = new ScaffoldElement { Id = "BEAM", Kind = ElementKind.Beam, Start = new Vec3(0, 0, -200), End = new Vec3(3000, 0, -200), Width = 100, ZMin = -200, ZMax = 0, WeightKg = 50, Description = "RCS Schiene" };
            var lower = new List<ScaffoldElement>();
            var heads = new List<ScaffoldElement>();
            var feet = new List<ScaffoldElement>();
            foreach (var x in new[] { 500.0, 2500 })
            {
                lower.Add(Std(x, 0, 0, 2000));
                heads.Add(Spindle(x, 0, 2000, 2600, "Kopfspindel UJB 38-50/30"));
                feet.Add(Spindle(x, dx, 2600 + dz, 3200 + dz, "Fußspindel UJB 38-50/30"));
                Std(x, dx, 3200 + dz, 5200 + dz);
            }

            var input = new AnalysisInput { Beam = beam, SelectedStandards = new List<ScaffoldElement>(lower), Candidates = new List<ScaffoldElement>(_all) };
            var session = new BeamLoadSession(s, input);
            return (session, lower, heads, feet);
        }

        private static BeamCheckSettings Settings(bool transfer = true) => new BeamCheckSettings { UseShapeRecognition = false, StackTransferEnabled = transfer };

        [Fact]
        public void StackBesideTheHeadSpindle_IsCarriedByTheBeam()
        {
            var (session, _, _, _) = Build(Settings(), 500, 0);
            var points = session.Result.Points;
            Assert.Equal(2, points.Count);
            Assert.All(points, p => Assert.Equal(KN(20 + 6 + 6 + 20), p.Gk, Tol));
            Assert.Empty(session.Topology.OpenHeads);
            Assert.Equal(2, session.Topology.UpperBeams.Count(l => l.IsStackTransfer && !l.IsManual));
            Assert.Contains(session.Topology.Warnings, w => w.Contains("Lastübergabe") || w.Contains("auf dem Kopf"));
        }

        [Fact]
        public void Rechenweg_NamesTheTransfer()
        {
            var (session, _, _, _) = Build(Settings(), 500, 0);
            var text = string.Join("\n", BeamCheck.Core.Report.CalculationBreakdown.Build(session));
            Assert.Contains("Versetzt auf dem Kopf", text);
        }

        [Fact]
        public void StackAboveAGap_IsCarriedByTheBeam()
        {
            var (session, _, _, _) = Build(Settings(), 0, 450);
            Assert.All(session.Result.Points, p => Assert.Equal(KN(20 + 6 + 6 + 20), p.Gk, Tol));
        }

        [Fact]
        public void SwitchedOff_HeadSpindlesAreOpen_AndAManualLinkCarriesTheStack()
        {
            var (session, _, heads, feet) = Build(Settings(false), 500, 0);
            Assert.All(session.Result.Points, p => Assert.Equal(KN(20 + 6), p.Gk, Tol));
            Assert.Equal(2, session.Topology.OpenHeads.Count);

            session.LinkStack(heads[0].Id, feet[0]);
            var pts = session.Result.Points.OrderBy(p => p.Position).ToList();
            Assert.Equal(KN(20 + 6 + 6 + 20), pts[0].Gk, Tol);
            Assert.Equal(KN(20 + 6), pts[1].Gk, Tol);
            Assert.Single(session.Topology.OpenHeads);
            Assert.True(session.Topology.UpperBeams.Single(l => l.IsStackTransfer).IsManual);
        }

        [Fact]
        public void TooFar_IsNotTakenAutomatically_ButByHand()
        {
            // 1.5 m beside the head: another structure as far as the program can tell
            var (session, _, heads, feet) = Build(Settings(), 1500, 0);
            Assert.All(session.Result.Points, p => Assert.Equal(KN(20 + 6), p.Gk, Tol));
            Assert.Equal(2, session.Topology.OpenHeads.Count);

            session.LinkStack(heads[1].Id, feet[1]);
            var pts = session.Result.Points.OrderBy(p => p.Position).ToList();
            Assert.Equal(KN(20 + 6 + 6 + 20), pts[1].Gk, Tol);
        }

        [Fact]
        public void ShippedSettings_HaveTheTransferSwitchedOn()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "deploy", "BeamCheck.settings.json")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var s = SettingsStore.Load(System.IO.Path.Combine(dir.FullName, "deploy", "BeamCheck.settings.json"));
            Assert.True(s.StackTransferEnabled);
            Assert.Equal(600, s.StackTransferPlanDistance);
            Assert.Equal(600, s.StackTransferHeight);
            Assert.Matches(s.HeadPartPattern, "Kopfspindel UJB 38-50/30");
            Assert.Matches(s.BuildingSupportLayerPattern, "BUILDING STRUCTURE+");
            Assert.Equal(300, s.BuildingSupportHeight);
            Assert.Equal(50, s.BuildingSupportPlanTolerance);

            // an older settings file without these values gets the defaults
            using (var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"SettingsVersion\":24}")))
                Assert.True(SettingsStore.Deserialize(ms).StackTransferEnabled);
        }

        [Fact]
        public void SupportStandardComingUpThroughTheBeam_IsNoLoadOnIt()
        {
            // B-005 in "Beam check Feed_V1": the UVR under the SRU (support S2) reaches a little above the beam top, a deck level hangs on it
            // 700 mm under the beam, and an empty UVB clamp sits on the beam there. The deck level is not on the beam.
            var beam = new ScaffoldElement { Id = "SRU", Kind = ElementKind.Beam, Start = new Vec3(0, 0, -60), End = new Vec3(4972, 0, -60), Width = 120, ZMin = -120, ZMax = 0, WeightKg = 135, Description = "SRU U120" };
            var support = Std(4049, 0, -2236, 20);                                   // UVR 200 below the beam, body up to 20 mm above its top
            E(ElementKind.Ledger, 4049, 0, -800, -710, 4, "Horizontalriegel UH 100 Plus").End = new Vec3(4049, 1000, -710);
            E(ElementKind.Ledger, 4049, 1000, -2236, 20, 10, "Vertikalstiel UVR 200").Kind = ElementKind.Standard;
            _all.Add(new ScaffoldElement { Id = "DECK", Kind = ElementKind.Deck, Start = new Vec3(4049, 500, -680), End = new Vec3(6000, 500, -680), Width = 1000, ZMin = -730, ZMax = -680, WeightKg = 40, Description = "Stahlbelag UDG" });
            var clamp = E(ElementKind.Standard, 4049, 0, -116, 344, 3, "UVB 136779");
            var session = new BeamLoadSession(Settings(), new AnalysisInput { Beam = beam, SelectedStandards = { support, clamp }, Candidates = new List<ScaffoldElement>(_all) });
            var c = session.Result.Points.Single();
            Assert.Equal(KN(3), c.Gk, Tol);
            Assert.Equal(0, c.Qk, Tol);
            Assert.DoesNotContain("DECK", session.Topology.UsedElementIds);
        }

        [Fact]
        public void ConsoleOnTheSupportStandardUnderTheBeam_IsNoLoadOnIt()
        {
            // B-005 in "Beam check Feed_V1": the support standard under the SRU carries a console 2.7 m lower, a pin ledger hangs on its
            // tip with stacks and decks on it. On the beam at the support sits an empty UVA clamp touching the support standard.
            // The console belongs to the support standard, not to the clamp: nothing of it is a load on the beam.
            var beam = new ScaffoldElement { Id = "SRU", Kind = ElementKind.Beam, Start = new Vec3(0, 0, -60), End = new Vec3(4972, 0, -60), Width = 52, ZMin = -120, ZMax = 0, WeightKg = 135, Description = "SRU 497" };
            var support = Std(4049, 0, -2236, -76);                                           // UVR 200 under the beam
            Std(4049, 0, -4236, -2236);
            var clamp = E(ElementKind.Standard, 4049, 0, -236, 224, 3, "UVA");               // touches the UVR top
            var console = new ScaffoldElement { Id = "UCM", Kind = ElementKind.Console, Start = new Vec3(4049, 0, -2800), End = new Vec3(3549, 0, -2800), Width = 60, ZMin = -3105, ZMax = -2736, WeightKg = 8, Description = "UCM 50 mit Zapfen" };
            _all.Add(console);
            var deck = new ScaffoldElement { Id = "DECK", Kind = ElementKind.Deck, Start = new Vec3(3549, 500, -2750), End = new Vec3(3549, 2500, -2750), Width = 1000, ZMin = -2800, ZMax = -2750, WeightKg = 40, Description = "Stahlbelag UDG" };
            _all.Add(deck);
            var session = new BeamLoadSession(Settings(), new AnalysisInput { Beam = beam, SelectedStandards = { clamp }, Candidates = new List<ScaffoldElement>(_all) });
            var c = session.Result.Points.Single();
            Assert.Equal(KN(3), c.Gk, Tol);
            Assert.Equal(0, c.Qk, Tol);
            Assert.DoesNotContain(session.Topology.UpperBeams, l => l.Beam.Id == "UCM");
        }

        [Fact]
        public void PlainScaffoldTop_IsNoOpenHead()
        {
            var beam = new ScaffoldElement { Id = "BEAM", Kind = ElementKind.Beam, Start = new Vec3(0, 0, -200), End = new Vec3(3000, 0, -200), Width = 100, ZMin = -200, ZMax = 0, WeightKg = 50, Description = "RCS Schiene" };
            var s1 = Std(500, 0, 0, 2000);
            var session = new BeamLoadSession(Settings(), new AnalysisInput { Beam = beam, SelectedStandards = { s1 }, Candidates = new List<ScaffoldElement>(_all) });
            Assert.Empty(session.Topology.OpenHeads);
            Assert.Empty(session.Topology.UpperBeams);
        }
    }
}
