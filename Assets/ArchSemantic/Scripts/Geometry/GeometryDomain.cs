using System;
using System.Diagnostics;

namespace ArchSemanticPilot
{
    /// <summary>
    /// Three-dimensional semantic parameter state (P1/P2/P3).
    /// </summary>
    [Serializable]
    public struct SpatialState
    {
        /// <summary>Parameter P1.</summary>
        public double p1, p2, p3;

        /// <summary>
        /// Construct a state.
        /// </summary>
        /// <param name="a">P1 value.</param>
        /// <param name="b">P2 value.</param>
        /// <param name="c">P3 value.</param>
        public SpatialState(double a, double b, double c)
        {
            p1 = a;
            p2 = b;
            p3 = c;
        }

        /// <summary>
        /// Access parameters by axis index: 0=P1, 1=P2, 2=P3.
        /// </summary>
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

        /// <summary>
        /// Check whether two states agree within the tolerance.
        /// </summary>
        /// <param name="b">State to compare.</param>
        /// <param name="tolerance">Comparison tolerance.</param>
        /// <returns>True when all three axes agree within tolerance.</returns>
        public bool CloseTo(SpatialState b, double tolerance = 1e-9)
        {
            return Math.Abs(p1 - b.p1) < tolerance && Math.Abs(p2 - b.p2) < tolerance && Math.Abs(p3 - b.p3) < tolerance;
        }
    }

    /// <summary>
    /// Geometry domain and validity rules:
    /// - Parameter clamping and easing;
    /// - Mapping semantic parameters to geometry dimensions;
    /// - Checking regulatory and experimental contract constraints;
    /// - Solving reachability along the active axis.
    /// </summary>
    public static class GeometryDomain
    {
        /// <summary>Geometry contract version for logs, checkpoints and recovery consistency checks.</summary>
        public const string ContractVersion = "CMER-0.9.13-fixed-group-remote-views";

        /// <summary>Minimum final clear width of shared portals, in metres.</summary>
        public const double MinimumPortal = 2.0;

        /// <summary>
        /// Check that a value is finite, excluding NaN and Infinity.
        /// </summary>
        /// <param name="v">Input value.</param>
        /// <returns>True for a finite value.</returns>
        public static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>
        /// Clamp the parameter to [-1, 1].
        /// </summary>
        public static double Clamp(double v)
        {
            if (!Finite(v))
                throw new ArgumentException("Non-finite input");
            return Math.Max(-1, Math.Min(1, v));
        }

        /// <summary>
        /// Map a linear parameter to a smoothly eased control value.
        /// </summary>
        /// <param name="v">Raw parameter.</param>
        /// <returns>Eased parameter.</returns>
        public static double Ease(double v)
        {
            double p = Clamp(v);
            return p * (3 - p * p) / 2;
        }

        /// <summary>
        /// Compute key geometry dimensions from the scenario and semantic parameters.
        /// Returned array index convention:
        /// [0..2]=q1/q2/q3; [3..4]=cW/cH; [5..12]=boundaries; [13..14]=upper-level depths; [15..18]=storey heights and roof.
        /// </summary>
        /// <param name="scenario">Scenario index (0=A, 1=B).</param>
        /// <param name="p">Input parameter state.</param>
        /// <returns>Array of key dimensions.</returns>
        public static double[] Dimensions(int scenario, SpatialState p)
        {
            string sid = scenario == 0 ? "A" : "B";
            double p1 = p.p1, p2 = p.p2, p3 = p.p3;
            double q1 = Ease(p1), q2 = Ease(p2), q3 = Ease(p3);
            double cW, cH, fLeft, fBottom, rLeft, rSouth, gSouth, gWest, gEast, gNorth;
          
            double uRDepth = 3.60 - .12 * q1 - .32 * q2 + .16 * q3;
            double uGDepth = 3.60 + .22 * q1 - .12 * q2 + .25 * q3;
            if (sid == "A")
            {
                const double areaF = 109.90, areaR = 115.12, areaG = 226.00;
                // P1 F width: 8.93 / 10.00 / 11.36 m; F depth follows fixed area.
                cW = 10.00 + (q1 < 0 ? 1.07 * q1 : 1.36 * q1) - .80 * q2 - .60 * q3;
                cH = 10.00 - 1.00 * q1 + 1.00 * q2 + .65 * q3;
                fLeft = 0.00;
                fBottom = -areaF / cW;
                // P2 isolated R depth: 13.89 / 14.39 / 14.89 m.
                rSouth = -4.39 - .60 * q1 + .50 * q2 + .30 * q3;
                rLeft = -areaR / (cH - rSouth);
                // P3 changes named G endpoints while exact L-shaped area remains 226 m2.
                gSouth = -4.39 - .70 * q1 + .25 * q2 + 1.10 * q3;
                gWest = -4.00 - .60 * q1 + .25 * q2 - 1.00 * q3;
                gNorth = cH + 5.04 + .50 * q1 - .30 * q2 + .45 * q3;
                gEast = (areaG + cW * (cH - gSouth) + gWest * (gNorth - cH)) / (gNorth - gSouth);
            }
            else
            {
                const double areaF = 109.50, areaR = 115.00, areaG = 226.00;
                cW = 7.00 + .55 * q1 - .45 * q2 - .30 * q3;
                cH = 14.00 - 1.10 * q1 + .90 * q2 + .60 * q3;
                // P1 F depth: 8.18 / 7.30 / 6.42 m; F width follows fixed area.
                double fDepth = 7.30 - .88 * q1 - .25 * q2 - .20 * q3;
                fBottom = -fDepth;
                fLeft = cW - areaF / fDepth;
                // P2 isolated R depth: 13.10 / 14.00 / 14.90 m.
                rSouth = 0.00;
                rLeft = -areaR / cH;
                gSouth = -4.10 - .70 * q1 + .25 * q2 + 1.10 * q3;
                gWest = -4.40 - .60 * q1 + .25 * q2 - 1.00 * q3;
                gNorth = cH + 5.3967391304 + .50 * q1 - .30 * q2 + .45 * q3;
                gEast = (areaG + cW * (cH - gSouth) + gWest * (gNorth - cH)) / (gNorth - gSouth);
            }

            // Clear heights respond within each semantic axis and remain part of the same coupled state.
            // F height compensates R/G height changes so total enclosed F+R+G volume remains constant.
            double areaFHeight = sid == "A" ? 109.90 : 109.50, areaRHeight = sid == "A" ? 115.12 : 115.00, areaGHeight = 226.00;
            double zR = 3.60 - .15 * q1 - .40 * q2 + .10 * q3;
            double zG = 6.00 - .25 * q1 - .10 * q2 + .35 * q3;
            double baseVolume = areaFHeight * 8.40 + areaRHeight * 3.60 + areaGHeight * 6.00;
            double zF = (baseVolume - areaRHeight * zR - areaGHeight * zG) / areaFHeight;
            double zRoof = System.Math.Max(9.00, System.Math.Max(zF + .60, zG + 2.85));
            return new[]
            {
                q1,
                q2,
                q3,
                cW,
                cH,
                fLeft,
                fBottom,
                rLeft,
                rSouth,
                gSouth,
                gWest,
                gEast,
                gNorth,
                uRDepth,
                uGDepth,
                zR,
                zG,
                zF,
                zRoof
            };
        }

        /// <summary>
        /// Taper mapping parameters: plan scaling and vertical compensation.
        /// </summary>
        public struct Mapping
        {
            public double k, a, verticalScale, targetVolume;

            /// <summary>
            /// Map an original height to its compensated height.
            /// </summary>
            /// <param name="z">Original height.</param>
            /// <returns>Mapped height.</returns>
            public double Z(double z)
            {
                return verticalScale * a * z / (1 + k * z);
            }

            /// <summary>
            /// Compute the plan scale factor at a given height.
            /// </summary>
            /// <param name="z">Height.</param>
            /// <returns>Plan scale factor.</returns>
            public double PlanScale(double z)
            {
                return 1 / (1 + k * z);
            }
        }

        /// <summary>
        /// Compute the taper mapping so the projected total volume matches the target.
        /// </summary>
        /// <param name="scenario">Scenario index (0=A, 1=B).</param>
        /// <param name="d">Dimension array returned by Dimensions.</param>
        /// <returns>Mapping parameters.</returns>
        public static Mapping Taper(int scenario, double[] d)
        {
            double topScale = 1 + Math.Max(-.18, Math.Min(.18, .10 * d[0] - .10 * d[1] + .08 * d[2]));
            double k = (1 / topScale - 1) / (d[18] + .18), a = 1 + k * (d[18] + .18);
            double f = scenario == 0 ? 109.90 : 109.50, r = scenario == 0 ? 115.12 : 115.00;
            double target = f * 8.4 + r * 3.6 + 226 * 6;
            Func<double, double, double> volume = (area, h) =>
            {
                double scale = 1 / (1 + k * h), z = a * h / (1 + k * h);
                return area * z * (1 + scale + scale * scale) / 3;
            };
            double actual = volume(f, d[17]) + volume(r, d[15]) + volume(226, d[16]);
            return new Mapping
            {
                k = k,
                a = a,
                verticalScale = target / actual,
                targetVolume = target
            };
        }

        /// <summary>
        /// Check that a parameter state is fully valid in the specified scenario.
        /// </summary>
        /// <param name="scenario">Scenario index.</param>
        /// <param name="p">State to check.</param>
        /// <returns>True when valid.</returns>
        public static bool IsLegal(int scenario, SpatialState p)
        {
            if (!Finite(p.p1) || !Finite(p.p2) || !Finite(p.p3) || Math.Abs(p.p1) > 1 || Math.Abs(p.p2) > 1 || Math.Abs(p.p3) > 1)
                return false;
            return LegalDimensions(scenario, Dimensions(scenario, p));
        }

        /// <summary>
        /// Internal dimension-level validity check.
        /// Checks plan proportions, boundary relationships, clear heights, volume conservation and portal clear widths.
        /// </summary>
        /// <param name="scenario">Scenario index.</param>
        /// <param name="d">Key dimension array.</param>
        /// <returns>True when all constraints pass.</returns>
        static bool LegalDimensions(int scenario, double[] d)
        {
            string sid = scenario == 0 ? "A" : "B";
            double cW = d[3], cH = d[4], fLeft = d[5], fBottom = d[6], rLeft = d[7], rSouth = d[8];
            double gSouth = d[9], gWest = d[10], gEast = d[11], gNorth = d[12], uR = d[13], uG = d[14];
            double zR = d[15], zG = d[16], zF = d[17], zRoof = d[18];
            double rW = -rLeft, rH = cH - rSouth, rAspect = System.Math.Max(rW, rH) / System.Math.Max(.001, System.Math.Min(rW, rH));
            double fW = cW - fLeft, fH = -fBottom, fAspect = System.Math.Max(fW, fH) / System.Math.Max(.001, System.Math.Min(fW, fH));
            double cArea = cW * cH, cArea0 = sid == "A" ? 100.0 : 98.0;
            double areaFHeight = sid == "A" ? 109.90 : 109.50, areaRHeight = sid == "A" ? 115.12 : 115.00, areaGHeight = 226.00;
            double volume0 = areaFHeight * 8.40 + areaRHeight * 3.60 + areaGHeight * 6.00;
            double volumeNow = areaFHeight * zF + areaRHeight * zR + areaGHeight * zG;
            bool nominal = cW >= 6.0 && cH >= 6.0 && cArea >= .90 * cArea0 && cArea <= 1.10 * cArea0 && fAspect <= 2.70 && rAspect >= 1.40 && rAspect <= 2.10 && gEast - cW >= 6.0 && gNorth - cH >= 4.50 && cH - gSouth >= 9.0 && gWest < 0 && gSouth < 0 && gEast > cW && gNorth > cH && uR >= 3.0 && uR <= 4.2 && uG >= 3.0 && uG <= 4.2 && zR >= 3.00 && zR <= 4.30 && zG - zR - .30 >= 1.80 - 1e-9 && zRoof - zG - .30 >= 2.25 - 1e-9 && zF >= 7.00 && zF <= 10.00 && zF - zG >= 1.10 && System.Math.Abs(volumeNow - volume0) <= .001;
            var map = Taper(scenario, d);
            double floor = map.Z(.06), rClear = map.Z(zR - .05) - floor;
            double u1Clear = map.Z(zG - .05) - map.Z(zR + .30);
            double u2Clear = map.Z(zRoof - .05) - map.Z(zG + .30);
            double maxRaw = 2.02 / Math.Min(1, map.PlanScale(zRoof));
            // Shared hosts must accommodate a complete clear portal and edge returns.
            return nominal && rClear >= 3 && u1Clear >= 1.80 && u2Clear >= 2.25 && map.Z(zF - .05) - floor >= 7 && Math.Abs(gWest) >= maxRaw + .40 && (sid == "A" ? -rSouth : -fLeft) >= maxRaw + .40 && -gSouth >= maxRaw + .40;
        }

        /// <summary>
        /// Change only the active axis, approaching the requested value within the connected valid interval from the committed valid state.
        /// </summary>
        /// <param name="scenario">Scenario index.</param>
        /// <param name="previous">Current committed valid state.</param>
        /// <param name="axis">Active axis (0/1/2).</param>
        /// <param name="requested">Requested target value.</param>
        /// <param name="reason">Returned projection reason (NONE / DOMAIN_CLAMP / ACTIVE_AXIS_BOUNDARY).</param>
        /// <returns>Reachable effective state.</returns>
        public static SpatialState SolveActive(int scenario, SpatialState previous, int axis, double requested, out string reason)
        {
            if (axis < 0 || axis > 2)
                throw new ArgumentOutOfRangeException("axis");
            if (!IsLegal(scenario, previous))
                throw new InvalidOperationException("Previous state is not legal");
            double target = Clamp(requested);
            var result = previous;
            double from = previous[axis];
            reason = target == requested ? "NONE" : "DOMAIN_CLAMP";
            // Follow the connected legal interval from the committed state; never change other axes.
            double last = from;
            for (int step = 1; step <= 128; step++)
            {
                double next = from + (target - from) * step / 128.0;
                result[axis] = next;
                if (!IsLegal(scenario, result))
                {
                    double lo = last, hi = next;
                    for (int n = 0; n < 44; n++)
                    {
                        double mid = (lo + hi) * .5;
                        result[axis] = mid;
                        if (IsLegal(scenario, result))
                            lo = mid;
                        else
                            hi = mid;
                    }

                    result = previous;
                    result[axis] = lo;
                    reason = "ACTIVE_AXIS_BOUNDARY";
                    return result;
                }

                last = next;
            }

            result = previous;
            result[axis] = target;
            return result;
        }
    }
}
