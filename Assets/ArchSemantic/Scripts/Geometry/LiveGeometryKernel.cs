using System;
using System.Linq;
using System.Collections.Generic;
using ArchSemanticGeometry;

namespace ArchSemanticPilot
{
    // Live geometry kernel: transform semantic parameters P1/P2/P3 and scenario A/B into complete renderable, validatable and navigable geometry.
    // This kernel computes and constructs geometry without managing Unity rendering threads or interaction state machines.
    public static class LiveGeometryKernel
    {
        // Source Grasshopper design hash for provenance verification.
        public const string SourceGhSha256 = "7f60fba3f7c5d7c112061a89b87ab4450111e7ca36369729cbeefb90891726e6";

        // Geometry result consumed by LiveGeometryDriver and converted to Unity meshes.
        public sealed class Result
        {
            // All solid geometry, including walls, slabs, doors, windows, lighting and displays.
            public List<GeometryBase> solids;
            // Semantic names corresponding one-to-one with solids: Scenario__Class__Owner__Role__Index.
            public List<string> names;
            // Anchors and start points for navigation, prompts and interaction.
            public List<Point3d> anchors, starts;
            public List<string> anchorNames;
            // Default viewing direction lines for the main spaces.
            public List<LineCurve> views;
            // Effective parameter values recorded in the output.
            public double p1, p2, p3, groundFloorHeight;
            // Actual shared-portal clear widths after taper and their semantic owners.
            public double[] portalWidths;
            public string[] portalOwners;
            // Target volume for outer-layer volume-contract validation.
            public double nominalVolume;
            // Projection or constraint description; usually NONE here because GeometryDomain handles constraints.
            public string projection;
        }

        /// <summary>
        /// Main entry point constructing complete A/B geometry for the given parameters (data source).
        /// </summary>
        /// <param name="Scenario">
        /// Scenario index: 0 for A, 1 for B.
        /// </param>
        /// <param name="P1">P1 arrival/reveal parameter, normally in [-1, 1].</param>
        /// <param name="P2">P2 openness/refuge parameter, normally in [-1, 1].</param>
        /// <param name="P3">P3 enclosure/continuity parameter, normally in [-1, 1].</param>
        /// <returns>
        /// Complete geometry result with solids, semantic names, navigation anchors, viewing lines, portal widths and target volume.
        /// </returns>
        public static Result Evaluate(int Scenario, double P1, double P2, double P3)
        {
            // The source GH hash records provenance. Runtime contract 0.9 has its own geometry rules.
            if (Scenario < 0 || Scenario > 1)
                throw new ArgumentOutOfRangeException("Scenario");
            string sid = Scenario == 0 ? "A" : "B";
            var state = new SpatialState(P1, P2, P3);
            if (!GeometryDomain.IsLegal(Scenario, state))
                throw new InvalidOperationException("Illegal state: solve on the active axis before reconstruction");
            double[] dims = GeometryDomain.Dimensions(Scenario, state);
            double effectiveP1 = P1, effectiveP2 = P2, effectiveP3 = P3;
            var volumeMap = GeometryDomain.Taper(Scenario, dims);
            // dims contains normalized parameters and key dimensions: courtyard width and height, region boundaries, upper-level depths and elevations.
            double q1 = dims[0], q2 = dims[1], q3 = dims[2], cW = dims[3], cH = dims[4];
            double fLeft = dims[5], fBottom = dims[6], rLeft = dims[7], rSouth = dims[8];
            double gSouth = dims[9], gWest = dims[10], gEast = dims[11], gNorth = dims[12];
            double uRDepth = dims[13], uGDepth = dims[14];
            // Clip upper footprints to the actual supporting fields.  This preserves the
            // supported upper footprints. Reachable parameter values still obey GeometryDomain.
            double uRSupportWest = sid == "A" ? rLeft : Math.Max(rLeft, fLeft);
            double uRSupportSouth = sid == "A" ? rSouth : fBottom;
            double uRWest = Math.Max(-uRDepth, uRSupportWest);
            double uRSouth = Math.Max(-4.0, uRSupportSouth);
            double uGWest = Math.Max(-uRDepth, gWest);
            double uGSouth = Math.Max(-4.0, gSouth);
            double uGEast = Math.Min(cW + uGDepth, gEast);
            double uGNorth = Math.Min(cH + uGDepth, gNorth);
            // These semantic cue variables drive local displacement, angles and clear widths rather than directly representing final geometry.
            // Semantic cues adapted from the public diagram; final dimensions include domain constraints and volume compensation.
            double p1ThresholdDelta = -1.20 * q1; // +1.20 delayed -> -1.20 m direct
            double p1RevealAngleDeg = 15.0 * (1.0 - q1); // reveal wall: 30 -> 15 -> 0 degrees
            double p1FCClearWidth = 2.40 + (q1 < 0 ? .20 : .80) * q1; // 2.20 -> 2.40 -> 3.20 m before taper
            double p2AnchorOffset = .50 * q2; // toward -0.50 -> away +0.50 m
            double p2ScreenAngleDeg = 12.0 * q2; // shelter screen: -12 -> 0 -> +12 degrees
            double p2RCViewWidth = 4.00 - 1.60 * q2; // 5.60 -> 4.00 -> 2.40 m
            double p3BoundaryTravel = 1.10 * Math.Abs(q3);
            double p3RouteOffset = .50 * (1.0 - q3); // 1.00 -> 0.50 -> 0.00 m
            double p3DisplayAngleDeg = 8.0 * (1.0 - q3); // display wall: 16 -> 8 -> 0 degrees
            double p3SharedClearWidth = 2.40 + (q3 < 0 ? .20 : .60) * q3; // 2.20 -> 2.40 -> 3.00 m before taper
            double p1TiltDeg = 8.0 * (1.0 - q1), p2TiltDeg = 12.0 * q2, p3TiltDeg = 6.0 * (1.0 - q3);
            double p1WallHeight = 3.20 + .40 * q1, p2WallHeight = 2.40 + .30 * q2, p3WallHeight = 2.80 + .35 * q3;
            const double WALL_T = .20, FRAME_W = .06, GLASS_T = .025;
            const double VISIBLE_SLAB_T = .18, FLOOR_BUILDUP_T = .12, RING_INSET = .60, RING_W = .08, SKYLIGHT_CURB = .12;
            double Z_R = dims[15], Z_R_FINISH = Z_R + .30, Z_G = dims[16], Z_UPPER_FINISH = Z_G + .30, Z_F = dims[17], Z_ROOF = dims[18];
            // Roofs, floors, glazing and observer anchors follow the same live height state.
            // The upper datum rises when F or G needs clearance; it is not fixed at 9 m.
            double F_TOP = Z_F, TOP_WINDOW_HEAD = Z_ROOF - .35;
            double F_SHARED_UPPER_HEAD = Math.Min(Z_F - .35, TOP_WINDOW_HEAD);
            var slabs = new List<Solid>();
            var walls = new List<Solid>();
            var glass = new List<Solid>();
            var frames = new List<Solid>();
            var doors = new List<Solid>();
            var ceilings = new List<Solid>();
            var fixtures = new List<Solid>();
            var lights = new List<Solid>();
            var interfaces = new List<Curve>();
            var joints = new List<Solid>();
            var anchorPoints = new List<Point3d>();
            var anchorNames = new List<string>();
            var allFormal = new List<GeometryBase>();
            var semanticNames = new List<string>();
            var counters = new Dictionary<string, int>();
            // Rect/Area/CCW/CloseAt/Extrude are shared polygon utilities for slabs, walls, openings and skylights.
            Func<double, double, double, double, List<Point3d>> Rect = (x0, y0, x1, y1) => new List<Point3d>
            {
                new Point3d(x0, y0, 0),
                new Point3d(x1, y0, 0),
                new Point3d(x1, y1, 0),
                new Point3d(x0, y1, 0)
            };
            Func<List<Point3d>, double> Area = pts =>
            {
                double a = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = pts[i];
                    var n = pts[(i + 1) % pts.Count];
                    a += p.X * n.Y - n.X * p.Y;
                }

                return Math.Abs(a * .5);
            };
            Func<List<Point3d>, List<Point3d>> CCW = pts =>
            {
                double a = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = pts[i];
                    var n = pts[(i + 1) % pts.Count];
                    a += p.X * n.Y - n.X * p.Y;
                }

                var r = new List<Point3d>(pts);
                if (a < 0)
                    r.Reverse();
                return r;
            };
            Func<List<Point3d>, double, PolylineCurve> CloseAt = (pts, z) =>
            {
                var q = CCW(pts);
                var p = new List<Point3d>();
                foreach (var v in q)
                    p.Add(new Point3d(v.X, v.Y, z));
                p.Add(p[0]);
                return new PolylineCurve(p);
            };
            Func<List<Point3d>, double, double, Solid> Extrude = (pts, z, h) =>
            {
                if (pts == null || pts.Count < 3 || h <= 1e-7 || Area(pts) <= 1e-8)
                    return null;
                var ex = Extrusion.Create(CloseAt(pts, z), h, true);
                return ex == null ? null : ex.ToBrep();
            };
            Func<Point3d, Point3d, double, double, double, double, Solid> SegmentBox = (a, b, u0, u1, z0, z1) =>
            {
                var dir = b - a;
                double len = dir.Length;
                if (len < 1e-8 || u1 - u0 < 1e-8 || z1 - z0 < 1e-8)
                    return null;
                dir.Unitize();
                var p0 = a + dir * u0;
                var p1 = a + dir * u1;
                var x = p1 - p0;
                double segLen = x.Length;
                x.Unitize();
                var y = new Vector3d(-x.Y, x.X, 0);
                y.Unitize();
                var plane = new Plane(new Point3d((p0.X + p1.X) * .5, (p0.Y + p1.Y) * .5, z0), x, y);
                return new Box(plane, new Interval(-segLen * .5, segLen * .5), new Interval(-WALL_T * .5, WALL_T * .5), new Interval(0, z1 - z0)).ToBrep();
            };
            Func<Point3d, Point3d, double, double, double, double, double, Solid> ThinSegmentBox = (a, b, u0, u1, z0, z1, depth) =>
            {
                var dir = b - a;
                double len = dir.Length;
                if (len < 1e-8 || u1 - u0 < 1e-8 || z1 - z0 < 1e-8)
                    return null;
                dir.Unitize();
                var p0 = a + dir * u0;
                var p1 = a + dir * u1;
                var x = p1 - p0;
                double segLen = x.Length;
                x.Unitize();
                var y = new Vector3d(-x.Y, x.X, 0);
                y.Unitize();
                var plane = new Plane(new Point3d((p0.X + p1.X) * .5, (p0.Y + p1.Y) * .5, z0), x, y);
                return new Box(plane, new Interval(-segLen * .5, segLen * .5), new Interval(-depth * .5, depth * .5), new Interval(0, z1 - z0)).ToBrep();
            };
            Func<string, string, string, string> NextName = (cls, owner, role) =>
            {
                // Shared semantic naming counter, incremented consistently per component category, owner and role.
                string key = cls + "|" + owner + "|" + role;
                int n = counters.ContainsKey(key) ? counters[key] + 1 : 1;
                counters[key] = n;
                return sid + "__" + cls + "__" + owner + "__" + role + "__" + n.ToString("00");
            };
            var occupiedWalls = new List<double[]>();
            int resolvedWallOverlaps = 0;
            Func<double[], double[], List<double[]>> SubtractBox = (p, q) =>
            {
                // Axis-aligned box subtraction deduplicates orthogonal wall pieces and reduces validation noise from overlap.
                double x0 = Math.Max(p[0], q[0]), y0 = Math.Max(p[1], q[1]), z0 = Math.Max(p[2], q[2]);
                double x1 = Math.Min(p[3], q[3]), y1 = Math.Min(p[4], q[4]), z1 = Math.Min(p[5], q[5]);
                var parts = new List<double[]>();
                if (x1 - x0 <= 1e-8 || y1 - y0 <= 1e-8 || z1 - z0 <= 1e-8)
                {
                    parts.Add(p);
                    return parts;
                }

                resolvedWallOverlaps++;
                if (x0 > p[0] + 1e-8)
                    parts.Add(new[] { p[0], p[1], p[2], x0, p[4], p[5] });
                if (x1 < p[3] - 1e-8)
                    parts.Add(new[] { x1, p[1], p[2], p[3], p[4], p[5] });
                if (y0 > p[1] + 1e-8)
                    parts.Add(new[] { x0, p[1], p[2], x1, y0, p[5] });
                if (y1 < p[4] - 1e-8)
                    parts.Add(new[] { x0, y1, p[2], x1, p[4], p[5] });
                if (z0 > p[2] + 1e-8)
                    parts.Add(new[] { x0, y0, p[2], x1, y1, z0 });
                if (z1 < p[5] - 1e-8)
                    parts.Add(new[] { x0, y0, z1, x1, y1, p[5] });
                return parts;
            };
            Action<List<Solid>, Solid, string, string, string> AddFormal = (target, b, cls, owner, role) =>
            {
                // All solids enter through AddFormal to ensure:
                // 1) Optional wall deduplication; 2) synchronized semantic names; 3) stable output ordering.
                if (b == null)
                    return;
                var bb = b.GetBoundingBox(true);
                bool orthogonal = cls == "WAL" && b.Vertices.Count == 8 && b.Faces.Count == 6;
                if (orthogonal)
                    foreach (var vertex in b.Vertices)
                    {
                        var p = vertex.Location;
                        if ((Math.Abs(p.X - bb.Min.X) > 1e-7 && Math.Abs(p.X - bb.Max.X) > 1e-7) || (Math.Abs(p.Y - bb.Min.Y) > 1e-7 && Math.Abs(p.Y - bb.Max.Y) > 1e-7) || (Math.Abs(p.Z - bb.Min.Z) > 1e-7 && Math.Abs(p.Z - bb.Max.Z) > 1e-7))
                        {
                            orthogonal = false;
                            break;
                        }
                    }

                if (orthogonal)
                {
                    var parts = new List<double[]>
                    {
                        new[]
                        {
                            bb.Min.X,
                            bb.Min.Y,
                            bb.Min.Z,
                            bb.Max.X,
                            bb.Max.Y,
                            bb.Max.Z
                        }
                    };
                    foreach (var previous in occupiedWalls)
                    {
                        var remaining = new List<double[]>();
                        foreach (var part in parts)
                            remaining.AddRange(SubtractBox(part, previous));
                        parts = remaining;
                        if (parts.Count == 0)
                            break;
                    }

                    foreach (var p in parts)
                    {
                        var piece = Extrude(Rect(p[0], p[1], p[3], p[4]), p[2], p[5] - p[2]);
                        if (piece == null)
                            continue;
                        occupiedWalls.Add(p);
                        target.Add(piece);
                        allFormal.Add(piece);
                        semanticNames.Add(NextName(cls, owner, role));
                    }
                }
                else
                {
                    target.Add(b);
                    allFormal.Add(b);
                    semanticNames.Add(NextName(cls, owner, role));
                }
            };
            Action<List<Solid>, List<Point3d>, double, double, string, string, string> AddPrism = (target, poly, z, h, cls, owner, role) =>
            {
                // Common construction helper: extrude a planar contour directly into a solid.
                AddFormal(target, Extrude(poly, z, h), cls, owner, role);
            };
            // F/R/C/G and upper U_* contours are the parent outlines for subsequent walls, slabs, doors and windows.
            var fPts = sid == "A" ? Rect(0, fBottom, cW, 0) : Rect(fLeft, fBottom, cW, 0);
            var rPts = Rect(rLeft, rSouth, 0, cH);
            var cPts = Rect(0, 0, cW, cH);
            var gPts = new List<Point3d>
            {
                new Point3d(gWest, cH, 0),
                new Point3d(cW, cH, 0),
                new Point3d(cW, gSouth, 0),
                new Point3d(gEast, gSouth, 0),
                new Point3d(gEast, gNorth, 0),
                new Point3d(gWest, gNorth, 0)
            };
            var uR1Pts = new List<Point3d>
            {
                new Point3d(uRWest, uRSouth, 0),
                new Point3d(0, uRSouth, 0),
                new Point3d(0, 0, 0),
                new Point3d(0, cH, 0),
                new Point3d(uRWest, cH, 0)
            };
            // U_R2 repeats the U_R1 footprint: no third-floor cantilever beyond the floor directly below.
            var uR2Pts = new List<Point3d>
            {
                new Point3d(uRWest, uRSouth, 0),
                new Point3d(0, uRSouth, 0),
                new Point3d(0, 0, 0),
                new Point3d(0, cH, 0),
                new Point3d(uRWest, cH, 0)
            };
            // U_G extends west only over the supporting G field.  The horizontal edge at y=cH
            // creates the U_R2-U_G connection without projecting U_R2 beyond U_R1.
            var uGPts = new List<Point3d>
            {
                new Point3d(uGWest, cH, 0),
                new Point3d(0, cH, 0),
                new Point3d(cW, cH, 0),
                new Point3d(cW, 0, 0),
                new Point3d(cW, uGSouth, 0),
                new Point3d(uGEast, uGSouth, 0),
                new Point3d(uGEast, uGNorth, 0),
                new Point3d(uGWest, uGNorth, 0)
            };
            // Foundations use the same final geometry mapping as walls and roofs.
            AddPrism(slabs, fPts, -.30, .30, "SLB", "F", "OWNED_FOUNDATION");
            AddPrism(slabs, rPts, -.30, .30, "SLB", "R", "OWNED_FOUNDATION");
            AddPrism(slabs, gPts, -.30, .30, "SLB", "G", "OWNED_FOUNDATION");
            AddPrism(slabs, cPts, -.20, .20, "SLB", "C", "COURTYARD_BASE");
            // Opening tuple: role, u0 fraction, u1 fraction, z0, z1, glazed, full-height, entry-door.
            Func<string, double, double, double, double, bool, bool, bool, object[]> Op = (role, u0, u1, z0, z1, glazedFlag, full, entry) => new object[]
            {
                // Opening specification: role, normalized u0/u1 interval, z0/z1 elevations and type flags.
                role,
                u0,
                u1,
                z0,
                z1,
                glazedFlag,
                full,
                entry
            };
            var wallSpecs = new List<object[]>();
            // AddWallSpec records wall hosts and opening parameters before discretizing walls, doors, glazing and frames, avoiding inconsistent local booleans.
            Action<string, string, Point3d, Point3d, double, double, List<object[]>, bool> AddWallSpec = (owner, role, a, b, z0, z1, ops, upper) => wallSpecs.Add(new object[] { owner, role, a, b, z0, z1, ops, upper });
            Func<double, double, double, double[]> OpeningAt = (hostLength, clearWidth, centre) =>
            {
                double w = Math.Min(Math.Max(.80, clearWidth), Math.Max(.80, hostLength - .40));
                double c = Math.Max(w * .5 + .20, Math.Min(hostLength - w * .5 - .20, centre * hostLength));
                return new[]
                {
                    (c - w * .5) / hostLength,
                    (c + w * .5) / hostLength
                };
            };
            var fcPortal = OpeningAt(cW, p1FCClearWidth, .50);
            var rcWindow = OpeningAt(cH, p2RCViewWidth, .70);
            var rgPortal = OpeningAt(Math.Abs(gWest), p3SharedClearWidth, .50);
            var gcEastPortal = OpeningAt(cH, p3SharedClearWidth, .18);
            var gcNorthPortal = OpeningAt(cW, p3SharedClearWidth, .70);
            if (sid == "A")
            {
                AddWallSpec("F", "SOUTH_ENTRY", new Point3d(0, fBottom, 0), new Point3d(cW, fBottom, 0), 0, F_TOP, new List<object[]> { Op("GLAZED_PANEL_LEFT", .08, .34, .35, 3.2, true, false, false), Op("ENTRY_PORTAL", .38, .62, 0, 3.2, true, false, true), Op("GLAZED_PANEL_RIGHT", .66, .92, .35, 3.2, true, false, false) }, false);
                AddWallSpec("F", "WEST_EXTERIOR", new Point3d(0, fBottom, 0), new Point3d(0, rSouth, 0), 0, F_TOP, new List<object[]> { Op("EXTERIOR_HIGH_WINDOW", .36, .64, .48 * Z_F, .70 * Z_F, true, false, false) }, false);
                AddWallSpec("F_R", "SHARED_INTERFACE", new Point3d(0, rSouth, 0), new Point3d(0, 0, 0), 0, F_TOP, new List<object[]> { Op("PORTAL", .22, .78, 0, 3.0, false, false, false), Op("U_R1_F_FULL_HEIGHT_GLAZED_WALL", .03, .97, Z_R_FINISH, Z_G - .05, true, true, false), Op("U_R2_F_FULL_HEIGHT_GLAZED_WALL", .03, .97, Z_UPPER_FINISH, F_SHARED_UPPER_HEAD, true, true, false) }, false);
                AddWallSpec("F_C", "SHARED_INTERFACE", new Point3d(0, 0, 0), new Point3d(cW, 0, 0), 0, F_TOP, new List<object[]> { Op("PORTAL", fcPortal[0], fcPortal[1], 0, 3.0, false, false, false), Op("CLERESTORY_LEFT", .08, .33, .50 * Z_F, .80 * Z_F, true, false, false), Op("CLERESTORY_RIGHT", .67, .92, .50 * Z_F, .80 * Z_F, true, false, false) }, false);
                AddWallSpec("F", "EAST_EXTERIOR", new Point3d(cW, fBottom, 0), new Point3d(cW, gSouth, 0), 0, F_TOP, new List<object[]> { Op("EXTERIOR_HIGH_WINDOW", .36, .64, .48 * Z_F, .70 * Z_F, true, false, false) }, false);
                AddWallSpec("F_G", "SHARED_INTERFACE", new Point3d(cW, gSouth, 0), new Point3d(cW, 0, 0), 0, F_TOP, new List<object[]> { Op("PORTAL", .22, .78, 0, 3.0, false, false, false), Op("U_G_F_FULL_HEIGHT_GLAZED_WALL", .03, .97, Z_UPPER_FINISH, F_SHARED_UPPER_HEAD, true, true, false) }, false);
            }
            else
            {
                AddWallSpec("F", "SOUTH_ENTRY", new Point3d(fLeft, fBottom, 0), new Point3d(cW, fBottom, 0), 0, F_TOP, new List<object[]> { Op("GLAZED_PANEL_LEFT", .08, .40, .35, 3.2, true, false, false), Op("ENTRY_PORTAL", .42, .58, 0, 3.2, true, false, true), Op("GLAZED_PANEL_RIGHT", .60, .92, .35, 3.2, true, false, false) }, false);
                AddWallSpec("F", "WEST_EXTERIOR", new Point3d(fLeft, fBottom, 0), new Point3d(fLeft, 0, 0), 0, F_TOP, new List<object[]> { Op("EXTERIOR_HIGH_WINDOW", .38, .62, .48 * Z_F, .70 * Z_F, true, false, false) }, false);
                AddWallSpec("F_R", "SHARED_INTERFACE", new Point3d(fLeft, 0, 0), new Point3d(0, 0, 0), 0, F_TOP, new List<object[]> { Op("PORTAL", .35, .65, 0, 3.0, false, false, false), Op("U_R1_F_FULL_HEIGHT_GLAZED_WALL", .55, .98, Z_R_FINISH, Z_G - .05, true, true, false), Op("U_R2_F_FULL_HEIGHT_GLAZED_WALL", .55, .98, Z_UPPER_FINISH, F_SHARED_UPPER_HEAD, true, true, false) }, false);
                AddWallSpec("F_C", "SHARED_INTERFACE", new Point3d(0, 0, 0), new Point3d(cW, 0, 0), 0, F_TOP, new List<object[]> { Op("PORTAL", fcPortal[0], fcPortal[1], 0, 3.0, false, false, false), Op("CLERESTORY_LEFT", .06, .29, .50 * Z_F, .80 * Z_F, true, false, false), Op("CLERESTORY_RIGHT", .71, .94, .50 * Z_F, .80 * Z_F, true, false, false) }, false);
                AddWallSpec("F", "EAST_EXTERIOR", new Point3d(cW, fBottom, 0), new Point3d(cW, gSouth, 0), 0, F_TOP, new List<object[]> { Op("EXTERIOR_HIGH_WINDOW", .22, .78, .48 * Z_F, .70 * Z_F, true, false, false) }, false);
                AddWallSpec("F_G", "SHARED_INTERFACE", new Point3d(cW, gSouth, 0), new Point3d(cW, 0, 0), 0, F_TOP, new List<object[]> { Op("PORTAL", .21, .79, 0, 3.0, false, false, false), Op("U_G_F_FULL_HEIGHT_GLAZED_WALL", .03, .97, Z_UPPER_FINISH, F_SHARED_UPPER_HEAD, true, true, false) }, false);
            }

            AddWallSpec("R", "WEST_EXTERIOR", new Point3d(rLeft, rSouth, 0), new Point3d(rLeft, cH, 0), 0, Z_R, new List<object[]> { Op("EXTERIOR_VIEW_WINDOW", .32, .68, .45, Z_R - .80, true, false, false) }, false);
            AddWallSpec("R", "SOUTH_EXTERIOR", new Point3d(rLeft, rSouth, 0), new Point3d(sid == "A" ? 0 : fLeft, rSouth, 0), 0, Z_R, new List<object[]>(), false);
            AddWallSpec("R", "NORTH_EXTERIOR", new Point3d(rLeft, cH, 0), new Point3d(gWest, cH, 0), 0, Z_R, new List<object[]>(), false);
            AddWallSpec("R_G", "SHARED_INTERFACE", new Point3d(gWest, cH, 0), new Point3d(0, cH, 0), 0, Z_G, new List<object[]> { Op("PORTAL", rgPortal[0], rgPortal[1], 0, 3.0, false, false, false), Op("U_R1_G_PERCEPTUAL_APERTURE", .16, .84, Z_R_FINISH + .25, Z_G - .40, false, false, false) }, false);
            AddWallSpec("R_C", "SHARED_INTERFACE", new Point3d(0, 0, 0), new Point3d(0, cH, 0), 0, Z_R, new List<object[]> { Op("PORTAL", sid == "A" ? .08 : .07, sid == "A" ? .32 : .24, 0, 3.0, false, false, false), Op("OBSERVATION_WINDOW", rcWindow[0], rcWindow[1], .45, Z_R - .80, true, false, false) }, false);
            AddWallSpec("G_C", "EAST_COURTYARD", new Point3d(cW, 0, 0), new Point3d(cW, cH, 0), 0, Z_G, new List<object[]> { Op("PORTAL", gcEastPortal[0], gcEastPortal[1], 0, 3.0, false, false, false), Op("CLERESTORY", sid == "A" ? .42 : .32, .92, .60 * Z_G, Z_G - .80, true, false, false) }, false);
            AddWallSpec("G_C", "NORTH_COURTYARD", new Point3d(0, cH, 0), new Point3d(cW, cH, 0), 0, Z_G, new List<object[]> { Op("CLERESTORY", .06, .32, .60 * Z_G, Z_G - .80, true, false, false), Op("PORTAL", gcNorthPortal[0], gcNorthPortal[1], 0, 3.0, false, false, false) }, false);
            AddWallSpec("G", "SOUTH_EXTERIOR", new Point3d(cW, gSouth, 0), new Point3d(gEast, gSouth, 0), 0, Z_G, new List<object[]> { Op("EXTERIOR_CLERESTORY", .23, .77, .60 * Z_G, Z_G - .75, true, false, false) }, false);
            AddWallSpec("G", "EAST_EXTERIOR", new Point3d(gEast, gSouth, 0), new Point3d(gEast, gNorth, 0), 0, Z_G, new List<object[]> { Op("EXTERIOR_CLERESTORY", .26, .47, .60 * Z_G, Z_G - .75, true, false, false) }, false);
            AddWallSpec("G", "NORTH_EXTERIOR", new Point3d(gEast, gNorth, 0), new Point3d(gWest, gNorth, 0), 0, Z_G, new List<object[]> { Op("EXTERIOR_CLERESTORY", .40, .60, .60 * Z_G, Z_G - .75, true, false, false) }, false);
            AddWallSpec("G", "WEST_EXTERIOR", new Point3d(gWest, gNorth, 0), new Point3d(gWest, cH, 0), 0, Z_G, new List<object[]>(), false);
            // Generate upper perimeter walls edge by edge; courtyardEdges selects edges with daylight openings.
            Action<List<Point3d>, string, double, double, HashSet<int>, HashSet<int>> AddUpperPerimeter = (poly, owner, z0, z1, courtyardEdges, skipEdges) =>
            {
                poly = CCW(poly);
                for (int i = 0; i < poly.Count; i++)
                {
                    if (skipEdges.Contains(i))
                        continue;
                    var a = poly[i];
                    var b = poly[(i + 1) % poly.Count];
                    var edge = b - a;
                    double len = edge.Length;
                    if (len < 1e-8)
                        continue;
                    edge.Unitize();
                    var inward = new Vector3d(-edge.Y, edge.X, 0) * (.5 * WALL_T);
                    a += inward;
                    b += inward;
                    var ops = new List<object[]>();
                    if (courtyardEdges.Contains(i))
                    {
                        if (len >= 9.0)
                        {
                            ops.Add(Op("UPPER_WINDOW_" + (i + 1) + "_A", .08, .47, z0 + .45, z1 - .45, true, false, false));
                            ops.Add(Op("UPPER_WINDOW_" + (i + 1) + "_B", .53, .92, z0 + .45, z1 - .45, true, false, false));
                        }
                        else if (len > 1.5)
                            ops.Add(Op("UPPER_WINDOW_" + (i + 1), .15, .85, z0 + .45, z1 - .45, true, false, false));
                    }

                    if ((owner == "U_R1" || owner == "U_R2") && i == 1)
                        ops.Add(Op(owner + "_F_FULL_HEIGHT_GLAZED_WALL", .03, .97, z0, z1 - .05, true, true, false));
                    AddWallSpec(owner, owner + "_PERIMETER_EDGE_" + (i + 1), a, b, z0, z1, ops, true);
                }
            };
            AddUpperPerimeter(uR1Pts, "U_R1", Z_R_FINISH, Z_G, new HashSet<int> { 2 }, new HashSet<int>(sid == "A" ? new[] { 1, 3 } : new[] { 3 }));
            AddUpperPerimeter(uR2Pts, "U_R2", Z_UPPER_FINISH, Z_ROOF, new HashSet<int> { 2 }, new HashSet<int>(sid == "A" ? new[] { 1, 3 } : new[] { 3 }));
            AddUpperPerimeter(uGPts, "U_G", Z_UPPER_FINISH, Z_ROOF, new HashSet<int> { 1, 2 }, new HashSet<int> { 0, 3 });
            AddWallSpec("U_R2_U_G", "THIRD_FLOOR_CONNECTION", new Point3d(uGWest, cH, 0), new Point3d(0, cH, 0), Z_UPPER_FINISH, Z_ROOF, new List<object[]> { Op("PORTAL", .16, .84, Z_UPPER_FINISH, Z_ROOF - .30, false, false, false) }, true);
            if (uGWest > uRWest + .005)
                AddWallSpec("U_R2", "NORTH_SUPPORT_CLOSURE", new Point3d(uRWest, cH, 0), new Point3d(uGWest, cH, 0), Z_UPPER_FINISH, Z_ROOF, new List<object[]>(), true);
            // Three local semantic cues rotate inside stable room envelopes. They change
            // architectural reading without changing F/R/G/C/U adjacency or requiring a connector.
            // The P1/P2/P3 cue walls change spatial perception cues while retaining the main topology.
            Func<double, Vector3d> Direction = degrees =>
            {
                double a = degrees * Math.PI / 180.0;
                return new Vector3d(Math.Cos(a), Math.Sin(a), 0);
            };
            var revealCentre = sid == "A" ? new Point3d(cW * .24, fBottom + 3.15 + p1ThresholdDelta * .35, 0) : new Point3d(fLeft + (cW - fLeft) * .27, fBottom + 2.45 + p1ThresholdDelta * .25, 0);
            var revealBaseDir = sid == "A" ? Vector3d.XAxis : Vector3d.YAxis;
            var revealDir = Direction((sid == "A" ? 0.0 : 90.0) + p1RevealAngleDeg);
            var p1CueBase = new LineCurve(revealCentre - revealBaseDir * 1.20, revealCentre + revealBaseDir * 1.20);
            var p1CuePlane = new Plane(revealCentre, Vector3d.XAxis, Vector3d.YAxis);
            var p2Base = sid == "A" ? new Point3d(rLeft * .55, cH * .42, 0) : new Point3d(rLeft * .55, cH * .55, 0);
            var towardC = new Vector3d(-p2Base.X, -p2Base.Y, 0);
            towardC.Unitize();
            var p2Anchor = p2Base - towardC * p2AnchorOffset;
            // Place the shelter screen behind the occupied anchor, rather than through it.
            var screenCentre = p2Anchor - towardC * 1.45;
            var screenBaseDir = Vector3d.XAxis;
            var screenDir = Direction(p2ScreenAngleDeg);
            var p2CueBase = new LineCurve(screenCentre - screenBaseDir * 1.15, screenCentre + screenBaseDir * 1.15);
            var p2CuePlane = new Plane(screenCentre, Vector3d.XAxis, Vector3d.YAxis);
            var displayCentre = new Point3d(cW + (gEast - cW) * .55, gSouth + (cH - gSouth) * .55, 0);
            var displayBaseDir = Vector3d.XAxis;
            var displayDir = Direction(p3DisplayAngleDeg);
            var p3CueBase = new LineCurve(displayCentre - displayBaseDir * 1.50, displayCentre + displayBaseDir * 1.50);
            var p3CuePlane = new Plane(displayCentre, Vector3d.XAxis, Vector3d.YAxis);
            // Shear real closed wall Breps around their horizontal floor contact.
            // A rigid 3D rotation would put one bottom corner through the floor.
            // These are freestanding architectural walls, not interaction proxy blocks.
            Action<Point3d, Vector3d, double, double, double, Vector3d, string, string> TiltWall = (centre, dir, length, height, tilt, toward, owner, role) =>
            {
                // Shear tilted walls while keeping their bottom edges on the floor, avoiding floor penetration from rigid rotation.
                var normal = new Vector3d(-dir.Y, dir.X, 0);
                if (normal.X * toward.X + normal.Y * toward.Y < 0)
                    normal = -normal;
                const double baseZ = .06;
                var wall = SegmentBox(centre - dir * (length * .5), centre + dir * (length * .5), 0, length, baseZ, baseZ + height);
                double slope = Math.Tan(tilt * Math.PI / 180.0);
                var transform = Transform.Identity;
                transform.M02 = normal.X * slope;
                transform.M12 = normal.Y * slope;
                transform.M03 = -normal.X * slope * baseZ;
                transform.M13 = -normal.Y * slope * baseZ;
                if (wall != null && !wall.Transform(transform))
                    throw new InvalidOperationException("Cannot incline wall: " + role);
                AddFormal(walls, wall, "WAL", owner, role);
            };
            TiltWall(revealCentre, revealDir, 2.40, p1WallHeight, p1TiltDeg, new Vector3d(cW * .5 - revealCentre.X, 0, 0), "F", "P1_REVEAL_WALL");
            TiltWall(screenCentre, screenDir, 2.30, p2WallHeight, p2TiltDeg, towardC, "R", "P2_SHELTER_SCREEN");
            TiltWall(displayCentre, displayDir, 3.00, p3WallHeight, p3TiltDeg, new Vector3d(0, -1, 0), "G", "P3_DISPLAY_ALIGNMENT_WALL");
            // Slab and ceiling openings are tiled, avoiding runtime Boolean dependence.
            Func<List<Point3d>, double[]> Bounds = poly => new[]
            {
                Enumerable.Min(poly, p => p.X),
                Enumerable.Min(poly, p => p.Y),
                Enumerable.Max(poly, p => p.X),
                Enumerable.Max(poly, p => p.Y)
            };
            Action<List<Solid>, List<Point3d>, double, double, List<List<Point3d>>, string, string, string> TileRect = (target, rect, z, h, holes, cls, owner, role) =>
            {
                // Tile rectangles along opening boundaries and omit cells inside openings for stable boolean-free construction.
                var rb = Bounds(rect);
                var active = new List<double[]>(Enumerable.Where(Enumerable.Select(holes, hole => Bounds(hole)), b => b[2] > rb[0] && b[0] < rb[2] && b[3] > rb[1] && b[1] < rb[3]));
                var xs = new List<double>
                {
                    rb[0],
                    rb[2]
                };
                var ys = new List<double>
                {
                    rb[1],
                    rb[3]
                };
                foreach (var b in active)
                {
                    xs.Add(Math.Max(rb[0], b[0]));
                    xs.Add(Math.Min(rb[2], b[2]));
                    ys.Add(Math.Max(rb[1], b[1]));
                    ys.Add(Math.Min(rb[3], b[3]));
                }

                xs = new List<double>(Enumerable.OrderBy(Enumerable.Distinct(xs), v => v));
                ys = new List<double>(Enumerable.OrderBy(Enumerable.Distinct(ys), v => v));
                for (int i = 0; i < xs.Count - 1; i++)
                    for (int j = 0; j < ys.Count - 1; j++)
                    {
                        double xm = (xs[i] + xs[i + 1]) * .5, ym = (ys[j] + ys[j + 1]) * .5;
                        if (!Enumerable.Any(active, b => xm > b[0] && xm < b[2] && ym > b[1] && ym < b[3]))
                            AddPrism(target, Rect(xs[i], ys[j], xs[i + 1], ys[j + 1]), z, h, cls, owner, role);
                    }
            };
            var groundLinkHoles = new List<List<Point3d>>();
            var upperLinkHoles = new List<List<Point3d>>();
            var portalWidths = new List<double>();
            var portalOwners = new List<string>();
            // Correct shared portal widths using the minimum post-taper plan scale to meet the final clear-width contract.
            foreach (var ws in wallSpecs)
            {
                var a = (Point3d)ws[2];
                var b = (Point3d)ws[3];
                double len = (b - a).Length;
                foreach (var op in (List<object[]>)ws[6])
                {
                    if ((string)op[0] != "PORTAL" || (bool)op[5] || (bool)op[7])
                        continue;
                    double minScale = Math.Min(volumeMap.PlanScale((double)op[3]), volumeMap.PlanScale((double)op[4]));
                    double required = 2.02 / minScale, width = Math.Max(required, ((double)op[2] - (double)op[1]) * len);
                    if (width > len - .40)
                        throw new InvalidOperationException("Portal does not fit shared host: " + ws[0]);
                    double centre = ((double)op[1] + (double)op[2]) * .5 * len;
                    centre = Math.Max(width * .5 + .20, Math.Min(len - width * .5 - .20, centre));
                    op[1] = (centre - width * .5) / len;
                    op[2] = (centre + width * .5) / len;
                    portalWidths.Add(width * minScale);
                    portalOwners.Add((string)ws[0]);
                }
            }

            TileRect(slabs, fPts, 0, .06, groundLinkHoles, "FLR", "F", "GROUND_FINISH");
            TileRect(slabs, rPts, 0, .06, groundLinkHoles, "FLR", "R", "GROUND_FINISH");
            TileRect(slabs, Rect(cW, gSouth, gEast, cH), 0, .06, groundLinkHoles, "FLR", "G", "GROUND_FINISH");
            TileRect(slabs, Rect(gWest, cH, gEast, gNorth), 0, .06, groundLinkHoles, "FLR", "G", "GROUND_FINISH");
            TileRect(slabs, cPts, 0, .06, groundLinkHoles, "FLR", "C", "COURTYARD_FINISH");
            // Build each unique wall host as non-overlapping solid cells, then rebuild its local opening accessories.
            // Unified wall discretization:
            // 1) Split the u-z grid at openings; 2) create wall pieces in non-opening cells;
            // 3) generate leaves, glazing and frames in openings; 4) retain interface lines for semantic checks.
            foreach (var ws in wallSpecs)
            {
                string owner = (string)ws[0], role = (string)ws[1];
                var a = (Point3d)ws[2];
                var b = (Point3d)ws[3];
                double wz0 = (double)ws[4], wz1 = (double)ws[5];
                var ops = (List<object[]>)ws[6];
                double len = (b - a).Length;
                var us = new List<double>
                {
                    0,
                    len
                };
                var zs = new List<double>
                {
                    wz0,
                    wz1
                };
                foreach (var op in ops)
                {
                    double ou0 = Math.Max(0, Math.Min(len, (double)op[1] * len));
                    double ou1 = Math.Max(0, Math.Min(len, (double)op[2] * len));
                    us.Add(ou0);
                    us.Add(ou1);
                    zs.Add((double)op[3]);
                    zs.Add((double)op[4]);
                }

                us = new List<double>(Enumerable.OrderBy(Enumerable.Distinct(us), v => v));
                zs = new List<double>(Enumerable.OrderBy(Enumerable.Distinct(Enumerable.Where(zs, v => v >= wz0 - 1e-8 && v <= wz1 + 1e-8)), v => v));
                int cell = 0;
                for (int i = 0; i < us.Count - 1; i++)
                    for (int j = 0; j < zs.Count - 1; j++)
                    {
                        double um = (us[i] + us[i + 1]) * .5, zm = (zs[j] + zs[j + 1]) * .5;
                        bool isVoid = false;
                        foreach (var op in ops)
                        {
                            double ou0 = (double)op[1] * len, ou1 = (double)op[2] * len, oz0 = (double)op[3], oz1 = (double)op[4];
                            if (um > ou0 - 1e-8 && um < ou1 + 1e-8 && zm > oz0 - 1e-8 && zm < oz1 + 1e-8)
                            {
                                isVoid = true;
                                break;
                            }
                        }

                        if (!isVoid)
                            AddFormal(walls, SegmentBox(a, b, us[i], us[i + 1], zs[j], zs[j + 1]), "WAL", owner, role + "_PANEL_" + (++cell));
                    }

                if (owner.Contains("_") || owner == "U_R2_U_G")
                    interfaces.Add(new LineCurve(a, b));
                foreach (var op in ops)
                {
                    string oRole = (string)op[0];
                    double u0 = (double)op[1] * len, u1 = (double)op[2] * len, z0 = (double)op[3], z1 = (double)op[4];
                    bool glazed = (bool)op[5], full = (bool)op[6], entry = (bool)op[7];
                    double inset = Math.Min(FRAME_W, Math.Min((u1 - u0) * .18, (z1 - z0) * .18));
                    Action<double, double, double, double, string> Bar = (bu0, bu1, bz0, bz1, suffix) => AddFormal(frames, ThinSegmentBox(a, b, bu0, bu1, bz0, bz1, FRAME_W), "FRM", owner, oRole + "_" + suffix);
                    if (entry)
                    {
                        double mid = (u0 + u1) * .5;
                        AddFormal(doors, ThinSegmentBox(a, b, u0 + inset, mid - inset * .5, z0 + .03, z1 - inset, GLASS_T), "DOR", owner, oRole + "_LEFT_LEAF");
                        AddFormal(doors, ThinSegmentBox(a, b, mid + inset * .5, u1 - inset, z0 + .03, z1 - inset, GLASS_T), "DOR", owner, oRole + "_RIGHT_LEAF");
                        Bar(u0, u0 + inset, z0, z1, "JAMB_LEFT");
                        Bar(u1 - inset, u1, z0, z1, "JAMB_RIGHT");
                        Bar(mid - inset * .5, mid + inset * .5, z0, z1 - inset, "CENTER_MULLION");
                        Bar(u0 + inset, u1 - inset, z1 - inset, z1, "HEAD");
                    }
                    else if (glazed)
                    {
                        int bays = full ? Math.Max(2, (int)Math.Ceiling((u1 - u0 - 2 * inset) / 1.35)) : 1;
                        double bayWidth = (u1 - u0 - 2 * inset) / bays;
                        for (int bay = 0; bay < bays; bay++)
                        {
                            double lo = u0 + inset + bay * bayWidth + (bay > 0 ? FRAME_W * .5 : 0);
                            double hi = u0 + inset + (bay + 1) * bayWidth - (bay < bays - 1 ? FRAME_W * .5 : 0);
                            AddFormal(glass, ThinSegmentBox(a, b, lo, hi, z0 + inset, z1 - inset, GLASS_T), "GLS", owner, oRole);
                        }

                        Bar(u0, u0 + inset, z0, z1, "JAMB_LEFT");
                        Bar(u1 - inset, u1, z0, z1, "JAMB_RIGHT");
                        Bar(u0 + inset, u1 - inset, z0, z0 + inset, "SILL");
                        Bar(u0 + inset, u1 - inset, z1 - inset, z1, "HEAD");
                        if (full)
                        {
                            for (int bay = 1; bay < bays; bay++)
                            {
                                double u = u0 + inset + (u1 - u0 - 2 * inset) * bay / bays;
                                Bar(u - FRAME_W * .5, u + FRAME_W * .5, z0 + inset, z1 - inset, "FULL_HEIGHT_MULLION_" + bay);
                            }
                        }
                    }
                    else if (oRole != "PORTAL")
                    {
                        Bar(u0, u0 + inset, z0, z1, "JAMB_LEFT");
                        Bar(u1 - inset, u1, z0, z1, "JAMB_RIGHT");
                        Bar(u0 + inset, u1 - inset, z1 - inset, z1, "HEAD");
                        if (oRole.Contains("APERTURE"))
                            Bar(u0 + inset, u1 - inset, z0, z0 + inset, "SILL");
                    }
                }
            }

            Func<double, double, double, double, double, double, double, double, List<Point3d>> MapRect = (x0, y0, x1, y1, bx0, by0, bx1, by1) =>
            {
                double tx0 = (x0 - bx0) / (bx1 - bx0), tx1 = (x1 - bx0) / (bx1 - bx0), ty0 = (y0 - by0) / (by1 - by0), ty1 = (y1 - by0) / (by1 - by0);
                double[] cb = sid == "A" ? new[]
                {
                    0.0,
                    fBottom,
                    cW,
                    0.0
                }

                : new[]
                {
                    fLeft,
                    fBottom,
                    cW,
                    0.0
                };
                return Rect(cb[0] + tx0 * (cb[2] - cb[0]), cb[1] + ty0 * (cb[3] - cb[1]), cb[0] + tx1 * (cb[2] - cb[0]), cb[1] + ty1 * (cb[3] - cb[1]));
            };
            var fSky = sid == "A" ? MapRect(3.8, -9.2, 6.2, -3.2, 0, -10.99, 10, 0) : MapRect(.4, -5.2, 6.4, -2.8, -8, -7.3, 7, 0);
            var gEastRect = Rect(cW, gSouth, gEast, cH);
            var gNorthRect = Rect(gWest, cH, gEast, gNorth);
            Func<double, double, double, double, double[], double[], List<Point3d>> MapBetween = (x0, y0, x1, y1, bb, cb) => Rect(cb[0] + (x0 - bb[0]) / (bb[2] - bb[0]) * (cb[2] - cb[0]), cb[1] + (y0 - bb[1]) / (bb[3] - bb[1]) * (cb[3] - cb[1]), cb[0] + (x1 - bb[0]) / (bb[2] - bb[0]) * (cb[2] - cb[0]), cb[1] + (y1 - bb[1]) / (bb[3] - bb[1]) * (cb[3] - cb[1]));
            var gSky1 = sid == "A" ? MapBetween(14.3, 1.5, 16.3, 7.5, new[] { 10.0, -4.39, 18.0, 10.0 }, new[] { cW, gSouth, gEast, cH }) : MapBetween(11.0, 3.0, 13.0, 10.0, new[] { 7.0, -4.1, 14.0, 14.0 }, new[] { cW, gSouth, gEast, cH });
            var gSky2 = sid == "A" ? MapBetween(2.0, 14.0, 8.0, 14.8, new[] { -4.0, 10.0, 18.0, 15.04 }, new[] { gWest, cH, gEast, gNorth }) : MapBetween(.5, 18.0, 6.5, 18.8, new[] { -4.4, 14.0, 14.0, 19.3967391304 }, new[] { gWest, cH, gEast, gNorth });
            AddPrism(slabs, rPts, Z_R, VISIBLE_SLAB_T, "SLB", "R", "ROOF_AND_U_R1_VISIBLE_STRUCTURE");
            if (sid == "B")
                AddPrism(slabs, Rect(uRWest, uRSouth, 0, 0), Z_R, VISIBLE_SLAB_T, "SLB", "U_R1", "FRONT_EXTENSION_VISIBLE_STRUCTURE");
            AddPrism(slabs, uR1Pts, Z_R + VISIBLE_SLAB_T, FLOOR_BUILDUP_T, "FLR", "U_R1", "RECESSED_SECOND_FLOOR_BUILDUP");
            TileRect(slabs, gEastRect, Z_G, VISIBLE_SLAB_T, new List<List<Point3d>> { gSky1 }, "SLB", "G", "ROOF_AND_U_G_VISIBLE_STRUCTURE");
            TileRect(slabs, gNorthRect, Z_G, VISIBLE_SLAB_T, new List<List<Point3d>> { gSky2 }, "SLB", "G", "ROOF_AND_U_G_VISIBLE_STRUCTURE");
            AddPrism(slabs, uR1Pts, Z_G, VISIBLE_SLAB_T, "SLB", "U_R2", "THIRD_FLOOR_VISIBLE_STRUCTURE");
            TileRect(slabs, Rect(uRWest, uRSouth, 0, cH), Z_G + VISIBLE_SLAB_T, FLOOR_BUILDUP_T, upperLinkHoles, "FLR", "U_R2", "RECESSED_THIRD_FLOOR_BUILDUP");
            TileRect(slabs, Rect(cW, uGSouth, uGEast, cH), Z_G + VISIBLE_SLAB_T, FLOOR_BUILDUP_T, upperLinkHoles, "FLR", "U_G", "RECESSED_THIRD_FLOOR_BUILDUP");
            TileRect(slabs, Rect(uGWest, cH, uGEast, uGNorth), Z_G + VISIBLE_SLAB_T, FLOOR_BUILDUP_T, upperLinkHoles, "FLR", "U_G", "RECESSED_THIRD_FLOOR_BUILDUP");
            var fRoofHoles = new List<List<Point3d>>
            {
                fSky
            };
            if (sid == "B")
                fRoofHoles.Add(Rect(uRWest, uRSouth, 0, 0));
            TileRect(slabs, fPts, Z_F, VISIBLE_SLAB_T, fRoofHoles, "SLB", "F", "VARIABLE_F_ROOF_WITH_SKYLIGHT");
            AddPrism(slabs, uR2Pts, Z_ROOF, VISIBLE_SLAB_T, "SLB", "U_R2", "UPPER_ROOF_VISIBLE_STRUCTURE");
            AddPrism(slabs, uGPts, Z_ROOF, VISIBLE_SLAB_T, "SLB", "U_G", "UPPER_ROOF_VISIBLE_STRUCTURE");
            int expectedWellEdges = 0, generatedWellEdges = 0;
            Action<List<Point3d>, string, double, double> Skylight = (poly, owner, roofZ, ceilingZ) =>
            {
                // Skylight assembly: glazing, raised curb and vertical well walls where needed.
                // A raised curb makes the skylight legible in Rhino's uniform shaded mode.
                // Where the experimental ceiling is lower than the common roof, a four-sided
                // well keeps the opening physically continuous instead of leaving a hidden gap.
                double curbBase = roofZ + VISIBLE_SLAB_T, glassZ = curbBase + SKYLIGHT_CURB;
                var b = Bounds(poly);
                double fw = .10;
                AddPrism(glass, Rect(b[0] + fw, b[1] + fw, b[2] - fw, b[3] - fw), glassZ, .03, "GLS", owner, "SKYLIGHT_VISIBLE_GLAZING");
                var bars = new[]
                {
                    Rect(b[0], b[1], b[2], b[1] + fw),
                    Rect(b[0], b[3] - fw, b[2], b[3]),
                    Rect(b[0], b[1] + fw, b[0] + fw, b[3] - fw),
                    Rect(b[2] - fw, b[1] + fw, b[2], b[3] - fw)
                };
                foreach (var bar in bars)
                    AddPrism(frames, bar, curbBase, SKYLIGHT_CURB + .03, "FRM", owner, "SKYLIGHT_RAISED_CURB");
                if (curbBase - ceilingZ > .08)
                {
                    int well = 0;
                    foreach (var edge in new[]
                    {
                        new[]
                        {
                            poly[0],
                            poly[1]
                        },
                        new[]
                        {
                            poly[1],
                            poly[2]
                        },
                        new[]
                        {
                            poly[2],
                            poly[3]
                        },
                        new[]
                        {
                            poly[3],
                            poly[0]
                        }
                    }

                    )
                    {
                        double len = (edge[1] - edge[0]).Length;
                        expectedWellEdges++;
                        int before = walls.Count;
                        AddFormal(walls, ThinSegmentBox(edge[0], edge[1], 0, len, ceilingZ, curbBase, .10), "WAL", owner, "SKYLIGHT_WELL_EDGE_" + (++well));
                        if (walls.Count > before)
                            generatedWellEdges++;
                    }
                }
            };
            Skylight(fSky, "F", Z_F, Z_F - .05);
            Skylight(gSky1, "G", Z_G, Z_G - .05);
            Skylight(gSky2, "G", Z_G, Z_G - .05);
            TileRect(ceilings, fPts, Z_F - .05, .025, fRoofHoles, "CLG", "F", "SIMPLE_CEILING_FINISH");
            AddPrism(ceilings, rPts, Z_R - .05, .025, "CLG", "R", "SIMPLE_CEILING_FINISH");
            TileRect(ceilings, gEastRect, Z_G - .05, .025, new List<List<Point3d>> { gSky1 }, "CLG", "G", "SIMPLE_CEILING_FINISH");
            TileRect(ceilings, gNorthRect, Z_G - .05, .025, new List<List<Point3d>> { gSky2 }, "CLG", "G", "SIMPLE_CEILING_FINISH");
            AddPrism(ceilings, uR1Pts, Z_G - .05, .025, "CLG", "U_R1", "SECOND_FLOOR_CEILING_FINISH");
            AddPrism(ceilings, uR2Pts, Z_ROOF - .05, .025, "CLG", "U_R2", "THIRD_FLOOR_CEILING_FINISH");
            AddPrism(ceilings, uGPts, Z_ROOF - .05, .025, "CLG", "U_G", "THIRD_FLOOR_CEILING_FINISH");
            Func<Point3d, Vector3d, Point3d, Vector3d, Point3d> IntersectLines = (p, d, q, e) =>
            {
                double cross = d.X * e.Y - d.Y * e.X;
                if (Math.Abs(cross) < 1e-10)
                    return new Point3d((p.X + q.X) * .5, (p.Y + q.Y) * .5, 0);
                double t = ((q.X - p.X) * e.Y - (q.Y - p.Y) * e.X) / cross;
                return p + d * t;
            };
            Func<List<Point3d>, double, List<Point3d>> InsetPoly = (poly, inset) =>
            {
                var src = CCW(poly);
                var points = new List<Point3d>();
                var dirs = new List<Vector3d>();
                for (int i = 0; i < src.Count; i++)
                {
                    var p = src[i];
                    var n = src[(i + 1) % src.Count];
                    var d = n - p;
                    d.Unitize();
                    var inward = new Vector3d(-d.Y, d.X, 0);
                    points.Add(p + inward * inset);
                    dirs.Add(d);
                }

                var result = new List<Point3d>();
                for (int i = 0; i < src.Count; i++)
                {
                    int prev = (i - 1 + src.Count) % src.Count;
                    result.Add(IntersectLines(points[prev], dirs[prev], points[i], dirs[i]));
                }

                return result;
            };
            Action<List<Point3d>, double, string> AddRing = (poly, z, owner) =>
            {
                // Inset the contour and place slender light fixtures along its edges to form a ring.
                var ring = InsetPoly(poly, RING_INSET);
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if ((b - a).Length < .20)
                        continue;
                    AddFormal(lights, ThinSegmentBox(a, b, 0, (b - a).Length, z, z + .014, RING_W), "LGT", owner, "PERIMETER_RING_SEGMENT_" + (i + 1));
                }
            };
            var foyerRing = sid == "A" ? fPts : Rect(0, fBottom, cW, 0);
            AddRing(foyerRing, Z_F - .062, "F");
            AddRing(rPts, Z_R - .062, "R");
            AddRing(gPts, Z_G - .062, "G");
            AddRing(uR1Pts, Z_G - .062, "U_R1");
            AddRing(uR2Pts, Z_ROOF - .062, "U_R2");
            AddRing(uGPts, Z_ROOF - .062, "U_G");
            Func<List<Point3d>, double, List<Point3d>> Chamfer = (poly, rad) =>
            {
                var b = Bounds(poly);
                double r = Math.Min(rad, Math.Min((b[2] - b[0]) * .22, (b[3] - b[1]) * .22));
                return new List<Point3d>
                {
                    new Point3d(b[0] + r, b[1], 0),
                    new Point3d(b[2] - r, b[1], 0),
                    new Point3d(b[2], b[1] + r, 0),
                    new Point3d(b[2], b[3] - r, 0),
                    new Point3d(b[2] - r, b[3], 0),
                    new Point3d(b[0] + r, b[3], 0),
                    new Point3d(b[0], b[3] - r, 0),
                    new Point3d(b[0], b[1] + r, 0)
                };
            };
            Func<List<Point3d>, double[], double[], List<Point3d>> AffineRect = (poly, bb, cb) =>
            {
                var b = Bounds(poly);
                return Rect(cb[0] + (b[0] - bb[0]) / (bb[2] - bb[0]) * (cb[2] - cb[0]), cb[1] + (b[1] - bb[1]) / (bb[3] - bb[1]) * (cb[3] - cb[1]), cb[0] + (b[2] - bb[0]) / (bb[2] - bb[0]) * (cb[2] - cb[0]), cb[1] + (b[3] - bb[1]) / (bb[3] - bb[1]) * (cb[3] - cb[1]));
            };
            Action<List<Point3d>, double, double, string, string> Fixture = (poly, z, h, owner, role) => AddPrism(fixtures, Chamfer(poly, .04), z, h, "FIX", owner, role);
            // Displays and furniture are neutral geometry placeholders preserving experimental spatial semantics rather than presenting content.
            double[] fBB = sid == "A" ? new[]
            {
                0.0,
                -10.99,
                10.0,
                0.0
            }

            : new[]
            {
                -8.0,
                -7.3,
                7.0,
                0.0
            };
            double[] fCB = sid == "A" ? new[]
            {
                0.0,
                fBottom,
                cW,
                0.0
            }

            : new[]
            {
                fLeft,
                fBottom,
                cW,
                0.0
            };
            double[] rBB = sid == "A" ? new[]
            {
                -8.0,
                -4.39,
                0.0,
                10.0
            }

            : new[]
            {
                -8.2142857143,
                0.0,
                0.0,
                14.0
            };
            double[] rCB = new[]
            {
                rLeft,
                rSouth,
                0.0,
                cH
            };
            double[] gBB = sid == "A" ? new[]
            {
                -4.0,
                -4.39,
                18.0,
                15.04
            }

            : new[]
            {
                -4.4,
                -4.1,
                14.0,
                19.3967391304
            };
            double[] gCB = new[]
            {
                gWest,
                gSouth,
                gEast,
                gNorth
            };
            var desk = sid == "A" ? Rect(1.10, -9.60, 1.90, -7.40) : Rect(-6.00, -6.55, -5.20, -4.15);
            Fixture(AffineRect(desk, fBB, fCB), 0, 1.05, "F", "RECEPTION_DESK");
            var bench1 = sid == "A" ? Rect(-7.2, 4.8, -6.6, 7.2) : Rect(-7.2, 7.5, -6.6, 9.9);
            var bench2 = sid == "A" ? Rect(-4.8, 4.8, -4.2, 7.2) : Rect(-4.8, 7.5, -4.2, 9.9);
            var table = sid == "A" ? Rect(-6.2, 5.15, -5.2, 6.85) : Rect(-6.2, 7.85, -5.2, 9.55);
            int bi = 0;
            foreach (var bench in new[]
            {
                bench1,
                bench2
            }

            )
            {
                var p = AffineRect(bench, rBB, rCB);
                var b = Bounds(p);
                Fixture(p, .40, .05, "R", "TIMBER_BENCH_" + (++bi) + "_SEAT");
                Fixture(Rect(b[0] + .18, b[1] + .08, b[0] + .30, b[3] - .08), 0, .40, "R", "TIMBER_BENCH_" + bi + "_LEG_A");
                Fixture(Rect(b[2] - .30, b[1] + .08, b[2] - .18, b[3] - .08), 0, .40, "R", "TIMBER_BENCH_" + bi + "_LEG_B");
            }

            var tp = AffineRect(table, rBB, rCB);
            var tb = Bounds(tp);
            Fixture(tp, .38, .05, "R", "LOW_TABLE_TOP");
            Fixture(Rect(tb[0] + .18, tb[1] + .14, tb[0] + .32, tb[3] - .14), 0, .38, "R", "LOW_TABLE_LEG_A");
            Fixture(Rect(tb[2] - .32, tb[1] + .14, tb[2] - .18, tb[3] - .14), 0, .38, "R", "LOW_TABLE_LEG_B");
            var gPlinthData = sid == "A" ? new[]
            {
                Rect(13.0, 1.6, 14.0, 2.6),
                Rect(15.4, 8.0, 16.4, 9.0)
            }

            : new[]
            {
                Rect(9.2, 3.0, 10.2, 4.0),
                Rect(11.5, 10.2, 12.5, 11.2)
            };
            int pi = 0;
            foreach (var p in gPlinthData)
                Fixture(AffineRect(p, gBB, gCB), 0, 1.0, "G", "NEUTRAL_PLINTH_" + (++pi));
            var gPanelData = sid == "A" ? new[]
            {
                Rect(10.8, -4.27, 13.2, -4.19),
                Rect(15.0, -4.27, 17.4, -4.19)
            }

            : new[]
            {
                Rect(7.6, -3.98, 9.8, -3.90),
                Rect(11.2, -3.98, 13.4, -3.90)
            };
            pi = 0;
            foreach (var p in gPanelData)
                Fixture(AffineRect(p, gBB, gCB), .90, 1.50, "G", "BLANK_DISPLAY_PANEL_" + (++pi));
            // Upper exhibits remain content-free and are regenerated from their parent bounds.
            var uR1BB = sid == "A" ? new[]
            {
                -3.6,
                -4.0,
                0.0,
                10.0
            }

            : new[]
            {
                -3.6,
                -4.0,
                0.0,
                14.0
            };
            var uR1CB = new[]
            {
                uRWest,
                uRSouth,
                0.0,
                cH
            };
            var uR2BB = sid == "A" ? new[]
            {
                -3.6,
                -4.0,
                0.0,
                10.0
            }

            : new[]
            {
                -3.6,
                -4.0,
                0.0,
                14.0
            };
            var uR2CB = new[]
            {
                uRWest,
                uRSouth,
                0.0,
                cH
            };
            var uGBB = sid == "A" ? new[]
            {
                -3.6,
                -4.0,
                13.6,
                13.6
            }

            : new[]
            {
                -3.6,
                -4.0,
                10.6,
                17.6
            };
            var uGCB = new[]
            {
                uGWest,
                uGSouth,
                uGEast,
                uGNorth
            };
            var ur1Panels = sid == "A" ? new[]
            {
                Rect(-3.48, 1.2, -3.40, 3.6),
                Rect(-3.48, 6.4, -3.40, 8.8)
            }

            : new[]
            {
                Rect(-3.48, 2.2, -3.40, 4.7),
                Rect(-3.48, 9.0, -3.40, 11.5)
            };
            pi = 0;
            foreach (var p in ur1Panels)
                Fixture(AffineRect(p, uR1BB, uR1CB), Z_R_FINISH + .25, Math.Max(.75, Z_G - Z_R_FINISH - .65), "U_R1", "BLANK_DISPLAY_PANEL_" + (++pi));
            var ur2Plinth = sid == "A" ? Rect(-2.85, 7.2, -2.05, 8.0) : Rect(-2.85, 10.0, -2.05, 10.8);
            Fixture(AffineRect(ur2Plinth, uR2BB, uR2CB), Z_UPPER_FINISH, .95, "U_R2", "NEUTRAL_PLINTH_1");
            Fixture(AffineRect(Rect(-3.48, -2.8, -3.40, -.4), uR2BB, uR2CB), Z_UPPER_FINISH + .45, Math.Max(.75, Z_ROOF - Z_UPPER_FINISH - .90), "U_R2", "BLANK_DISPLAY_PANEL_1");
            var ugPlinths = sid == "A" ? new[]
            {
                Rect(12.15, 2.4, 12.95, 3.2),
                Rect(12.15, 9.6, 12.95, 10.4)
            }

            : new[]
            {
                Rect(9.15, 2.4, 9.95, 3.2),
                Rect(9.15, 11.2, 9.95, 12.0)
            };
            pi = 0;
            foreach (var p in ugPlinths)
                Fixture(AffineRect(p, uGBB, uGCB), Z_UPPER_FINISH, .95, "U_G", "NEUTRAL_PLINTH_" + (++pi));
            var ugPanel = sid == "A" ? Rect(13.40, -2.8, 13.48, -.4) : Rect(10.40, -2.8, 10.48, -.4);
            Fixture(AffineRect(ugPanel, uGBB, uGCB), Z_UPPER_FINISH + .45, Math.Max(.75, Z_ROOF - Z_UPPER_FINISH - .90), "U_G", "BLANK_DISPLAY_PANEL_1");
            // Six fixed 1:1 anchors, generated from scenario-local usable regions.
            var anchors = sid == "A" ? new[]
            {
                new Point3d(cW * .50, fBottom * .68, 1.65),
                new Point3d(rLeft * .55, cH * .42, 1.65),
                new Point3d(cW + (gEast - cW) * .55, cH * .40, 1.65),
                new Point3d(uRWest * .55, uRSouth + (cH - uRSouth) * .35, (Z_R_FINISH + Z_G) * .5),
                new Point3d(uRWest * .55, uRSouth + (cH - uRSouth) * .72, (Z_UPPER_FINISH + Z_ROOF) * .5),
                new Point3d(cW + (uGEast - cW) * .55, cH + (uGNorth - cH) * .55, (Z_UPPER_FINISH + Z_ROOF) * .5)
            }

            : new[]
            {
                new Point3d(fLeft + (cW - fLeft) * .50, fBottom * .68, 1.65),
                new Point3d(rLeft * .55, cH * .55, 1.65),
                new Point3d(cW + (gEast - cW) * .55, cH * .42, 1.65),
                new Point3d(uRWest * .55, uRSouth + (cH - uRSouth) * .45, (Z_R_FINISH + Z_G) * .5),
                new Point3d(uRWest * .55, uRSouth + (cH - uRSouth) * .72, (Z_UPPER_FINISH + Z_ROOF) * .5),
                new Point3d(cW + (uGEast - cW) * .55, cH + (uGNorth - cH) * .55, (Z_UPPER_FINISH + Z_ROOF) * .5)
            };
            string[] anchorLabels =
            {
                "F_1TO1",
                "R_1TO1",
                "G_1TO1",
                "U_R1_1TO1",
                "U_R2_1TO1",
                "U_G_1TO1"
            };
            for (int i = 0; i < anchors.Length; i++)
            {
                anchorPoints.Add(anchors[i]);
                anchorNames.Add(sid + "__ANCHOR__" + anchorLabels[i]);
            }

            // Unity starts use the first three room-local 1:1 anchors. View lines remain in
            // world coordinates, so the observer can perceive the room rotating around them.
            var unityStartPoints = new List<Point3d>
            {
                anchors[0],
                anchors[1],
                anchors[2]
            };
            var unityViewLines = new List<LineCurve>
            {
                new LineCurve(anchors[0], anchors[0] + new Vector3d(0, -3, 0)),
                new LineCurve(anchors[1], anchors[1] + new Vector3d(-3, 0, 0)),
                new LineCurve(anchors[2], anchors[2] + new Vector3d(3, 0, 0))
            };
            var p1Threshold = sid == "A" ? new Point3d(cW * .50, fBottom + 2.20 + p1ThresholdDelta, 1.65) : new Point3d(fLeft + (cW - fLeft) * .50, fBottom + 1.80 + p1ThresholdDelta, 1.65);
            var p3RouteNode = new Point3d(cW + p3RouteOffset, cH * .50, 1.65);
            anchorPoints.Add(p1Threshold);
            anchorNames.Add(sid + "__ANCHOR__P1_THRESHOLD_REVELATION");
            anchorPoints.Add(new Point3d(p2Anchor.X, p2Anchor.Y, 1.65));
            anchorNames.Add(sid + "__ANCHOR__P2_OCCUPIED_REFUGE");
            anchorPoints.Add(p3RouteNode);
            anchorNames.Add(sid + "__ANCHOR__P3_ROUTE_INTEGRATION");
            interfaces.Add(new LineCurve(p1Threshold, new Point3d(cW * .50, 0, 1.65)));
            interfaces.Add(new LineCurve(new Point3d(p2Anchor.X, p2Anchor.Y, 1.65), new Point3d(cW * .50, cH * .50, 1.65)));
            interfaces.Add(new LineCurve(new Point3d(0, cH * .50, 1.65), p3RouteNode));
            // Shared host floors remain continuous; no disconnected link patch is generated.
            // A single invertible projective taper maps ALL building parts once.
            // Shared points receive identical coordinates regardless of their owner.
            // Planes stay planar; floors stay horizontal. Ground z=0 and overall top
            // height is compensated to preserve the F/R/G envelope volume.
            // P1 revelation expands the upper envelope; P2 refuge contracts it;
            // P3 integration expands the upper envelope with a smaller coefficient.
            // Apply one reversible taper to all solids to keep mappings consistent across shared boundaries and owners.
            double taperRequested = .10 * q1 - .10 * q2 + .08 * q3;
            double taper = Math.Max(-.18, Math.Min(.18, taperRequested));
            double topScale = 1.0 + taper;
            double taperTop = Z_ROOF + VISIBLE_SLAB_T;
            double taperK = (1.0 / topScale - 1.0) / taperTop;
            double taperA = 1.0 + taperK * taperTop;
            double centreX = cW * .5, centreY = cH * .5;
            var taperTransform = Transform.Identity;
            taperTransform.M02 = centreX * taperK;
            taperTransform.M12 = centreY * taperK;
            taperTransform.M22 = taperA * volumeMap.verticalScale;
            taperTransform.M32 = taperK;
            Func<double, double> MapZ = z => volumeMap.Z(z);
            Func<Point3d, Point3d> MapPoint = p =>
            {
                double w = 1.0 + taperK * p.Z;
                return new Point3d(centreX + (p.X - centreX) / w, centreY + (p.Y - centreY) / w, MapZ(p.Z));
            };
            bool taperValid = 1.0 + taperK * (-.30) > 0 && 1.0 + taperK * (taperTop + .30) > 0;
            if (!taperValid)
                throw new InvalidOperationException("Invalid taper denominator");
            for (int i = 0; i < allFormal.Count; i++)
            {
                // Apply the same transform to structure, opening accessories, lighting and displays under one coordinate contract.
                if (!allFormal[i].Transform(taperTransform))
                    throw new InvalidOperationException("Taper failed: " + semanticNames[i]);
            }

            for (int i = 0; i < anchorPoints.Count; i++)
            {
                // Reposition transformed anchors to occupiable heights, avoiding locations below floors or above ceilings.
                var p = MapPoint(anchorPoints[i]);
                double floor = i == 3 ? Z_R_FINISH : (i == 4 || i == 5 ? Z_UPPER_FINISH : .06);
                double ceiling = i == 3 ? Z_G : (i == 4 || i == 5 ? Z_ROOF : (i == 1 || i == 7 ? Z_R : (i == 2 || i == 8 ? Z_G : Z_F)));
                p.Z = MapZ(floor) + Math.Min(1.65, (MapZ(ceiling) - MapZ(floor)) * .5);
                anchorPoints[i] = p;
            }

            for (int i = 0; i < unityStartPoints.Count; i++)
            {
                var oldLine = unityViewLines[i];
                var dir = MapPoint(oldLine.PointAtEnd) - MapPoint(oldLine.PointAtStart);
                dir.Z = 0;
                dir.Unitize();
                unityStartPoints[i] = anchorPoints[i];
                unityViewLines[i] = new LineCurve(anchorPoints[i], anchorPoints[i] + dir * 3.0);
            }

            return new Result
            {
                // Return rendering geometry, semantic names, navigation anchors, view rays, volume targets and portal-width validation data.
                groundFloorHeight = MapZ(.06),
                solids = allFormal,
                names = semanticNames,
                anchors = anchorPoints,
                anchorNames = anchorNames,
                starts = unityStartPoints,
                views = unityViewLines,
                p1 = effectiveP1,
                p2 = effectiveP2,
                p3 = effectiveP3,
                projection = "NONE",
                portalWidths = portalWidths.ToArray(),
                portalOwners = portalOwners.ToArray(),
                nominalVolume = volumeMap.targetVolume
            };
        }
    }

    // Separate training geometry avoids prior exposure to task A or B.
    public static class PracticeGeometry
    {
        /// <summary>
        /// Generate an independent simplified three-room practice model to avoid exposing participants to the formal A/B tasks beforehand.
        /// </summary>
        /// <param name="p">Practice parameters (p1/p2/p3).</param>
        /// <returns>Practice-mode geometry result.</returns>
        public static LiveGeometryKernel.Result Evaluate(SpatialState p)
        {
            var r = new LiveGeometryKernel.Result
            {
                solids = new List<GeometryBase>(),
                names = new List<string>(),
                starts = new List<Point3d>(),
                views = new List<LineCurve>(),
                anchors = new List<Point3d>(),
                anchorNames = new List<string>(),
                p1 = p.p1,
                p2 = p.p2,
                p3 = p.p3,
                groundFloorHeight = .06,
                projection = "PRACTICE",
                portalWidths = new[]
                {
                    2.2
                },
                portalOwners = new[]
                {
                    "PRACTICE"
                }
            };
            for (int room = 0; room < 3; room++)
            {
                // Apply lightweight parameter deformation to each practice room while retaining floors, walls, roofs and observation points.
                string owner = new[]
                {
                    "F",
                    "R",
                    "G"
                }[room];
                double x = room * 7, w = 5 + .3 * p[room], d = 5 - .2 * p[(room + 1) % 3], h = 3.6 + .2 * p[room];
                Add(r, owner, "SLB", "OWNED_FOUNDATION", x, 0, w, d, -.3, .3);
                Add(r, owner, "FLR", "GROUND_FINISH", x, 0, w, d, 0, .06);
                Add(r, owner, "WAL", "LEFT", x, 0, .2, d, 0, h);
                Add(r, owner, "WAL", "RIGHT", x + w - .2, 0, .2, d, 0, h);
                Add(r, owner, "WAL", "BACK", x, d - .2, w, .2, 0, h);
                string roof = room == 0 ? "VARIABLE_F_ROOF_WITH_SKYLIGHT" : room == 1 ? "ROOF_AND_U_R1_VISIBLE_STRUCTURE" : "ROOF_AND_U_G_VISIBLE_STRUCTURE";
                Add(r, owner, "SLB", roof, x, 0, w, d, h, .18);
                var start = new Point3d(x + w * .5, 1.2, 1.71);
                r.starts.Add(start);
                r.views.Add(new LineCurve(start, start + new Vector3d(0, -3, 0)));
                r.anchors.Add(start);
                r.anchorNames.Add("PRACTICE_" + owner);
                r.nominalVolume += w * d * h;
            }

            for (int i = 0; i < 6; i++)
            {
                r.anchors.Add(r.starts[i % 3]);
                r.anchorNames.Add("PRACTICE_CUE_" + i);
            }

            return r;
        }

        /// <summary>
        /// Append a rectangular prism component to the practice geometry result.
        /// </summary>
        /// <param name="r">Target geometry result container.</param>
        /// <param name="owner">Space owner, such as F/R/G.</param>
        /// <param name="cat">Component category, such as SLB/FLR/WAL.</param>
        /// <param name="role">Component semantic role.</param>
        /// <param name="x">Base rectangle starting X coordinate.</param>
        /// <param name="y">Base rectangle starting Y coordinate.</param>
        /// <param name="w">Base width along X.</param>
        /// <param name="d">Base depth along Y.</param>
        /// <param name="z">Base elevation.</param>
        /// <param name="h">Extrusion height.</param>
        static void Add(LiveGeometryKernel.Result r, string owner, string cat, string role, double x, double y, double w, double d, double z, double h)
        {
            // Practice components retain the formal naming convention but use scenario P (Practice).
            r.names.Add("P__" + cat + "__" + owner + "__" + role + "__01");
            r.solids.Add(Solid.Prism(new[] { new Point3d(x, y, z), new Point3d(x + w, y, z), new Point3d(x + w, y + d, z), new Point3d(x, y + d, z) }, h));
        }
    }
}
