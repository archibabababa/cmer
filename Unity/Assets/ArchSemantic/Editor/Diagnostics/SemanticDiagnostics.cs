using System;
using System.Collections.Generic;
using System.Linq;
using ArchSemanticGeometry;

namespace ArchSemanticPilot
{
    [Serializable]
    public sealed class SemanticProbe
    {
        public int scenario, axis;
        public double requested;
        public SpatialState effective;
        public string projection;
        public double firstCourtyardVisibilityMetres;
        public int refugeBlockedDirectionsOfEight, crossRoomVisiblePairsOfSix;
        public GeometryMetrics geometry;
    }

    public static class SemanticDiagnostics
    {
        // Explicit geometric proxies for later pilot calibration; these are not IRG,
        // prospect/refuge ratings, validated semantic effects, or measured eye gaze.
        public static SemanticProbe Probe(int scenario, int axis, double requested)
        {
            string reason = "NONE";
            SpatialState state = default;
            if (axis >= 0)
                state = GeometryDomain.SolveActive(scenario, default, axis, requested, out reason);
            var r = LiveGeometryKernel.Evaluate(scenario, state.p1, state.p2, state.p3);
            var metrics = GeometryValidation.Measure(r);
            var triangles = new List<Point3d[]>();
            for (int i = 0; i < r.names.Count; i++)
            {
                string category = r.names[i].Split(new[] { "__" }, StringSplitOptions.None)[1];
                if (category != "WAL" && category != "SLB" && category != "FIX")
                    continue;
                var solid = (Solid)r.solids[i];
                var t = solid.Triangles();
                for (int j = 0; j < t.Count; j += 3)
                    triangles.Add(new[] { solid.Vertices[t[j]].Location, solid.Vertices[t[j + 1]].Location, solid.Vertices[t[j + 2]].Location });
            }

            var probe = new SemanticProbe
            {
                scenario = scenario,
                axis = axis,
                requested = requested,
                effective = state,
                projection = reason,
                geometry = metrics,
                firstCourtyardVisibilityMetres = -1
            };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (i != j && Clear(r.starts[i], r.starts[j], triangles))
                        probe.crossRoomVisiblePairsOfSix++;
            var anchor = r.anchors[7];
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                var target = new Point3d(anchor.X + 5 * Math.Cos(a), anchor.Y + 5 * Math.Sin(a), anchor.Z);
                if (!Clear(anchor, target, triangles))
                    probe.refugeBlockedDirectionsOfEight++;
            }

            var court = metrics.rooms.First(room => room.room == "C");
            double cx = 0, cy = 0;
            int count = court.footprintXY.Length / 2;
            for (int i = 0; i < count; i++)
            {
                cx += court.footprintXY[i * 2] / count;
                cy += court.footprintXY[i * 2 + 1] / count;
            }

            var origin = r.anchors[6];
            var endpoint = new Point3d(cx, cy, origin.Z);
            double length = Math.Sqrt((cx - origin.X) * (cx - origin.X) + (cy - origin.Y) * (cy - origin.Y));
            for (int step = 0; step <= 12; step++)
            {
                double distance = step * .5;
                if (distance >= length - .1)
                    break;
                double t = distance / length;
                var from = new Point3d(origin.X + (cx - origin.X) * t, origin.Y + (cy - origin.Y) * t, origin.Z);
                if (Clear(from, endpoint, triangles))
                {
                    probe.firstCourtyardVisibilityMetres = distance;
                    break;
                }
            }

            return probe;
        }

        static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        static Vector3d Cross(Vector3d a, Vector3d b) => new Vector3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        static bool Clear(Point3d from, Point3d to, List<Point3d[]> triangles)
        {
            var ray = to - from;
            foreach (var triangle in triangles)
            {
                var edge1 = triangle[1] - triangle[0];
                var edge2 = triangle[2] - triangle[0];
                var p = Cross(ray, edge2);
                double det = Dot(edge1, p);
                if (Math.Abs(det) < 1e-10)
                    continue;
                double inverse = 1 / det;
                var s = from - triangle[0];
                double u = Dot(s, p) * inverse;
                if (u < 0 || u > 1)
                    continue;
                var q = Cross(s, edge1);
                double v = Dot(ray, q) * inverse;
                if (v < 0 || u + v > 1)
                    continue;
                double t = Dot(edge2, q) * inverse;
                if (t > 1e-5 && t < 1 - 1e-5)
                    return false;
            }

            return true;
        }
    }
}
