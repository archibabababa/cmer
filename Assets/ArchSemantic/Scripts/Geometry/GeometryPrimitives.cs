using System;
using System.Collections.Generic;
using System.Linq;

// Lightweight polygon construction primitives; no Rhino, CAD or other external geometry runtime dependency.
namespace ArchSemanticGeometry
{
    /// <summary>A three-dimensional point with double-precision coordinates.</summary>
    public struct Point3d
    {
        /// <summary>X, Y and Z components in geometry coordinates.</summary>
        public double X, Y, Z;

        /// <summary>Create a point from three coordinate components.</summary>
        /// <param name="x">Point X coordinate.</param>
        /// <param name="y">Point Y coordinate.</param>
        /// <param name="z">Point Z coordinate.</param>
        public Point3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>Translate a point by a vector to produce a new point.</summary>
        /// <param name="p">Point to translate.</param>
        /// <param name="v">Translation vector.</param>
        /// <returns>Translated point.</returns>
        public static Point3d operator +(Point3d p, Vector3d v) => new Point3d(p.X + v.X, p.Y + v.Y, p.Z + v.Z);

        /// <summary>Subtract a translation vector from a point to produce a new point.</summary>
        /// <param name="p">Original point.</param>
        /// <param name="v">Vector to subtract.</param>
        /// <returns>Point translated in the opposite direction.</returns>
        public static Point3d operator -(Point3d p, Vector3d v) => new Point3d(p.X - v.X, p.Y - v.Y, p.Z - v.Z);

        /// <summary>Subtract two points to obtain the vector from the second point to the first.</summary>
        /// <param name="p">Vector end point.</param>
        /// <param name="q">Vector start point.</param>
        /// <returns>Displacement vector from q to p.</returns>
        public static Vector3d operator -(Point3d p, Point3d q) => new Vector3d(p.X - q.X, p.Y - q.Y, p.Z - q.Z);
    }

    /// <summary>Double-precision three-dimensional vector and basic operations.</summary>
    public struct Vector3d
    {
        /// <summary>Vector components along X, Y and Z.</summary>
        public double X, Y, Z;

        /// <summary>Create a vector from three components.</summary>
        /// <param name="x">X component.</param>
        /// <param name="y">Y component.</param>
        /// <param name="z">Z component.</param>
        public Vector3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>Euclidean length (magnitude) of the vector.</summary>
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        /// <summary>Normalize the current vector in place.</summary>
        /// <returns>True when normalization is possible; a near-zero vector is left unchanged and returns false.</returns>
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

        /// <summary>Positive X unit vector.</summary>
        public static Vector3d XAxis => new Vector3d(1, 0, 0);
        /// <summary>Positive Y unit vector.</summary>
        public static Vector3d YAxis => new Vector3d(0, 1, 0);

        /// <summary>Multiply each vector component by a scalar.</summary>
        /// <param name="a">Vector to scale.</param>
        /// <param name="b">Scale factor.</param>
        /// <returns>Scaled vector.</returns>
        public static Vector3d operator *(Vector3d a, double b) => new Vector3d(a.X * b, a.Y * b, a.Z * b);

        /// <summary>Reverse the vector direction.</summary>
        /// <param name="a">Vector to reverse.</param>
        /// <returns>Vector of equal length in the opposite direction.</returns>
        public static Vector3d operator -(Vector3d a) => a * -1;
    }

    /// <summary>Numeric interval bounded by minimum and maximum values.</summary>
    public struct Interval
    {
        /// <summary>Interval endpoints; this type does not enforce their ordering.</summary>
        public double Min, Max;

        /// <summary>Create an interval with the specified endpoints.</summary>
        /// <param name="a">First interval endpoint.</param>
        /// <param name="b">Second interval endpoint.</param>
        public Interval(double a, double b)
        {
            Min = a;
            Max = b;
        }
    }

    /// <summary>Local plane coordinate system defined by an origin and two in-plane basis vectors.</summary>
    public struct Plane
    {
        /// <summary>Origin of the local plane coordinate system.</summary>
        public Point3d Origin;
        /// <summary>Local X and Y basis vectors.</summary>
        public Vector3d X, Y;

        /// <summary>Create a plane from its origin and two basis vectors.</summary>
        /// <param name="o">Plane coordinate origin.</param>
        /// <param name="x">Local plane X direction.</param>
        /// <param name="y">Local plane Y direction.</param>
        public Plane(Point3d o, Vector3d x, Vector3d y)
        {
            Origin = o;
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// Sparse projective transformation matrix for geometry points.
    /// Unlisted entries are zero; M32, M33 and the point Z component determine the homogeneous denominator.
    /// </summary>
    public struct Transform
    {
        /// <summary>Nonzero matrix entries used by this transform.</summary>
        public double M00, M11, M22, M33, M02, M12, M03, M13, M32;

        /// <summary>Identity transform, leaving point positions unchanged.</summary>
        public static Transform Identity => new Transform
        {
            M00 = 1,
            M11 = 1,
            M22 = 1,
            M33 = 1
        };

        /// <summary>Transform a point and perform homogeneous division.</summary>
        /// <param name="p">Input point to transform.</param>
        /// <returns>Transformed point.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the homogeneous denominator is not positive.</exception>
        public Point3d Apply(Point3d p)
        {
            double w = M32 * p.Z + M33;
            if (w <= 1e-12)
                throw new InvalidOperationException("Nonpositive projective denominator");
            return new Point3d((M00 * p.X + M02 * p.Z + M03) / w, (M11 * p.Y + M12 * p.Z + M13) / w, M22 * p.Z / w);
        }
    }

    /// <summary>Abstract base class for transformable geometry.</summary>
    public abstract class GeometryBase
    {
        /// <summary>Apply a transform to the geometry in place.</summary>
        /// <param name="t">Sparse projective transform to apply.</param>
        /// <returns>True on successful transformation.</returns>
        public abstract bool Transform(Transform t);
    }

    /// <summary>Basic curve represented by an ordered collection of 3D points.</summary>
    public class Curve : GeometryBase
    {
        /// <summary>Ordered control points or polyline vertices.</summary>
        public List<Point3d> Points;

        /// <summary>Create a curve by copying the input point sequence.</summary>
        /// <param name="p">3D points in curve order.</param>
        public Curve(IEnumerable<Point3d> p)
        {
            Points = new List<Point3d>(p);
        }

        /// <summary>Transform each point, updating the curve's internal point collection.</summary>
        /// <param name="t">Transform to apply to each curve point.</param>
        /// <returns>True after completing the transformation.</returns>
        public override bool Transform(Transform t)
        {
            for (int i = 0; i < Points.Count; i++)
                Points[i] = t.Apply(Points[i]);
            return true;
        }
    }

    /// <summary>Polyline curve connecting ordered vertices.</summary>
    public sealed class PolylineCurve : Curve
    {
        /// <summary>Create a polyline from a point sequence.</summary>
        /// <param name="p">Vertices in polyline order.</param>
        public PolylineCurve(IEnumerable<Point3d> p) : base(p)
        {
        }
    }

    /// <summary>Line segment defined by its start and end points.</summary>
    public sealed class LineCurve : Curve
    {
        /// <summary>Create a segment joining two endpoints.</summary>
        /// <param name="a">Segment start point.</param>
        /// <param name="b">Segment end point.</param>
        public LineCurve(Point3d a, Point3d b) : base(new[] { a, b })
        {
        }

        /// <summary>Get the segment start point.</summary>
        public Point3d PointAtStart => Points[0];
        /// <summary>Get the segment end point.</summary>
        public Point3d PointAtEnd => Points[1];
    }

    /// <summary>A polyhedral vertex storing its 3D position.</summary>
    public sealed class Vertex
    {
        /// <summary>Vertex position.</summary>
        public Point3d Location;

        /// <summary>Create a vertex at the specified position.</summary>
        /// <param name="p">Vertex 3D coordinates.</param>
        public Vertex(Point3d p)
        {
            Location = p;
        }
    }

    /// <summary>Axis-aligned bounding box represented by minimum and maximum points.</summary>
    public struct Bounds
    {
        /// <summary>Minimum and maximum points along each coordinate axis.</summary>
        public Point3d Min, Max;
    }

    /// <summary>Closed polyhedral solid defined by vertices and face indices.</summary>
    public sealed class Solid : GeometryBase
    {
        /// <summary>Shared vertex table referenced by face indices.</summary>
        public List<Vertex> Vertices = new List<Vertex>();
        /// <summary>Vertex indices of each face; faces may be triangles, quads or polygons.</summary>
        public List<int[]> Faces = new List<int[]>();

        /// <summary>Compute an axis-aligned bounding box from the current vertices.</summary>
        /// <param name="accurate">Compatibility parameter; this implementation always scans every vertex directly.</param>
        /// <returns>Minimum and maximum coordinates enclosing every vertex.</returns>
        public Bounds GetBoundingBox(bool accurate)
        {
            return new Bounds
            {
                Min = new Point3d(Vertices.Min(v => v.Location.X), Vertices.Min(v => v.Location.Y), Vertices.Min(v => v.Location.Z)),
                Max = new Point3d(Vertices.Max(v => v.Location.X), Vertices.Max(v => v.Location.Y), Vertices.Max(v => v.Location.Z))
            };
        }

        /// <summary>Transform every vertex without changing face indices.</summary>
        /// <param name="t">Transform to apply to each vertex.</param>
        /// <returns>True after completing the transformation.</returns>
        public override bool Transform(Transform t)
        {
            foreach (var v in Vertices)
                v.Location = t.Apply(v.Location);
            return true;
        }

        /// <summary>
        /// Extrude an XY polygon in positive Z to create a prism with top, bottom and side faces.
        /// Remove duplicate closing and collinear points and normalize contour winding before construction.
        /// </summary>
        /// <param name="source">Ordered base contour; a repeated closing point is allowed.</param>
        /// <param name="height">Positive extrusion height along Z.</param>
        /// <returns>Closed polyhedron generated from the cleaned contour.</returns>
        /// <exception cref="InvalidOperationException">Thrown for fewer than three valid contour points or an invalid extrusion height.</exception>
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

        /// <summary>Compute the signed XY cross product of a, b and c for turn and area orientation tests.</summary>
        /// <param name="a">Starting point of the directed segment.</param>
        /// <param name="b">Middle point of the directed segment.</param>
        /// <param name="c">Third point used to determine the turn.</param>
        /// <returns>Positive for a counterclockwise turn, negative for clockwise, near zero for collinearity.</returns>
        private static double Cross(Point3d a, Point3d b, Point3d c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        /// <summary>
        /// Convert all solid faces to triangle indices for rendering.
        /// Copy triangles directly, split quads along a fixed diagonal, and ear-clip other polygons.
        /// </summary>
        /// <returns>Each three consecutive indices form a triangle referencing Vertices.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no valid ear can be found, usually indicating a self-intersecting or degenerate contour.</exception>
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

    /// <summary>Wrapper for a solid extruded from a closed polyline contour.</summary>
    public sealed class Extrusion : GeometryBase
    {
        private Solid solid;

        /// <summary>Create a positive-Z extrusion from a polyline.</summary>
        /// <param name="c">Polyline defining the base contour.</param>
        /// <param name="h">Extrusion height along positive Z.</param>
        /// <param name="cap">Compatibility cap flag; the implementation always creates a closed solid using Solid.Prism.</param>
        /// <returns>Extrusion object containing the solid.</returns>
        public static Extrusion Create(PolylineCurve c, double h, bool cap) => new Extrusion
        {
            solid = Solid.Prism(c.Points, h)
        };

        /// <summary>Get the polyhedral solid represented by this extrusion.</summary>
        /// <returns>Internally generated solid.</returns>
        public Solid ToBrep() => solid;

        /// <summary>Transform all vertices of the internal polyhedron.</summary>
        /// <param name="t">Geometry transform to apply.</param>
        /// <returns>True when the internal solid is transformed successfully.</returns>
        public override bool Transform(Transform t) => solid.Transform(t);
    }

    /// <summary>Box defined by a local plane basis and three intervals.</summary>
    public struct Box
    {
        // Local box coordinate system and X/Y/Z extents.
        private Plane plane;
        private Interval x, y, z;

        /// <summary>Define a box from a plane basis and three axis intervals.</summary>
        /// <param name="p">Local plane and basis vectors of the box base.</param>
        /// <param name="xx">Local X range.</param>
        /// <param name="yy">Local Y range.</param>
        /// <param name="zz">Local Z range.</param>
        public Box(Plane p, Interval xx, Interval yy, Interval zz)
        {
            plane = p;
            x = xx;
            y = yy;
            z = zz;
        }

        /// <summary>
        /// Convert the box to a solid by forming a rectangular contour from the plane's X/Y ranges,
        /// then extruding along local Z by the Z interval length.
        /// </summary>
        /// <returns>Closed prism defined by the current plane and intervals.</returns>
        public Solid ToBrep()
        {
            var origin = new Point3d(plane.Origin.X, plane.Origin.Y, plane.Origin.Z + z.Min);
            return Solid.Prism(new[] { origin + plane.X * x.Min + plane.Y * y.Min, origin + plane.X * x.Max + plane.Y * y.Min, origin + plane.X * x.Max + plane.Y * y.Max, origin + plane.X * x.Min + plane.Y * y.Max }, z.Max - z.Min);
        }
    }
}
