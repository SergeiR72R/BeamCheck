using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;
using BeamCheck.Core.Spindles;
using Xunit;

namespace BeamCheck.Core.Tests
{
    /// <summary>SPINDELLOAD engine (former V05_yashu engine tests T2-T8 and PERF), now on the BeamCheck engine of this repository.</summary>
    public class SpindleEngineTests
    {
        private const double Tol = 0.002;

        private sealed class Model
        {
            private int _h;
            public readonly ScanResult Scan = new ScanResult();

            public ScaffoldElement E(ElementKind k, Vec3 a, Vec3 b, double w, double z0, double z1, double kg, string d, bool spindle = false)
            {
                var e = new ScaffoldElement { Id = "E" + (++_h), Kind = k, Start = a, End = b, Width = w, ZMin = z0, ZMax = z1, WeightKg = kg, Description = d };
                Scan.Add(e, spindle, false);
                return e;
            }

            public ScaffoldElement Vt(double x, double y, double z0, double z1, double kg = 20) =>
                E(ElementKind.Standard, new Vec3(x, y, z0), new Vec3(x, y, z1), 48, z0, z1, kg, "Vertikalstiel UVR");

            public ScaffoldElement Sp(double x, double y, double z0, double z1) =>
                E(ElementKind.Standard, new Vec3(x, y, z0), new Vec3(x, y, z1), 48, z0, z1, 6, "Fußspindel UJB", true);

            public ScaffoldElement Bm(double ax, double ay, double bx, double by, double z0, double z1, double kg) =>
                E(ElementKind.Beam, new Vec3(ax, ay, z0), new Vec3(bx, by, z0), 100, z0, z1, kg, "RCS Schiene");

            public ScaffoldElement Ledger(Vec3 a, Vec3 b, double z, double kg, string d) => E(ElementKind.Ledger, a, b, 48, z - 60, z, kg, d);

            public ScaffoldElement Deck(Vec3 a, Vec3 b, double z, double kg) => E(ElementKind.Deck, a, b, 1000, z - 50, z, kg, "Belag UDI");
        }

        private static BeamCheckSettings Bs() => new BeamCheckSettings { UseShapeRecognition = false };

        private static SpindleSettings Ss()
        {
            var s = new SpindleSettings();
            s.Normalize();
            return s;
        }

        private static SpindleEngine Engine(Model m) => new SpindleEngine(m.Scan, Ss(), Bs(), 3);

        private static double KN(double kg) => kg * 9.81 / 1000;

        private static SpindleInfo S(CalcResult r, ScaffoldElement e) => r.Spindles.First(x => x.Key == e.Id);

        [Fact]
        public void T2_RcsBeamOnTwoVerticalsCarryingAThird()
        {
            var m = new Model();
            var a = m.Sp(0, 0, 0, 600); m.Vt(0, 0, 600, 3000);
            m.Sp(4000, 0, 0, 600); m.Vt(4000, 0, 600, 3000);
            m.Bm(-200, 0, 4200, 0, 3000, 3200, 100);
            var c = m.Sp(1000, 0, 3200, 3800); m.Vt(1000, 0, 3800, 6000);
            var r = Engine(m).Run(new Sidecar());

            // A: own column 26 kg + 3/4 of the column on the beam (26 kg) + half of the 100 kg beam = 95.5 kg
            Assert.Equal(0.938, S(r, a).DeadKn, Tol);
            Assert.Equal(KN(26), S(r, c).DeadKn, Tol);
            Assert.False(r.Cancelled);
        }

        [Fact]
        public void T3_HeadSpindleLinkedToTheSpindleAbove_LoadGoesDownTheColumn()
        {
            var m = new Model();
            var lb = m.Sp(0, 0, 0, 600); m.Vt(0, 0, 600, 2600); var lh = m.Sp(0, 0, 2600, 3200);
            var ub = m.Sp(0, 0, 3700, 4300); var uv = m.Vt(0, 0, 4300, 6300);
            var eng = Engine(m);
            var sc = new Sidecar();
            var r = eng.Run(sc);
            Assert.True(S(r, lh).Open);

            var e = sc.Get(lh.Id, true);
            e.LinkHandles.Add(ub.Id);
            e.HeadAnswer = "link";
            r = eng.Run(sc);
            Assert.Equal(KN(6 + 26), S(r, lh).DeadKn, Tol);
            Assert.Equal(KN(6 + 20 + 6 + 26), S(r, lb).DeadKn, Tol);
            Assert.False(S(r, lh).Open);

            // manual load on the head spindle reaches the base spindle
            e.Manual.Add(new ManualItem { Description = "test", DeadKn = 1, ServiceKn = 0.5 });
            r = eng.Run(sc, r, eng.KeysNear(r, 0, 0));
            Assert.Equal(KN(6 + 20 + 6 + 26) + 1, S(r, lb).DeadKn, Tol);
            Assert.Equal(0.5, S(r, lb).ServiceKn, Tol);

            // a removed part is removed from every spindle
            sc.Excluded.Add(uv.Id);
            r = eng.Run(sc, r, eng.KeysNear(r, 0, 0));
            Assert.Equal(KN(6 + 20 + 6 + 6) + 1, S(r, lb).DeadKn, Tol);
        }

        [Fact]
        public void T4_LinkToAPlainVertical_HeadCarriesTheLinkedColumn()
        {
            var m = new Model();
            var b4 = m.Sp(0, 0, 0, 600); m.Vt(0, 0, 600, 2600); var h4 = m.Sp(0, 0, 2600, 3200);
            var far = m.Vt(2000, 0, 4200, 6200); m.Vt(2000, 0, 6200, 8200);
            var eng = Engine(m);
            var sc = new Sidecar();
            var r = eng.Run(sc);
            Assert.Single(r.OpenHeads);

            var e = sc.Get(h4.Id, true);
            e.LinkHandles.Add(far.Id);
            e.HeadAnswer = "link";
            r = eng.Run(sc);
            Assert.Equal(KN(6 + 40), S(r, h4).DeadKn, Tol);
            Assert.Equal(KN(6 + 20 + 6 + 40), S(r, b4).DeadKn, Tol);
            Assert.Empty(r.OpenHeads);
            Assert.Contains(S(r, b4).Rows, x => x.Kind == "LINKED");
        }

        [Fact]
        public void T6_DecksOnLedgers_DeadTotalExactAndServiceLoadFromBeamCheck()
        {
            var m = new Model();
            foreach (var x in new[] { 0.0, 2000 })
                foreach (var y in new[] { 0.0, 1000 })
                {
                    m.Sp(x, y, 0, 600);
                    m.Vt(x, y, 600, 4600);
                }

            foreach (var z in new[] { 2000.0, 4000 })
            {
                m.Ledger(new Vec3(0, 0, z - 30), new Vec3(0, 1000, z - 30), z, 8, "Riegel UH 100");
                m.Ledger(new Vec3(2000, 0, z - 30), new Vec3(2000, 1000, z - 30), z, 8, "Riegel UH 100");
                m.Deck(new Vec3(0, 500, z), new Vec3(2000, 500, z), z, 40);
            }

            var r = Engine(m).Run(new Sidecar());
            Assert.Equal(4, r.Spindles.Count);
            Assert.Equal(KN(4 * 26 + 2 * (16 + 40)), r.Spindles.Sum(x => x.DeadKn), Tol);
            // load class 3 = 2 kN/m², one level loaded (default level mode): 2 m² deck, a quarter on each spindle
            Assert.All(r.Spindles, x => Assert.Equal(1.0, x.ServiceKn, Tol));
            Assert.All(r.Spindles, x => Assert.Equal(1.5 * x.DeadKn + 1.5 * x.ServiceKn, x.DesignKn, Tol));
        }

        [Fact]
        public void T7_VerticalFarOutOnALongBeam_IsFoundByCandidateGrowth()
        {
            var m = new Model();
            var a = m.Sp(0, 0, 0, 600); m.Vt(0, 0, 600, 3000);
            var b = m.Sp(8000, 0, 0, 600); m.Vt(8000, 0, 600, 3000);
            m.Bm(0, 0, 8000, 0, 3000, 3200, 0);
            m.Sp(6000, 0, 3200, 3800); m.Vt(6000, 0, 3800, 6000);
            var r = Engine(m).Run(new Sidecar());
            Assert.Equal(KN(26 + 0.25 * 26), S(r, a).DeadKn, Tol);
            Assert.Equal(KN(26 + 0.75 * 26), S(r, b).DeadKn, Tol);
        }

        [Fact]
        public void T8_HugePart_IsSkippedWithoutFreezing()
        {
            var m = new Model();
            var a = m.Sp(0, 0, 0, 600); m.Vt(0, 0, 600, 3000);
            m.Ledger(new Vec3(-1e8, 0, 1970), new Vec3(1e8, 0, 1970), 2000, 5, "XLINE as ledger");
            var sw = Stopwatch.StartNew();
            var eng = Engine(m);
            var r = eng.Run(new Sidecar());
            Assert.True(sw.ElapsedMilliseconds < 5000, "took " + sw.ElapsedMilliseconds + " ms");
            Assert.Equal(1, eng.GridSkipped);
            Assert.Equal(KN(26), S(r, a).DeadKn, Tol);
        }

        [Fact]
        public void AddedPart_IsDistributedByBeamCheck()
        {
            // a deck lying 700 mm above the ledgers is not found automatically; added by the user ("Objekte hinzufügen")
            // BeamCheck ties it to the nearest ledger and distributes it to the spindles
            var m = new Model();
            var spindles = new List<ScaffoldElement>();
            foreach (var x in new[] { 0.0, 2000 })
                foreach (var y in new[] { 0.0, 1000 })
                {
                    spindles.Add(m.Sp(x, y, 0, 600));
                    m.Vt(x, y, 600, 4600);
                }

            m.Ledger(new Vec3(0, 0, 1970), new Vec3(0, 1000, 1970), 2000, 8, "Riegel UH 100");
            m.Ledger(new Vec3(2000, 0, 1970), new Vec3(2000, 1000, 1970), 2000, 8, "Riegel UH 100");
            var loose = m.Deck(new Vec3(0, 500, 2700), new Vec3(2000, 500, 2700), 2700, 40);
            var eng = Engine(m);
            var sc = new Sidecar();
            var r = eng.Run(sc);
            double before = r.Spindles.Sum(x => x.DeadKn);
            Assert.Equal(KN(4 * 26 + 16), before, Tol);

            sc.Get(spindles[0].Id, true).AddedIds.Add(loose.Id);
            r = eng.Run(sc, r, new[] { spindles[0].Id });
            Assert.Equal(KN(26 + 4) + KN(10), S(r, spindles[0]).DeadKn, Tol);
            Assert.Contains(loose.Id, S(r, spindles[0]).UsedIds);
        }

        [Fact]
        public void Cancel_KeepsPartialResults()
        {
            var m = new Model();
            for (int i = 0; i < 20; i++) { m.Sp(i * 2000, 0, 0, 600); m.Vt(i * 2000, 0, 600, 2600); }
            var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            var r = Engine(m).Run(new Sidecar(), null, null, cts.Token);
            Assert.True(r.Cancelled);
            Assert.All(r.Spindles.Where(x => !x.Calculated), x => Assert.Contains("Not calculated (cancelled).", x.Problems));
        }

        [Fact]
        public void Perf_LargeModel_AndPartialRecalc()
        {
            var m = new Model();
            const int n = 30;
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n; j++)
                {
                    m.Sp(i * 2000, j * 1000, 0, 600);
                    for (int k = 0; k < 4; k++) m.Vt(i * 2000, j * 1000, 600 + k * 2000, 2600 + k * 2000, 10);
                }

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    for (int k = 1; k <= 4; k++)
                    {
                        double z = 600 + k * 2000;
                        m.Ledger(new Vec3(i * 2000, j * 1000, z - 30), new Vec3(i * 2000, j * 1000 + 1000, z - 30), z, 5, "Riegel UH 100");
                        m.Ledger(new Vec3(i * 2000, j * 1000, z - 30), new Vec3(i * 2000 + 2000, j * 1000, z - 30), z, 8, "Riegel UH 200");
                        m.Deck(new Vec3(i * 2000, j * 1000 + 500, z), new Vec3(i * 2000 + 2000, j * 1000 + 500, z), z, 20);
                    }

            var eng = Engine(m);
            var sc = new Sidecar();
            var r = eng.Run(sc);
            Assert.Equal(961, r.Spindles.Count);
            Assert.All(r.Spindles, x => Assert.True(x.Calculated));
            // check value of the summary: nearly the whole model weight reaches the bottom spindles
            Assert.InRange(r.BaseSpindlesKn / r.ModelWeightKn, 0.98, 1.02);
            Assert.True(r.Millis < 60000, "full run " + r.Millis + " ms");

            var keys = eng.KeysNear(r, 20000, 10000);
            var r2 = eng.Run(sc, r, keys);
            Assert.True(keys.Count < r.Spindles.Count / 4);
            Assert.True(r2.Millis < 10000, "partial recalc " + r2.Millis + " ms");
        }

        [Fact]
        public void Sidecar_And_Settings_RoundTrip()
        {
            var dir = Path.Combine(Path.GetTempPath(), "spindle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "x.spindleload.json");
                var sc = new Sidecar();
                var e = sc.Get("1A2F", true);
                e.LinkHandles.Add("3B");
                e.Manual.Add(new ManualItem { Description = "m", DeadKn = 1.5, ServiceKn = 0.25 });
                sc.Get("EMPTY", true);
                sc.Excluded.Add("77");
                sc.ExcludedBatches.Add(1);
                sc.Save(path);
                var back = Sidecar.Load(path);
                Assert.Single(back.Edits);
                Assert.Equal("3B", back.Get("1A2F", false).LinkHandles.Single());
                Assert.Equal(1.5, back.Get("1A2F", false).Manual.Single().DeadKn);
                Assert.Equal("77", back.Excluded.Single());

                var sp = Path.Combine(dir, SpindleSettings.FileName);
                var s = SpindleSettings.LoadOrCreate(out var used, sp);
                Assert.Equal(sp, used);
                Assert.True(File.Exists(sp));
                Assert.True(s.SpindleRx.IsMatch("Fußspindel UJB 38-50/30"));
                Assert.False(s.SpindleRx.IsMatch("UVA 49 Anfangsstück"));

                // an old V05_yashu file (version 6) is replaced by the new defaults and kept as .bak
                File.WriteAllText(sp, "{\"SettingsVersion\":6,\"CandidateRadius\":1234}");
                s = SpindleSettings.LoadOrCreate(out used, sp);
                Assert.Equal(3500, s.CandidateRadius);
                Assert.True(File.Exists(sp + ".v6.bak"));

                File.WriteAllText(sp, "{\"SettingsVersion\":" + SpindleSettings.CurrentVersion + ",\"CandidateRadius\":1234}");
                s = SpindleSettings.LoadOrCreate(out used, sp);
                Assert.Equal(1234, s.CandidateRadius);
                Assert.Equal(200, s.VirtualSupportSize);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
