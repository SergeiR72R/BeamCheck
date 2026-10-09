using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Model;
using BeamCheck.Core.Settings;

namespace BeamCheck.Core.Analysis
{
    /// <summary>
    /// State of one BEAMLOAD run: input, user choices and the current result.
    /// Lives across dialog re-openings (highlight / exclude / add objects).
    /// </summary>
    public sealed class BeamLoadSession
    {
        private bool _factorsEdited;

        public BeamLoadSession(BeamCheckSettings settings, AnalysisInput input)
        {
            Settings = settings;
            Input = input;
            Options = new LoadOptions
            {
                LoadClass = settings.DefaultLoadClass,
                GammaG = settings.GammaG,
                GammaQ = settings.GammaQ,
                ShowDesignValues = settings.ShowDesignValues,
                BeamName = input.Beam?.DisplayName,
            };
            Reanalyze();
        }

        public BeamCheckSettings Settings { get; }

        public AnalysisInput Input { get; }

        public LoadOptions Options { get; }

        public Topology Topology { get; private set; }

        public BeamLoadResult Result { get; private set; }

        /// <summary>Re-runs the geometric analysis (after exclusions/additions).</summary>
        public void Reanalyze()
        {
            Topology = new ScaffoldAnalyzer(Settings).Analyze(Input);
            var defaults = LoadCalculator.DefaultLevelFactors(Topology, Settings);
            if (_factorsEdited)
            {
                // Keep user edits for levels that still exist.
                foreach (var kv in defaults)
                    if (!Options.LevelFactors.ContainsKey(kv.Key))
                        Options.LevelFactors[kv.Key] = kv.Value;
                foreach (var k in Options.LevelFactors.Keys.Where(k => !defaults.ContainsKey(k)).ToList())
                    Options.LevelFactors.Remove(k);
            }
            else
            {
                Options.LevelFactors = defaults;
            }

            Recalculate();
        }

        public void SetLevelFactor(int levelIndex, double factor)
        {
            Options.LevelFactors[levelIndex] = factor;
            _factorsEdited = true;
            Recalculate();
        }

        public void ResetLevelFactors()
        {
            _factorsEdited = false;
            Options.LevelFactors = LoadCalculator.DefaultLevelFactors(Topology, Settings);
            Recalculate();
        }

        public void Recalculate()
        {
            Result = LoadCalculator.Calculate(Topology, Settings, Options);
        }

        public void Exclude(IEnumerable<string> ids)
        {
            int n = 0;
            foreach (var id in ids)
            {
                Input.ExcludedIds.Add(id);
                Input.ForcedIds.Remove(id);
                n++;
            }

            Reanalyze();
            StatusMessage = "Исключено из расчёта объектов: " + n + ".";
        }

        /// <summary>Message about the last manual change (shown in the dialog).</summary>
        public string StatusMessage { get; set; }

        /// <summary>Ids of everything the user added by hand (highlighted even if it carries no load).</summary>
        public ISet<string> ManualIds => Input.ForcedIds;

        /// <summary>Adds elements (or re-adds excluded ones), replacing any existing entry with the same id.</summary>
        public void Add(IEnumerable<ScaffoldElement> elements)
        {
            var list = elements.ToList();
            foreach (var e in list)
            {
                Input.ExcludedIds.Remove(e.Id);
                Input.ForcedIds.Add(e.Id);
                for (int i = Input.Candidates.Count - 1; i >= 0; i--)
                    if (Input.Candidates[i].Id == e.Id)
                        Input.Candidates.RemoveAt(i);
                Input.Candidates.Add(e);
                if (e.Kind == ElementKind.Standard && Input.SelectedStandards.All(s => s.Id != e.Id))
                    Input.SelectedStandards.Add(e);
            }

            Reanalyze();

            int used = list.Count(e => Topology.UsedElementIds.Contains(e.Id));
            var byKind = string.Join(", ", list.GroupBy(e => e.Kind).Select(g => g.Key + "×" + g.Count()));
            StatusMessage = list.Count == 0
                ? "Ничего не добавлено."
                : $"Добавлено в расчёт: {list.Count} ({byKind}); дают нагрузку на выбранные стойки: {used}." +
                  (used < list.Count ? " Остальные не связаны с выбранными стойками (леджер не доходит до выбранной стойки)." : "");
        }

        /// <summary>Area of each level summed over all selected standards, m².</summary>
        public double LevelArea(Level level) =>
            Topology.Contributions.Where(c => c.Type == ContributionType.Deck && c.Level == level).Sum(c => c.AreaM2);
    }
}
