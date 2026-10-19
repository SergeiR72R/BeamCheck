using System;
using System.Collections.Generic;
using System.Linq;

namespace BeamCheck.Core.Proofs
{
    /// <summary>Internal forces at one point of the beam (just left and just right of a point load the shear differs).</summary>
    public sealed class ForcePoint
    {
        /// <summary>From the left edge, m.</summary>
        public double X { get; set; }

        /// <summary>Bending moment, kNm, sagging positive.</summary>
        public double M { get; set; }

        /// <summary>Largest absolute shear force at this point (left or right of it), kN.</summary>
        public double VAbs { get; set; }
    }

    public sealed class BeamStaticsResult
    {
        public bool Stable { get; set; }

        public string Message { get; set; }

        /// <summary>Reaction of every support (in the order given), kN, upward positive.</summary>
        public double[] Reactions { get; set; }

        /// <summary>Moment at every support, kNm.</summary>
        public double[] SupportMoments { get; set; }

        public List<ForcePoint> Points { get; } = new List<ForcePoint>();

        public ForcePoint MaxMoment => Points.OrderByDescending(p => Math.Abs(p.M)).FirstOrDefault();

        public ForcePoint MaxShear => Points.OrderByDescending(p => p.VAbs).FirstOrDefault();
    }

    /// <summary>
    /// A straight beam of constant stiffness on pin supports (continuous over all of them), with point loads and a uniform load
    /// (own weight). Linear elastic, Euler–Bernoulli elements between all supports and loads. Reactions and internal forces of a beam
    /// with constant EI do not depend on the value of EI, so none is needed.
    /// </summary>
    public static class BeamStatics
    {
        /// <param name="length">Beam length, m.</param>
        /// <param name="supports">Support positions from the left edge, m.</param>
        /// <param name="loads">Point loads (position m, force kN downward).</param>
        /// <param name="q">Uniform load over the whole length, kN/m downward.</param>
        public static BeamStaticsResult Solve(double length, IList<double> supports, IList<Tuple<double, double>> loads, double q)
        {
            var result = new BeamStaticsResult { Reactions = new double[supports.Count], SupportMoments = new double[supports.Count] };
            if (supports.Count == 0 || supports.Max() - supports.Min() < 0.01)
            {
                result.Stable = false;
                result.Message = supports.Count == 0 ? "kein Auflager" : "nur ein Auflager — statisch nicht bestimmbar (Kragträger ohne Einspannung)";
                return result;
            }

            // Nodes: the ends, the supports and the loads (merged within 1 mm).
            var xs = new List<double> { 0, length };
            xs.AddRange(supports.Select(s => Clamp(s, length)));
            xs.AddRange(loads.Select(l => Clamp(l.Item1, length)));
            xs.Sort();
            var nodes = new List<double>();
            foreach (var x in xs)
                if (nodes.Count == 0 || x - nodes[nodes.Count - 1] > 0.001)
                    nodes.Add(x);
            int Node(double x)
            {
                x = Clamp(x, length);
                int best = 0;
                for (int i = 1; i < nodes.Count; i++)
                    if (Math.Abs(nodes[i] - x) < Math.Abs(nodes[best] - x))
                        best = i;
                return best;
            }

            int n = nodes.Count, dof = 2 * n;
            var K = new double[dof, dof];
            var F = new double[dof]; // upward / counter-clockwise positive
            for (int e = 0; e < n - 1; e++)
            {
                double l = nodes[e + 1] - nodes[e];
                var k = ElementStiffness(l);
                int[] map = { 2 * e, 2 * e + 1, 2 * e + 2, 2 * e + 3 };
                for (int a = 0; a < 4; a++)
                    for (int b = 0; b < 4; b++)
                        K[map[a], map[b]] += k[a, b];
                var f0 = FixedEndLoads(l, q);
                for (int a = 0; a < 4; a++)
                    F[map[a]] += f0[a];
            }

            foreach (var ld in loads)
                F[2 * Node(ld.Item1)] -= ld.Item2;

            var supportNodes = supports.Select(Node).ToArray();
            var fixedDof = new HashSet<int>(supportNodes.Select(i => 2 * i));
            var free = Enumerable.Range(0, dof).Where(i => !fixedDof.Contains(i)).ToArray();
            var A = new double[free.Length, free.Length];
            var rhs = new double[free.Length];
            for (int i = 0; i < free.Length; i++)
            {
                rhs[i] = F[free[i]];
                for (int j = 0; j < free.Length; j++)
                    A[i, j] = K[free[i], free[j]];
            }

            var dFree = SolveLinear(A, rhs);
            if (dFree == null)
            {
                result.Stable = false;
                result.Message = "statisches System instabil";
                return result;
            }

            var d = new double[dof];
            for (int i = 0; i < free.Length; i++)
                d[free[i]] = dFree[i];

            // Reactions: K d − F at the fixed degrees of freedom (several supports on one node share it).
            var nodeReaction = new Dictionary<int, double>();
            foreach (int sn in supportNodes.Distinct())
            {
                double r = -F[2 * sn];
                for (int j = 0; j < dof; j++)
                    r += K[2 * sn, j] * d[j];
                nodeReaction[sn] = r;
            }

            for (int i = 0; i < supports.Count; i++)
                result.Reactions[i] = nodeReaction[supportNodes[i]] / supportNodes.Count(x => x == supportNodes[i]);

            // Internal forces element by element: end forces f = k·d − f0; M(x) = −M1 + V1·x − q·x²/2, V(x) = V1 − q·x.
            var momentAtNode = new double[n];
            for (int e = 0; e < n - 1; e++)
            {
                double l = nodes[e + 1] - nodes[e];
                var k = ElementStiffness(l);
                var f0 = FixedEndLoads(l, q);
                var de = new[] { d[2 * e], d[2 * e + 1], d[2 * e + 2], d[2 * e + 3] };
                var fe = new double[4];
                for (int a = 0; a < 4; a++)
                {
                    for (int b = 0; b < 4; b++)
                        fe[a] += k[a, b] * de[b];
                    fe[a] -= f0[a];
                }

                double v1 = fe[0], m1 = fe[1];
                double M(double x) => -m1 + v1 * x - q * x * x / 2;
                double V(double x) => v1 - q * x;
                var samples = new List<double> { 0, l };
                if (q > 1e-12 && v1 / q > 0 && v1 / q < l)
                    samples.Add(v1 / q);
                foreach (var x in samples)
                    result.Points.Add(new ForcePoint { X = nodes[e] + x, M = M(x), VAbs = Math.Abs(V(x)) });
                momentAtNode[e] = M(0);
                momentAtNode[e + 1] = M(l);
            }

            for (int i = 0; i < supports.Count; i++)
                result.SupportMoments[i] = momentAtNode[supportNodes[i]];
            result.Points.Sort((a, b) => a.X.CompareTo(b.X));
            result.Stable = true;
            return result;
        }

        private static double Clamp(double x, double length) => Math.Max(0, Math.Min(length, x));

        private static double[,] ElementStiffness(double l)
        {
            double l2 = l * l, c = 1 / (l2 * l);
            return new[,]
            {
                { 12 * c, 6 * l * c, -12 * c, 6 * l * c },
                { 6 * l * c, 4 * l2 * c, -6 * l * c, 2 * l2 * c },
                { -12 * c, -6 * l * c, 12 * c, -6 * l * c },
                { 6 * l * c, 2 * l2 * c, -6 * l * c, 4 * l2 * c },
            };
        }

        /// <summary>Equivalent nodal loads of a downward uniform load q (upward / counter-clockwise positive).</summary>
        private static double[] FixedEndLoads(double l, double q) => new[] { -q * l / 2, -q * l * l / 12, -q * l / 2, q * l * l / 12 };

        /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
        private static double[] SolveLinear(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone();
            var x = (double[])b.Clone();
            double scale = 0;
            foreach (var v in m)
                scale = Math.Max(scale, Math.Abs(v));
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col]))
                        piv = r;
                if (Math.Abs(m[piv, col]) < 1e-12 * Math.Max(scale, 1e-30))
                    return null;
                if (piv != col)
                {
                    for (int c = 0; c < n; c++)
                    {
                        var t = m[col, c];
                        m[col, c] = m[piv, c];
                        m[piv, c] = t;
                    }

                    var tb = x[col];
                    x[col] = x[piv];
                    x[piv] = tb;
                }

                for (int r = col + 1; r < n; r++)
                {
                    double f = m[r, col] / m[col, col];
                    if (f == 0)
                        continue;
                    for (int c = col; c < n; c++)
                        m[r, c] -= f * m[col, c];
                    x[r] -= f * x[col];
                }
            }

            for (int r = n - 1; r >= 0; r--)
            {
                double s = x[r];
                for (int c = r + 1; c < n; c++)
                    s -= m[r, c] * x[c];
                x[r] = s / m[r, r];
            }

            return x;
        }
    }
}
