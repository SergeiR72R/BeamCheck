using System.IO;
using System.Text;
using BeamCheck.Core.Model;
using BeamCheck.Core.Recognition;
using BeamCheck.Core.Report;
using BeamCheck.Core.Settings;
using Xunit;

namespace BeamCheck.Core.Tests
{
    public class ClassifierAndSettingsTests
    {
        [Theory]
        [InlineData("PERI_UP_UVR_200", ElementKind.Standard)]
        [InlineData("Ledger UH 150", ElementKind.Ledger)]
        [InlineData("Steel Deck UDI 25x200", ElementKind.Deck)]
        [InlineData("Diagonal UBL 200/150", ElementKind.Diagonal)]
        [InlineData("Toe board 150", ElementKind.Accessory)]
        public void Default_rules_recognise_typical_names(string blockName, ElementKind expected)
        {
            var c = new ElementClassifier(new BeamCheckSettings());
            var result = c.Classify(new ElementSignature { BlockName = blockName, Layer = "0" });
            Assert.Equal(expected, result.Kind);
        }

        [Fact]
        public void Article_and_weight_come_from_properties_then_catalogue()
        {
            var s = new BeamCheckSettings();
            s.Catalog.Add(new CatalogItem { Article = "123456", Kind = "Deck", WeightKg = 17.4 });
            var sig = new ElementSignature { BlockName = "X" };
            sig.Add("ATTR:ARTIKELNR", "123456");
            sig.Add("ATTR:GEWICHT", "16,9 kg");

            var r = new ElementClassifier(s).Classify(sig);

            Assert.Equal("123456", r.Article);
            Assert.Equal(ElementKind.Deck, r.Kind);
            Assert.Equal(17.4, r.WeightKg);
        }

        [Fact]
        public void Weight_is_parsed_with_comma_decimal()
        {
            var sig = new ElementSignature { BlockName = "Standard" };
            sig.Add("Weight", "8,35 kg");
            var r = new ElementClassifier(new BeamCheckSettings()).Classify(sig);
            Assert.Equal(8.35, r.WeightKg);
        }

        [Fact]
        public void Settings_round_trip_and_missing_fields_get_defaults()
        {
            var s = new BeamCheckSettings { DefaultLoadClass = 4, Scale = 25 };
            var json = SettingsStore.Serialize(s);
            var back = SettingsStore.Deserialize(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            Assert.Equal(4, back.DefaultLoadClass);
            Assert.Equal(25, back.Scale);
            Assert.Equal(6, back.LoadClasses.Count);

            var partial = SettingsStore.Deserialize(new MemoryStream(Encoding.UTF8.GetBytes("{\"DefaultLoadClass\":2}")));
            Assert.Equal(2, partial.DefaultLoadClass);
            Assert.Equal(1.35, partial.GammaG);
            Assert.Equal(120, partial.NodeTolerance);
        }

        [Fact]
        public void Table_and_scheme_are_built_for_the_example()
        {
            var b = ScaffoldBuilder.Example(2000, 4000);
            var session = new Analysis.BeamLoadSession(new BeamCheckSettings(), b.Input());
            var table = ResultTable.Build(session.Result, ",");
            Assert.Equal(4, table.Rows.Count);
            Assert.Equal("1423", table.Rows[0][1]);
            Assert.Contains("Стойка", table.ToCsv());

            var scheme = SchemeLayout.Build(session.Result, 125, ",");
            Assert.Equal(3, scheme.Arrows.Count);
            Assert.Equal(5, scheme.Dimensions.Count); // 4 chain segments + overall
        }
    }
}
