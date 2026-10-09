using System.Linq;
using BeamCheck.Core.Analysis;
using BeamCheck.Core.Geometry;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    /// <summary>Decks must lie within 50 mm of the ledger (plan) and within ±50 mm of the model's normal seat height.</summary>
    public class LooseDeckTests
    {
        private const double ExactArea = 0.183 + (0.183 + 0.2745) + 0.2745;

        private static double TotalArea(out Topology topo, double raiseAll = 0, double shiftX = 0, int oddDecks = 0, double oddRaise = 0, bool snap = false, bool forceAll = false)
        {
            var b = ScaffoldBuilder.Example(2000);
            int i = 0;
            foreach (var d in b.Elements.Where(e => e.Kind == ElementKind.Deck))
            {
                double raise = raiseAll + (i++ < oddDecks ? oddRaise : 0);
                d.ZMin += raise;
                d.ZMax += raise;
                d.Start = new Vec3(d.Start.X + shiftX, d.Start.Y, d.Start.Z + raise);
                d.End = new Vec3(d.End.X + shiftX, d.End.Y, d.End.Z + raise);
            }

            var input = b.Input();
            if (forceAll)
                foreach (var d in b.Elements.Where(e => e.Kind == ElementKind.Deck))
                    input.ForcedIds.Add(d.Id);
            var s = new BeamLoadSession(new BeamCheckSettings { DeckSnapEnabled = snap }, input);
            topo = s.Topology;
            return s.Result.Points.Sum(p => p.AreaM2);
        }

        [Fact]
        public void A_deck_resting_exactly_is_matched()
        {
            Assert.Equal(ExactArea, TotalArea(out var t), 6);
            Assert.DoesNotContain(t.Warnings, w => w.Contains("без леджера"));
        }

        [Fact]
        public void The_seat_height_is_learned_from_the_model_so_a_uniform_offset_is_fine()
        {
            Assert.Equal(ExactArea, TotalArea(out var t, raiseAll: 120), 6);
            Assert.Contains(t.Warnings, w => w.Contains("Посадка деков") && w.Contains("130"));
        }

        [Fact]
        public void A_deck_off_by_more_than_5_cm_in_height_from_the_others_is_rejected_and_reported()
        {
            double a = TotalArea(out var t, oddDecks: 1, oddRaise: 80);

            Assert.True(a < ExactArea);
            Assert.Contains(t.Warnings, w => w.Contains("без леджера") && w.Contains("+80"));
        }

        [Fact]
        public void A_deck_within_5_cm_in_height_is_accepted()
        {
            Assert.Equal(ExactArea, TotalArea(out _, oddDecks: 1, oddRaise: 40), 6);
        }

        [Fact]
        public void Plan_distance_is_limited_to_5_cm()
        {
            Assert.Equal(ExactArea, TotalArea(out _, shiftX: 45), 6);

            double far = TotalArea(out var t, shiftX: 80);
            Assert.Equal(0, far, 9);
            Assert.Contains(t.Warnings, w => w.Contains("без леджера под опорой") && w.Contains("80"));
        }

        [Fact]
        public void With_snapping_decks_that_miss_the_ledger_are_attached_to_the_nearest_one()
        {
            Assert.Equal(ExactArea, TotalArea(out var t, shiftX: 80, snap: true), 6);
            Assert.Contains(t.Warnings, w => w.Contains("привязаны к ближайшему леджеру") && w.Contains("80"));
        }

        [Fact]
        public void Snapping_has_its_own_limit()
        {
            Assert.Equal(0, TotalArea(out _, shiftX: 400, snap: true), 9);
        }

        [Fact]
        public void A_deck_the_user_added_by_hand_is_attached_even_when_far_away()
        {
            Assert.Equal(ExactArea, TotalArea(out _, shiftX: 400, snap: false, forceAll: true), 6);
            Assert.Equal(ExactArea, TotalArea(out _, oddDecks: 1, oddRaise: 700, snap: false, forceAll: true), 6);
        }

        [Fact]
        public void Adding_elements_reports_how_many_carry_load_and_marks_them_as_manual()
        {
            var b = ScaffoldBuilder.Example(2000);
            var input = b.Input();
            var deck = b.Elements.First(e => e.Kind == ElementKind.Deck);
            input.ExcludedIds.Add(deck.Id);
            var s = new BeamLoadSession(new BeamCheckSettings { DeckSnapEnabled = false }, input);
            double before = s.Result.Points.Sum(p => p.AreaM2);

            s.Add(new[] { deck });

            Assert.True(s.Result.Points.Sum(p => p.AreaM2) > before);
            Assert.Contains(deck.Id, s.ManualIds);
            Assert.Contains("Добавлено в расчёт: 1", s.StatusMessage);
            Assert.Contains("дают нагрузку на выбранные стойки: 1", s.StatusMessage);
        }
    }
}
