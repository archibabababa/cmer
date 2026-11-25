using System;
using System.Collections.Generic;
using System.Linq;

// Lightweight polygon construction primitives; no Rhino, CAD or other external geometry runtime dependency.
namespace ArchSemanticGeometry
{
    public struct Point3d
    {
        public double X, Y, Z;
        public double DistanceTo(Point3d other) => (this - other).Length;
        public Point3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
        public static Point3d operator +(Point3d p, Vector3d v) => new Point3d(p.X + v.X, p.Y + v.Y, p.Z + v.Z);
        public static Point3d operator -(Point3d p, Vector3d v) => new Point3d(p.X - v.X, p.Y - v.Y, p.Z - v.Z);
        public static Vector3d operator -(Point3d p, Point3d q) => new Vector3d(p.X - q.X, p.Y - q.Y, p.Z - q.Z);
    }
    public struct Vector3d
    {
        public double X, Y, Z;
        public Vector3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public bool Unitize()
        {
            double l = Length;
            if (l < 1e-14)
                return false;
            X /= l;
            Y /= l;
            Z /= l;
            return true;
        }
        public static Vector3d XAxis => new Vector3d(1, 0, 0);
        public static Vector3d YAxis => new Vector3d(0, 1, 0);
        public static Vector3d operator *(Vector3d a, double b) => new Vector3d(a.X * b, a.Y * b, a.Z * b);
        public static Vector3d operator -(Vector3d a) => a * -1;
    }
    public struct Interval
    {
        public double Min, Max;
        public Interval(double a, double b)
        {
            Min = a;
            Max = b;
        }
    }
    public struct Plane
    {
        public Point3d Origin;
        public Vector3d X, Y;
        public Plane(Point3d o, Vector3d x, Vector3d y)
        {
            Origin = o;
            X = x;
            Y = y;
        }
    }
    public struct Transform
    {
        public double M00, M11, M22, M33, M02, M12, M03, M13, M32;
        public static Transform Identity => new Transform
        {
            M00 = 1,
            M11 = 1,
            M22 = 1,
            M33 = 1
        };
        public Point3d Apply(Point3d p)
        {
            double w = M32 * p.Z + M33;
            if (w <= 1e-12)
                throw new InvalidOperationException("Nonpositive projective denominator");
            return new Point3d((M00 * p.X + M02 * p.Z + M03) / w, (M11 * p.Y + M12 * p.Z + M13) / w, M22 * p.Z / w);
        }
    }
    public abstract class GeometryBase
    {
        public abstract bool Transform(Transform t);
    }
    public class Curve : GeometryBase
    {
        public List<Point3d> Points;
        public Curve(IEnumerable<Point3d> p)
        {
            Points = new List<Point3d>(p);
        }
        public override bool Transform(Transform t)
        {
            for (int i = 0; i < Points.Count; i++)
                Points[i] = t.Apply(Points[i]);
            return true;
        }
    }
    public sealed class PolylineCurve : Curve
    {
        public PolylineCurve(IEnumerable<Point3d> p) : base(p)
        {
        }
    }
    public sealed class LineCurve : Curve
    {
        public LineCurve(Point3d a, Point3d b) : base(new[] { a, b })
        {
        }
        public Point3d PointAtStart => Points[0];
        public Point3d PointAtEnd => Points[1];
    }
    public sealed class Vertex
    {
        public Point3d Location;
        public Vertex(Point3d p)
        {
            Location = p;
        }
    }
    public struct Bounds
    {
        public Point3d Min, Max;
    }
    public sealed class Solid : GeometryBase
    {
        public List<Vertex> Vertices = new List<Vertex>();
        public List<int[]> Faces = new List<int[]>();
        public Bounds GetBoundingBox(bool accurate)
        {
            return new Bounds
            {
                Min = new Point3d(Vertices.Min(v => v.Location.X), Vertices.Min(v => v.Location.Y), Vertices.Min(v => v.Location.Z)),
                Max = new Point3d(Vertices.Max(v => v.Location.X), Vertices.Max(v => v.Location.Y), Vertices.Max(v => v.Location.Z))
            };
        }
        public override bool Transform(Transform t)
        {
            foreach (var v in Vertices)
                v.Location = t.Apply(v.Location);
            return true;
        }
        public static Solid Prism(IEnumerable<Point3d> source, double height)
        {
            var p = new List<Point3d>(source);
            if (p.Count > 1 && (p[0] - p[p.Count - 1]).Length < 1e-10)
                p.RemoveAt(p.Count - 1);
            // Repeatedly remove collinear or duplicate contour vertices before triangulation to avoid degenerate faces.
            bool changed = true;
            while (changed && p.Count > 3)
            {
                changed = false;
                for (int i = 0; i < p.Count; i++)
                {
                    Point3d a = p[(i + p.Count - 1) % p.Count], b = p[i], c = p[(i + 1) % p.Count];
                    if (Math.Abs(Cross(a, b, c)) < 1e-10)
                    {
                        p.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }

            if (p.Count < 3 || height <= 0)
                throw new InvalidOperationException("Degenerate prism");
            double area = 0;
            for (int i = 0; i < p.Count; i++)
                area += p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y;
            if (area < 0)
                p.Reverse();
            var s = new Solid();
            int n = p.Count;
            foreach (var v in p)
                s.Vertices.Add(new Vertex(v));
            foreach (var v in p)
                s.Vertices.Add(new Vertex(new Point3d(v.X, v.Y, v.Z + height)));
            // Use right-handed winding in source geometry coordinates so face normals point outward.
            s.Faces.Add(Enumerable.Range(0, n).Reverse().ToArray());
            s.Faces.Add(Enumerable.Range(n, n).ToArray());
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                s.Faces.Add(new[] { i, j, j + n, i + n });
            }

            return s;
        }
        private static double Cross(Point3d a, Point3d b, Point3d c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        public List<int> Triangles()
        {
            var result = new List<int>();
            foreach (var face in Faces)
            {
                if (face.Length == 4)
                {
                    result.AddRange(new[] { face[0], face[1], face[2], face[0], face[2], face[3] });
                    continue;
                }

                if (face.Length == 3)
                {
                    result.AddRange(face);
                    continue;
                }

                // Side faces are already triangles or quads; only the caps may need general triangulation.
                // Ear-clip potentially concave caps in XY; extrusion and taper preserve their horizontal contours.
                var ids = new List<int>(face);
                double area = 0;
                for (int i = 0; i < ids.Count; i++)
                {
                    var a = Vertices[ids[i]].Location;
                    var b = Vertices[ids[(i + 1) % ids.Count]].Location;
                    area += a.X * b.Y - b.X * a.Y;
                }

                // Adjust the convex-ear test for polygon winding to support both clockwise and counterclockwise contours.
                double sign = area > 0 ? 1 : -1;
                while (ids.Count > 3)
                {
                    bool ear = false;
                    for (int i = 0; i < ids.Count; i++)
                    {
                        // Use the current vertex and its neighbours as the candidate triangle; concave or collinear corners are not ears.
                        int ai = ids[(i + ids.Count - 1) % ids.Count], bi = ids[i], ci = ids[(i + 1) % ids.Count];
                        var a = Vertices[ai].Location;
                        var b = Vertices[bi].Location;
                        var c = Vertices[ci].Location;
                        if (sign * Cross(a, b, c) <= 1e-12)
                            continue;
                        // Do not clip a candidate ear containing another contour vertex.
                        bool inside = false;
                        foreach (int j in ids)
                        {
                            if (j == ai || j == bi || j == ci)
                                continue;
                            var q = Vertices[j].Location;
                            if (sign * Cross(a, b, q) >= -1e-12 && sign * Cross(b, c, q) >= -1e-12 && sign * Cross(c, a, q) >= -1e-12)
                            {
                                inside = true;
                                break;
                            }
                        }

                        if (inside)
                            continue;
                        result.Add(ai);
                        result.Add(bi);
                        result.Add(ci);
                        ids.RemoveAt(i);
                        ear = true;
                        break;
                    }

                    // A complete pass without a valid ear means the contour cannot be reliably triangulated.
                    if (!ear)
                        throw new InvalidOperationException("Cannot triangulate cap; footprint may self-intersect");
                }

                result.AddRange(ids);
            }

            return result;
        }
    }
    public sealed class Extrusion : GeometryBase
    {
        private Solid solid;
        public static Extrusion Create(PolylineCurve c, double h, bool cap) => new Extrusion
        {
            solid = Solid.Prism(c.Points, h)
        };
        public Solid ToBrep() => solid;
        public override bool Transform(Transform t) => solid.Transform(t);
    }
    public struct Box
    {
        // Local box coordinate system and X/Y/Z extents.
        private Plane plane;
        private Interval x, y, z;
        public Box(Plane p, Interval xx, Interval yy, Interval zz)
        {
            plane = p;
            x = xx;
            y = yy;
            z = zz;
        }
        public Solid ToBrep()
        {
            var origin = new Point3d(plane.Origin.X, plane.Origin.Y, plane.Origin.Z + z.Min);
            return Solid.Prism(new[] { origin + plane.X * x.Min + plane.Y * y.Min, origin + plane.X * x.Max + plane.Y * y.Min, origin + plane.X * x.Max + plane.Y * y.Max, origin + plane.X * x.Min + plane.Y * y.Max }, z.Max - z.Min);
        }
    }
}
