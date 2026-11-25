using System;
using System.Diagnostics;

namespace ArchSemanticPilot
{
    [Serializable]
    public struct SpatialState
    {
        public double p1, p2, p3;
        public SpatialState(double a, double b, double c)
        {
            p1 = a;
            p2 = b;
            p3 = c;
        }
        public double this[int axis]
        {
            get
            {
                return axis == 0 ? p1 : axis == 1 ? p2 : p3;
            }

            set
            {
                if (axis == 0)
                    p1 = value;
                else if (axis == 1)
                    p2 = value;
                else if (axis == 2)
                    p3 = value;
                else
                    throw new ArgumentOutOfRangeException("axis");
            }
        }
        public bool CloseTo(SpatialState b, double tolerance = 1e-9)
        {
            return Math.Abs(p1 - b.p1) < tolerance && Math.Abs(p2 - b.p2) < tolerance && Math.Abs(p3 - b.p3) < tolerance;
        }
    }
    public static class GeometryDomain
    {
        public const string ContractVersion = "CMER-cage-20261118/4";
        public const double MinimumPortal = 2.0;
        public static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
        public static double Clamp(double v)
        {
            if (!Finite(v))
                throw new ArgumentException("Non-finite input");
            return Math.Max(-1, Math.Min(1, v));
        }
        public static double Ease(double v)
        {
            double p = Clamp(v);
            return p * (3 - p * p) / 2;
        }

        public static bool IsLegal(int scenario, SpatialState p) =>
            CageRules.Legal(scenario, new[] { p.p1, p.p2, p.p3 });

        public static SpatialState SolveActive(int scenario, SpatialState previous, int axis, double requested, out string reason)
        {
            if (axis < 0 || axis > 2) throw new ArgumentOutOfRangeException(nameof(axis));
            if (!IsLegal(scenario, previous)) throw new InvalidOperationException("Previous state is not legal.");
            double target = Clamp(requested);
            var p = new[] { previous.p1, previous.p2, previous.p3 };
            var q = (double[])p.Clone(); q[axis] = target;
            var solved = CageRules.Solve(scenario, p, q, axis);
            reason = Math.Abs(solved[axis] - target) > 1e-8 ? "ACTIVE_AXIS_BOUNDARY" :
                target != requested ? "DOMAIN_CLAMP" : "NONE";
            return new SpatialState(solved[0], solved[1], solved[2]);
        }
    }
}
