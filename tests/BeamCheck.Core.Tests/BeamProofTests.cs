using System;
using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Batch;
using BeamCheck.Core.Proofs;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    public class BeamProofTests
    {
        private static List<Tuple<double, double>> P(params double[] xf)
        {
            var l = new List<Tuple<double, double>>();
            for (int i = 0; i < xf.Length; i += 2)
                l.Add(Tuple.Create(xf[i], xf[i + 1]));
            return l;
        }

        [Fact]
        public void Simply_supported_beam_with_a_point_load_in_the_middle()
        {
            var r = BeamStatics.Solve(4, new[] { 0.0, 4.0 }, P(2, 10), 0);
            Assert.True(r.Stable);
            Assert.Equal(5, r.Reactions[0], 6);
            Assert.Equal(5, r.Reactions[1], 6);
            Assert.Equal(10, r.MaxMoment.M, 6);       // P·L/4
            Assert.Equal(5, r.MaxShear.VAbs, 6);
        }

        [Fact]
        public void Two_equal_spans_under_uniform_load_give_the_textbook_reactions_and_moments()
        {
            // q = 2 kN/m, spans 3 m: R = 3/8 qL, 10/8 qL, 3/8 qL; M support = −qL²/8; max field moment 9/128 qL²
            var r = BeamStatics.Solve(6, new[] { 0.0, 3.0, 6.0 }, P(), 2);
            Assert.Equal(0.375 * 6, r.Reactions[0], 6);
            Assert.Equal(1.25 * 6, r.Reactions[1], 6);
            Assert.Equal(-2 * 9 / 8.0, r.SupportMoments[1], 6);
            Assert.Equal(2 * 9 / 8.0, Math.Abs(r.MaxMoment.M), 6);
            Assert.Equal(9.0 / 128 * 2 * 9, r.Points.Max(p => p.M), 6);
        }

        [Fact]
        public void A_cantilever_load_beyond_the_support_lifts_the_far_support()
        {
            var r = BeamStatics.Solve(5, new[] { 0.0, 4.0 }, P(5, 10), 0);
            Assert.Equal(-2.5, r.Reactions[0], 6);
            Assert.Equal(12.5, r.Reactions[1], 6);
            Assert.Equal(-10, r.SupportMoments[1], 6);
        }

        [Fact]
        public void One_support_is_not_enough()
        {
            Assert.False(BeamStatics.Solve(3, new[] { 1.5 }, P(1, 5), 0).Stable);
        }

        [Theory]
        [InlineData("SRU 147", "", BeamProfile.Sru)]
        [InlineData("RCS 148", "", BeamProfile.Rcs)]
        [InlineData("GT 24, 3,30 m", "", BeamProfile.Gt24)]
        [InlineData("Träger VT 20K", "", BeamProfile.Vt20)]
        [InlineData("", "075330", BeamProfile.Gt24)]
        [InlineData("", "074905", BeamProfile.Vt20)]
        [InlineData("", "103877", BeamProfile.Sru)]
        [InlineData("UH 250", "", BeamProfile.Unknown)]
        public void Profiles_are_recognised_by_name_or_article(string name, string art, BeamProfile expected)
        {
            Assert.Equal(expected, BeamProofs.Recognise(name, art));
        }

        [Fact]
        public void RCS_moment_resistance_without_shear_is_the_plastic_moment_of_the_net_section()
        {
            Assert.Equal(13269.9, BeamProofs.RcsMvRd(0), 1);
            Assert.True(BeamProofs.RcsMvRd(1) < 13269.9);
        }

        private static BeamReport Report(string name, double length, double[] supports, params double[] loads)
        {
            var r = new BeamReport { Name = "B", Description = name, Length = length, SelfWeightKn = 0 };
            for (int i = 0; i < supports.Length; i++)
                r.Supports.Add(new BeamEvent { Kind = BeamEventKind.Support, Label = "S" + (i + 1), Position = supports[i] });
            for (int i = 0; i < loads.Length; i += 2)
                r.Loads.Add(new BeamEvent { Kind = BeamEventKind.Load, Position = loads[i], Fd = loads[i + 1] });
            return r;
        }

        [Fact]
        public void VT20_end_and_inner_supports_use_24_and_33_kN()
        {
            // supports 200 mm and 2800 mm from the left end of a 3000 mm girder (both outer: 200 → end, 200 from the other end → end)
            var p = BeamProofs.Check(Report("VT 20", 3000, new[] { 200.0, 2800.0 }, 1500, 10), new BeamCheckSettings());
            Assert.True(p.Done);
            var r1 = p.Checks.First(c => c.Name.StartsWith("S1"));
            Assert.Equal(24, r1.Rd);
            Assert.Equal(5, r1.Ed, 6);
            // girder: M = 10 · 2.6 / 4 = 6.5 kNm < 7.5
            var m = p.Checks.First(c => c.Name.StartsWith("m ="));
            Assert.Equal(6.5, m.Ed, 6);
            Assert.True(p.Ok);

            var q = BeamProofs.Check(Report("VT 20", 3000, new[] { 500.0, 2500.0 }, 1500, 10), new BeamCheckSettings());
            Assert.Equal(33, q.Checks.First(c => c.Name.StartsWith("S1")).Rd);
        }

        [Fact]
        public void GT24_uses_the_unfavourable_cases_unless_set_otherwise_and_fails_when_the_moment_is_too_high()
        {
            var r = Report("GT 24", 4000, new[] { 200.0, 3800.0 }, 2000, 12);   // M = 12 · 3.6 / 4 = 10.8 kNm > 10.5
            var p = BeamProofs.Check(r, new BeamCheckSettings());
            Assert.Contains(p.Checks, c => c.Name.Contains("Fall 2"));
            Assert.False(p.Ok);
            Assert.Equal(10.8 / 10.5, p.MaxEta, 6);

            var fav = BeamProofs.Check(r, new BeamCheckSettings { Gt24FavourableDiagonals = true });
            Assert.Contains(fav.Checks, c => c.Name.Contains("Fall 1"));
        }

        [Fact]
        public void SRU_is_checked_against_every_cross_section_and_the_weakest_governs()
        {
            // 20 kNm on a simply supported SRU: m = 2000 / 2518.77 (section 5-5) governs over 4-4 and 2-2
            var p = BeamProofs.Check(Report("SRU 197", 4000, new[] { 0.0, 4000.0 }, 2000, 20), new BeamCheckSettings());
            Assert.True(p.Done);
            Assert.Equal(2000 / 2518.77, p.Checks.Where(c => c.Name.StartsWith("m =")).Max(c => c.Eta), 6);
            Assert.True(p.Ok);
        }

        [Fact]
        public void A_beam_lying_on_the_ground_or_of_unknown_profile_is_not_checked()
        {
            var r = Report("SRU 147", 1472, new double[0], 500, 5);
            r.ContinuousSupport = true;
            Assert.False(BeamProofs.Check(r, new BeamCheckSettings()).Done);
            Assert.False(BeamProofs.Check(Report("HEB 200", 3000, new[] { 0.0, 3000.0 }, 1500, 5), new BeamCheckSettings()).Done);
        }
    }
}
