using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ArchSemanticGeometry;

namespace ArchSemanticPilot
{
    [Serializable]
    public sealed class RoomMetrics
    {
        public string room;
        public double area, height, volume;
        public double[] footprintXY;
        public bool Contains(double x, double y)
        {
            bool inside = false;
            int n = footprintXY.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = footprintXY[2 * i], yi = footprintXY[2 * i + 1], xj = footprintXY[2 * j], yj = footprintXY[2 * j + 1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                    inside = !inside;
            }

            return inside;
        }
    }

    [Serializable]
    public sealed class GeometryMetrics
    {
        public string geometryHash;
        public double nominalVolume, expectedVolume, volumeError, minimumPortal;
        public int triangles, objects, primaryRouteCells;
        public double primaryRouteWidth;
        public RoomMetrics[] rooms;
        public string Zone(double x, double y)
        {
            foreach (var r in rooms)
                if (r.Contains(x, y))
                    return r.room;
            return "OUTSIDE";
        }
    }

    public static class GeometryValidation
    {
        public static GeometryMetrics Measure(LiveGeometryKernel.Result r, bool practice = false, bool verifyRoute = true)
        {
            var metrics = new GeometryMetrics
            {
                objects = r.names.Count,
                expectedVolume = r.nominalVolume,
                minimumPortal = r.portalWidths.Length > 0 ? r.portalWidths.Min() : 0
            };
            if (r.names.Count != r.solids.Count || r.names.Distinct().Count() != r.names.Count)
                throw new InvalidDataException("Invalid semantic object identities.");
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes))
            {
                for (int i = 0; i < r.names.Count; i++)
                {
                    writer.Write(r.names[i]);
                    var solid = (Solid)r.solids[i];
                    writer.Write(solid.Vertices.Count);
                    foreach (var v in solid.Vertices)
                    {
                        var p = v.Location;
                        if (!GeometryDomain.Finite(p.X) || !GeometryDomain.Finite(p.Y) || !GeometryDomain.Finite(p.Z))
                            throw new InvalidDataException("Non-finite mesh.");
                        writer.Write((float)p.X);
                        writer.Write((float)p.Z);
                        writer.Write((float)p.Y);
                    }

                    var t = solid.Triangles();
                    metrics.triangles += t.Count / 3;
                    writer.Write(t.Count);
                    foreach (int index in t)
                        writer.Write(index);
                }

                writer.Flush();
                bytes.Position = 0;
                using (var sha = SHA256.Create())
                    metrics.geometryHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }

            var rooms = r.cageState == null ? new List<RoomMetrics>() : LiveGeometryKernel.MeasureRooms(r).ToList();
            if (r.cageState == null)
            foreach (string owner in new[]
            {
                "F",
                "R",
                "G",
                "C"
            }

            )
            {
                int fi = r.names.FindIndex(n => n.Contains("__SLB__" + owner + "__" + (owner == "C" ? "COURTYARD_BASE" : "OWNED_FOUNDATION")));
                if (fi < 0)
                    continue;
                var floor = ((Solid)r.solids[fi]).Vertices.Select(v => v.Location).Where(p => Math.Abs(p.Z) < 1e-7).ToArray();
                double area = 0;
                for (int i = 0; i < floor.Length; i++)
                    area += floor[i].X * floor[(i + 1) % floor.Length].Y - floor[(i + 1) % floor.Length].X * floor[i].Y;
                var room = new RoomMetrics
                {
                    room = owner,
                    area = Math.Abs(area) / 2,
                    footprintXY = floor.SelectMany(p => new[] { p.X, p.Y }).ToArray()
                };
                if (owner != "C")
                {
                    string role = owner == "F" ? "VARIABLE_F_ROOF_WITH_SKYLIGHT" : owner == "R" ? "ROOF_AND_U_R1_VISIBLE_STRUCTURE" : "ROOF_AND_U_G_VISIBLE_STRUCTURE";
                    var roof = new List<Point3d>();
                    for (int i = 0; i < r.names.Count; i++)
                        if (r.names[i].Contains("__SLB__" + owner + "__" + role))
                        {
                            var solid = (Solid)r.solids[i];
                            double z = solid.Vertices.Min(v => v.Location.Z);
                            roof.AddRange(solid.Vertices.Select(v => v.Location).Where(p => Math.Abs(p.Z - z) < 1e-7));
                        }

                    if (roof.Count == 0)
                        throw new InvalidDataException("Missing room roof.");
                    double sx = (roof.Max(p => p.X) - roof.Min(p => p.X)) / (floor.Max(p => p.X) - floor.Min(p => p.X)), sy = (roof.Max(p => p.Y) - roof.Min(p => p.Y)) / (floor.Max(p => p.Y) - floor.Min(p => p.Y));
                    if (Math.Abs(sx - sy) > 1e-6)
                        throw new InvalidDataException("Envelope mapping is not a uniform plan taper.");
                    room.height = roof.Min(p => p.Z);
                    room.volume = room.area * room.height * (1 + sx + sx * sx) / 3;
                    metrics.nominalVolume += room.volume;
                }

                rooms.Add(room);
            }

            if (r.cageState != null) metrics.nominalVolume = rooms.Sum(x => x.volume);
            metrics.rooms = rooms.ToArray();
            metrics.volumeError = Math.Abs(metrics.nominalVolume - metrics.expectedVolume);
            if (!practice && (metrics.volumeError > .001 || metrics.minimumPortal < GeometryDomain.MinimumPortal - 1e-7))
                throw new InvalidDataException("Final geometry violates volume or portal contract.");
            if (!practice && verifyRoute)
            {
                metrics.primaryRouteCells = RouteValidation.Check(r, metrics);
                metrics.primaryRouteWidth = 1.2;
            }

            return metrics;
        }
    }
    public static class FloorNavigation
    {
        public static bool Find(LiveGeometryKernel.Result result, double x, double y, double ceiling,
            string requiredOwner, out string owner, out double height)
        {
            owner = null;
            height = double.NegativeInfinity;
            if (!GeometryDomain.Finite(x) || !GeometryDomain.Finite(y) || !GeometryDomain.Finite(ceiling)) return false;
            for (int i = 0; i < result.solids.Count; i++)
            {
                if (result.names[i].IndexOf("__FLR__", StringComparison.Ordinal) < 0 &&
                    !(result.cageState != null && (result.names[i].Contains("__FOUNDATION") || result.names[i].Contains("__COURTYARD__") || result.names[i].Contains("__FLOOR__")))) continue;
                var name = result.names[i].Split(new[] { "__" }, StringSplitOptions.None);
                if (name.Length < 3 || (name[1] != "FLR" && result.cageState == null) || requiredOwner != null && name[2] != requiredOwner) continue;
                var solid = (Solid)result.solids[i];
                var bounds = solid.GetBoundingBox(true);
                if (x < bounds.Min.X || x > bounds.Max.X || y < bounds.Min.Y || y > bounds.Max.Y) continue;
                double top = solid.Vertices.Max(v => v.Location.Z);
                if (top > ceiling + .001 || top < height) continue;
                var triangles = solid.Triangles();
                for (int t = 0; t < triangles.Count; t += 3)
                {
                    var a = solid.Vertices[triangles[t]].Location;
                    var b = solid.Vertices[triangles[t + 1]].Location;
                    var c = solid.Vertices[triangles[t + 2]].Location;
                    if (Math.Abs(a.Z - top) > 1e-6 || Math.Abs(b.Z - top) > 1e-6 || Math.Abs(c.Z - top) > 1e-6) continue;
                    double d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                    if (Math.Abs(d) < 1e-12) continue;
                    double u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
                    double v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
                    if (u < -1e-7 || v < -1e-7 || u + v > 1.0000001) continue;
                    owner = name[2]; height = top; break;
                }
            }
            return owner != null;
        }

        public static bool Supported(LiveGeometryKernel.Result result, double x, double y, string owner, out double height)
        {
            if (!Find(result, x, y, 100, owner, out _, out height)) return false;
            foreach (var offset in new[] { new[] { .26, 0.0 }, new[] { -.26, 0.0 }, new[] { 0.0, .26 }, new[] { 0.0, -.26 } })
                if (!Find(result, x + offset[0], y + offset[1], height + .01, owner, out _, out double edge) || Math.Abs(edge - height) > .02) return false;
            return true;
        }
    }

    // Grid-based ground passage check.
    // Cells and obstacles are inflated, so a connected path certifies a 1.2m-wide
    // corridor at 1.8m standing height. Upper-storey access is a separate task.
    public static class RouteValidation
    {
        sealed class Grid
        {
            public double minX, minY;
            public int nx, ny;
            public bool[] free;
        }

        static Grid BuildGrid(LiveGeometryKernel.Result r, GeometryMetrics metrics, Grid bounds = null)
        {
            const double step = .25, half = .6;
            double margin = half + step * .5;
            var all = metrics.rooms.SelectMany(room => Enumerable.Range(0, room.footprintXY.Length / 2).Select(i => new Point3d(room.footprintXY[i * 2], room.footprintXY[i * 2 + 1], 0))).ToArray();
            double minX = bounds == null ? all.Min(p => p.X) : bounds.minX, minY = bounds == null ? all.Min(p => p.Y) : bounds.minY;
            int nx = bounds == null ? (int)Math.Ceiling((all.Max(p => p.X) - minX) / step) + 1 : bounds.nx, ny = bounds == null ? (int)Math.Ceiling((all.Max(p => p.Y) - minY) / step) + 1 : bounds.ny;
            var free = new bool[nx * ny];
            Func<double, double, bool> supported = (x, y) => metrics.rooms.Any(room => room.Contains(x, y));
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    double px = minX + x * step, py = minY + y * step;
                    bool ok = true;
                    foreach (double ox in new[]
                    {
                        -margin,
                        0,
                        margin
                    }

                    )
                        foreach (double oy in new[]
                        {
                            -margin,
                            0,
                            margin
                        }

                        )
                            if (!supported(px + ox, py + oy))
                                ok = false;
                    free[y * nx + x] = ok;
                }

            double low = r.groundFloorHeight + .04, high = r.groundFloorHeight + 1.8;
            for (int i = 0; i < r.solids.Count; i++)
            {
                string category = r.names[i].Split(new[] { "__" }, StringSplitOptions.None)[1];
                if (category != "WAL" && category != "GLS" && category != "FRM" && category != "DOR" && category != "FIX")
                    continue;
                var s = (Solid)r.solids[i];
                var pts = new List<Point3d>();
                foreach (var face in s.Faces)
                    for (int j = 0; j < face.Length; j++)
                    {
                        var a = s.Vertices[face[j]].Location;
                        var b = s.Vertices[face[(j + 1) % face.Length]].Location;
                        if (a.Z >= low && a.Z <= high)
                            pts.Add(a);
                        foreach (double h in new[]
                        {
                            low,
                            high
                        }

                        )
                            if ((a.Z < h && b.Z > h) || (a.Z > h && b.Z < h))
                            {
                                double t = (h - a.Z) / (b.Z - a.Z);
                                pts.Add(new Point3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, h));
                            }
                    }

                if (pts.Count == 0)
                    continue;
                int x0 = Math.Max(0, (int)Math.Ceiling((pts.Min(p => p.X) - margin - minX) / step)), x1 = Math.Min(nx - 1, (int)Math.Floor((pts.Max(p => p.X) + margin - minX) / step));
                int y0 = Math.Max(0, (int)Math.Ceiling((pts.Min(p => p.Y) - margin - minY) / step)), y1 = Math.Min(ny - 1, (int)Math.Floor((pts.Max(p => p.Y) + margin - minY) / step));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        free[y * nx + x] = false;
            }

            return new Grid { minX = minX, minY = minY, nx = nx, ny = ny, free = free };
        }

        static int[] Targets(Grid grid, LiveGeometryKernel.Result r, GeometryMetrics metrics, int count)
        {
            const double step = .25;
            double minX = grid.minX, minY = grid.minY;
            int nx = grid.nx, ny = grid.ny;
            var free = grid.free;
            int[] targets = new int[count];
            for (int room = 0; room < count; room++)
            {
                double distance = double.MaxValue;
                targets[room] = -1;
                var p = room < 3 ? r.starts[room] : r.anchors[room + 3];
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        if (free[y * nx + x])
                        {
                            double px = minX + x * step, py = minY + y * step;
                            if (!metrics.rooms[room % 3].Contains(px, py))
                                continue;
                            double d = (px - p.X) * (px - p.X) + (py - p.Y) * (py - p.Y);
                            if (d < distance)
                            {
                                distance = d;
                                targets[room] = y * nx + x;
                            }
                        }

                if (targets[room] < 0 || distance > 4)
                    throw new InvalidDataException("No 1.2m primary route near room " + metrics.rooms[room % 3].room);
            }

            return targets;
        }

        public static void PlaceStarts(LiveGeometryKernel.Result r, GeometryMetrics metrics)
        {
            var grid = BuildGrid(r, metrics);
            var targets = Targets(grid, r, metrics, 3);
            var courtyard = metrics.rooms.First(x => x.room == "C");
            var center = new Point3d(courtyard.footprintXY.Where((_, i) => i % 2 == 0).Average(),
                courtyard.footprintXY.Where((_, i) => i % 2 == 1).Average(), r.groundFloorHeight + 1.65);
            for (int i = 0; i < 3; i++)
            {
                var point = new Point3d(grid.minX + targets[i] % grid.nx * .25,
                    grid.minY + targets[i] / grid.nx * .25, r.groundFloorHeight + 1.65);
                r.starts[i] = point; r.anchors[i] = point; r.anchors[6 + i] = point;
                // The existing navigation layer faces opposite the stored view vector.
                r.views[i] = new LineCurve(point, new Point3d(2*point.X-center.X,2*point.Y-center.Y,point.Z));
            }
        }

        public static int Check(LiveGeometryKernel.Result r, GeometryMetrics metrics)
        {
            var grid = BuildGrid(r, metrics);
            int nx = grid.nx, ny = grid.ny;
            var free = grid.free;
            var targets = Targets(grid, r, metrics, 3);
            var seen = new bool[free.Length];
            var queue = new Queue<int>();
            queue.Enqueue(targets[0]);
            seen[targets[0]] = true;
            int cells = 0;
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                cells++;
                int x = cell % nx, y = cell / nx;
                foreach (int next in new[]
                {
                    x > 0 ? cell - 1 : -1,
                    x < nx - 1 ? cell + 1 : -1,
                    y > 0 ? cell - nx : -1,
                    y < ny - 1 ? cell + nx : -1
                }

                )
                    if (next >= 0 && free[next] && !seen[next])
                    {
                        seen[next] = true;
                        queue.Enqueue(next);
                    }
            }

            if (!seen[targets[1]] || !seen[targets[2]])
                throw new InvalidDataException("Primary 1.2m route does not connect F/R/G");
            return cells;
        }

        // A paired initial/final export shares exactly the same camera path. The
        // intersection of clear cells prevents a changed wall from clipping only
        // one condition. If no common route exists, export fails explicitly.
        public static List<Point3d> PairedReviewPath(LiveGeometryKernel.Result initial, LiveGeometryKernel.Result final)
        {
            var metrics = GeometryValidation.Measure(initial, false, false);
            var grid = BuildGrid(initial, metrics);
            var other = BuildGrid(final, GeometryValidation.Measure(final, false, false), grid);
            for (int i = 0; i < grid.free.Length; i++) grid.free[i] &= other.free[i];
            var targets = Targets(grid, initial, metrics, 6);
            var path = new List<Point3d>();
            double eye = Math.Max(initial.groundFloorHeight, final.groundFloorHeight) + 1.65;
            for (int leg = 1; leg < targets.Length; leg++)
            {
                var previous = Enumerable.Repeat(-1, grid.free.Length).ToArray();
                var queue = new Queue<int>();
                int from = targets[leg - 1], to = targets[leg];
                previous[from] = from;
                queue.Enqueue(from);
                while (queue.Count > 0 && previous[to] < 0)
                {
                    int cell = queue.Dequeue(), x = cell % grid.nx, y = cell / grid.nx;
                    foreach (int next in new[] { x > 0 ? cell - 1 : -1, x + 1 < grid.nx ? cell + 1 : -1, y > 0 ? cell - grid.nx : -1, y + 1 < grid.ny ? cell + grid.nx : -1 })
                        if (next >= 0 && grid.free[next] && previous[next] < 0)
                        { previous[next] = cell; queue.Enqueue(next); }
                }
                if (previous[to] < 0) throw new InvalidDataException("No shared 1.2m route for the paired review.");
                var legCells = new List<int>();
                for (int cell = to; cell != from; cell = previous[cell]) legCells.Add(cell);
                legCells.Add(from); legCells.Reverse();
                foreach (int cell in legCells.Skip(path.Count == 0 ? 0 : 1))
                    path.Add(new Point3d(grid.minX + cell % grid.nx * .25, grid.minY + cell / grid.nx * .25, eye));
            }
            if (path.Count < 2) throw new InvalidDataException("Review path has no movement.");
            return path;
        }

    }
}
