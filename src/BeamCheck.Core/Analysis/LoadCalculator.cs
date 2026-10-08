using System;
using System.Collections.Generic;
using System.Linq;
using BeamCheck.Core.Settings;

namespace BeamCheck.Core.Analysis
{
    public sealed class LevelLoad
    {
        public Level Level { get; set; }

        /// <summary>Tributary deck area of this level on the column, m².</summary>
        public double AreaM2 { get; set; }

        /// <summary>Service-load factor applied to this level.</summary>
        public double Factor { get; set; }

        /// <summary>Deck self-weight, kN.</summary>
        public double DeckGk { get; set; }

        /// <summary>Service load, kN.</summary>
        public double Qk { get; set; }
    }

    public sealed class PointLoad
    {
        public Column Column { get; set; }

        public string Name => Column.Name;

        /// <summary>Distance from the (possibly flipped) beam start, mm.</summary>
        public double Position { get; set; }

        /// <summary>Total tributary deck area over all levels, m².</summary>
        public double AreaM2 { get; set; }

        /// <summary>Permanent load (all self-weights), kN.</summary>
        public double Gk { get; set; }

        /// <summary>Service load, kN.</summary>
        public double Qk { get; set; }

        public double Fk => Gk + Qk;

        public double Fd { get; set; }

        public List<LevelLoad> Levels { get; } = new List<LevelLoad>();

        public bool HasEstimatedWeights { get; set; }
    }

    public sealed class BeamLoadResult
    {
        public string BeamName { get; set; }

        public double BeamLength { get; set; }

        public int LoadClass { get; set; }

        public double ServiceLoad { get; set; }

        public double GammaG { get; set; }

        public double GammaQ { get; set; }

        public bool ShowDesignValues { get; set; }

        public List<PointLoad> Points { get; } = new List<PointLoad>();

        public List<string> Warnings { get; } = new List<string>();

        public double DisplayValue(PointLoad p) => ShowDesignValues ? p.Fd : p.Fk;

        /// <summary>Chain of distances: beam start → loads → beam end (mm).</summary>
        public List<double> DimensionChain()
        {
            var stops = new List<double> { 0 };
            stops.AddRange(Points.Select(p => p.Position).OrderBy(x => x));
            stops.Add(BeamLength);
            var chain = new List<double>();
            for (int i = 1; i < stops.Count; i++)
                chain.Add(stops[i] - stops[i - 1]);
            return chain;
        }
    }

    public sealed class LoadOptions
    {
        public int LoadClass { get; set; }

        /// <summary>Level index → service-load factor. Missing levels use 0.</summary>
        public Dictionary<int, double> LevelFactors { get; set; } = new Dictionary<int, double>();

        public double GammaG { get; set; } = 1.35;

        public double GammaQ { get; set; } = 1.5;

        public bool ShowDesignValues { get; set; }

        /// <summary>Measure positions from the beam end instead of its start.</summary>
        public bool FlipBeam { get; set; }

        public string BeamName { get; set; }
    }

    public static class LoadCalculator
    {
        public const double G = 9.81;

        public static double KgToKn(double kg) => kg * G / 1000.0;

        /// <summary>
        /// Default level factors: either every level at the main factor, or the governing level
        /// (largest deck area on the selected standards) at the main factor, its neighbours at the
        /// adjacent factor and all others at the "other" factor.
        /// </summary>
        public static Dictionary<int, double> DefaultLevelFactors(Topology topo, BeamCheckSettings s)
        {
            var result = new Dictionary<int, double>();
            if (topo.Levels.Count == 0)
                return result;

            if (string.Equals(s.LevelMode, LevelModes.AllLevels, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var l in topo.Levels)
                    result[l.Index] = s.MainLevelFactor;
                return result;
            }

            var areaByLevel = topo.Levels.ToDictionary(l => l.Index, l => topo.Contributions
                .Where(c => c.Type == ContributionType.Deck && c.Level == l)
                .Sum(c => c.AreaM2));
            int governing = areaByLevel.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First().Key;

            foreach (var l in topo.Levels)
            {
                int d = Math.Abs(l.Index - governing);
                result[l.Index] = d == 0 ? s.MainLevelFactor : d == 1 ? s.AdjacentLevelFactor : s.OtherLevelFactor;
            }

            return result;
        }

        public static BeamLoadResult Calculate(Topology topo, BeamCheckSettings s, LoadOptions o)
        {
            double q = s.GetServiceLoad(o.LoadClass);
            var result = new BeamLoadResult
            {
                BeamName = o.BeamName,
                BeamLength = topo.BeamLength,
                LoadClass = o.LoadClass,
                ServiceLoad = q,
                GammaG = o.GammaG,
                GammaQ = o.GammaQ,
                ShowDesignValues = o.ShowDesignValues,
            };
            result.Warnings.AddRange(topo.Warnings);
            if (q <= 0)
                result.Warnings.Add("Класс нагрузки " + o.LoadClass + " не найден в настройках — полезная нагрузка = 0.");

            foreach (var col in topo.Columns)
            {
                var contribs = topo.Contributions.Where(c => c.Column == col).ToList();
                var p = new PointLoad
                {
                    Column = col,
                    Position = o.FlipBeam ? topo.BeamLength - col.Position : col.Position,
                    Gk = KgToKn(contribs.Sum(c => c.WeightKg)),
                    HasEstimatedWeights = contribs.Any(c => c.Source.WeightIsEstimated),
                };

                foreach (var level in topo.Levels)
                {
                    var lc = contribs.Where(c => c.Type == ContributionType.Deck && c.Level == level).ToList();
                    if (lc.Count == 0)
                        continue;
                    o.LevelFactors.TryGetValue(level.Index, out double f);
                    double area = lc.Sum(c => c.AreaM2);
                    p.Levels.Add(new LevelLoad
                    {
                        Level = level,
                        AreaM2 = area,
                        Factor = f,
                        DeckGk = KgToKn(lc.Sum(c => c.WeightKg)),
                        Qk = q * area * f,
                    });
                }

                p.AreaM2 = p.Levels.Sum(l => l.AreaM2);
                p.Qk = p.Levels.Sum(l => l.Qk);
                p.Fd = o.GammaG * p.Gk + o.GammaQ * p.Qk;
                result.Points.Add(p);
            }

            result.Points.Sort((a, b) => a.Position.CompareTo(b.Position));
            if (result.Points.Any(p => p.HasEstimatedWeights))
                result.Warnings.Add("Для части элементов вес не найден в данных чертежа — использованы значения по умолчанию из настроек.");
            return result;
        }
    }
}
