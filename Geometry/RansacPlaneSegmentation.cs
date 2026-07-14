using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitPointCloudMCP.Geometry
{
    public enum PlaneClass { Floor, Ceiling, Wall, Sloped, Unclassified }

    /// <summary>
    /// One detected planar patch from a point cloud region, already shaped into
    /// something usable for modeling: a wall centerline + base/top heights for
    /// vertical planes, or a boundary loop + elevation for horizontal ones.
    /// </summary>
    public sealed class PlaneCandidate
    {
        public required XYZ Normal { get; init; }
        public required XYZ Centroid { get; init; }
        public required List<XYZ> Inliers { get; init; }
        public PlaneClass Classification { get; set; } = PlaneClass.Unclassified;

        // Populated when Classification == Wall
        public XYZ? WallStart { get; set; }
        public XYZ? WallEnd { get; set; }
        public double BaseZ { get; set; }
        public double TopZ { get; set; }

        // Populated when Classification is Floor or Ceiling
        public List<XYZ>? BoundaryLoop { get; set; }
        public double Elevation { get; set; }
    }

    /// <summary>
    /// Sequential RANSAC: repeatedly finds the plane with the most inliers among
    /// the remaining points, removes those inliers, and repeats. This is a coarse,
    /// fast first pass meant to hand Claude a short list of candidates to reason
    /// about and refine - not a substitute for careful manual review before you
    /// commit to building off of it.
    /// </summary>
    public static class RansacPlaneSegmentation
    {
        public static List<PlaneCandidate> DetectPlanes(
            IReadOnlyList<XYZ> points,
            double distanceThresholdFt,
            int minInliers,
            int maxPlanes,
            int maxIterationsPerPlane = 400,
            int? randomSeed = null)
        {
            var remaining = new List<XYZ>(points);
            var results = new List<PlaneCandidate>();
            var rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();

            while (results.Count < maxPlanes && remaining.Count >= Math.Max(minInliers, 30))
            {
                var best = FitBestPlane(remaining, distanceThresholdFt, maxIterationsPerPlane, rng);
                if (best is null || best.Inliers.Count < minInliers)
                    break;

                ClassifyAndShape(best);
                results.Add(best);

                // XYZ doesn't override Equals, so this HashSet uses reference equality.
                // That's fine (not a bug) because Inliers always holds the exact same
                // XYZ instances that came from `remaining`, never copies.
                var inlierSet = new HashSet<XYZ>(best.Inliers);
                remaining = remaining.Where(p => !inlierSet.Contains(p)).ToList();
            }

            // Among "horizontal" candidates, the lowest is almost always the floor
            // and the highest the ceiling - flip any that FitBestPlane's Z-only
            // heuristic got backwards (e.g. a mezzanine floor above a low ceiling).
            var horizontals = results.Where(r => r.Classification is PlaneClass.Floor or PlaneClass.Ceiling).ToList();
            if (horizontals.Count > 1)
            {
                var ordered = horizontals.OrderBy(h => h.Elevation).ToList();
                ordered[0].Classification = PlaneClass.Floor;
                ordered[^1].Classification = PlaneClass.Ceiling;
                for (int i = 1; i < ordered.Count - 1; i++)
                    ordered[i].Classification = PlaneClass.Floor; // ambiguous mid-height slab; caller should review
            }

            return results;
        }

        private static PlaneCandidate? FitBestPlane(
            List<XYZ> points, double distanceThresholdFt, int iterations, Random rng)
        {
            if (points.Count < 3) return null;

            List<XYZ> bestInliers = new();
            XYZ bestNormal = XYZ.BasisZ;

            for (int i = 0; i < iterations; i++)
            {
                var p0 = points[rng.Next(points.Count)];
                var p1 = points[rng.Next(points.Count)];
                var p2 = points[rng.Next(points.Count)];

                var v1 = p1.Subtract(p0);
                var v2 = p2.Subtract(p0);
                var normal = v1.CrossProduct(v2);
                var length = normal.GetLength();
                if (length < 1e-9) continue; // sampled 3 (near-)collinear points, skip

                normal = normal.Normalize();

                var inliers = new List<XYZ>(points.Count / 4 + 1);
                foreach (var p in points)
                {
                    var dist = Math.Abs(p.Subtract(p0).DotProduct(normal));
                    if (dist <= distanceThresholdFt)
                        inliers.Add(p);
                }

                if (inliers.Count > bestInliers.Count)
                {
                    bestInliers = inliers;
                    bestNormal = normal;
                }
            }

            if (bestInliers.Count == 0) return null;

            var centroid = Average(bestInliers);
            if (bestNormal.Z < 0) bestNormal = bestNormal.Negate(); // consistent "upward" convention

            return new PlaneCandidate
            {
                Normal = bestNormal,
                Centroid = centroid,
                Inliers = bestInliers
            };
        }

        private static XYZ Average(List<XYZ> pts)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
            return new XYZ(x / pts.Count, y / pts.Count, z / pts.Count);
        }

        private static void ClassifyAndShape(PlaneCandidate plane)
        {
            var verticality = Math.Abs(plane.Normal.Z); // ~1.0 = horizontal, ~0.0 = vertical

            if (verticality > 0.85)
            {
                plane.Classification = PlaneClass.Floor; // provisionally; DetectPlanes() may relabel to Ceiling
                plane.Elevation = plane.Centroid.Z;
                plane.BoundaryLoop = ConvexHull2D(plane.Inliers);
            }
            else if (verticality < 0.15)
            {
                plane.Classification = PlaneClass.Wall;
                ShapeWall(plane);
            }
            else
            {
                plane.Classification = PlaneClass.Sloped; // likely a roof pitch, soffit, or noise - caller decides
            }
        }

        private static void ShapeWall(PlaneCandidate plane)
        {
            var along = plane.Normal.CrossProduct(XYZ.BasisZ);
            if (along.GetLength() < 1e-9)
                along = XYZ.BasisX; // shouldn't happen given the verticality check above, but stay safe
            along = along.Normalize();

            double minT = double.MaxValue, maxT = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;

            foreach (var p in plane.Inliers)
            {
                var t = p.Subtract(plane.Centroid).DotProduct(along);
                if (t < minT) minT = t;
                if (t > maxT) maxT = t;
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            var midZ = (minZ + maxZ) / 2.0;
            var startXY = plane.Centroid.Add(along.Multiply(minT));
            var endXY = plane.Centroid.Add(along.Multiply(maxT));

            plane.WallStart = new XYZ(startXY.X, startXY.Y, midZ);
            plane.WallEnd = new XYZ(endXY.X, endXY.Y, midZ);
            plane.BaseZ = minZ;
            plane.TopZ = maxZ;
        }

        /// <summary>
        /// Andrew's monotone chain convex hull over the XY projection. This is a
        /// coarse first-pass boundary (it can't represent concave rooms) - good
        /// enough as a starting sketch, not a substitute for checking the result.
        /// </summary>
        private static List<XYZ> ConvexHull2D(List<XYZ> points)
        {
            var avgZ = points.Average(p => p.Z);
            var pts = points
                .Select(p => (p.X, p.Y))
                .Distinct()
                .OrderBy(p => p.X).ThenBy(p => p.Y)
                .ToList();

            if (pts.Count < 3)
                return points.Take(Math.Min(3, points.Count)).ToList();

            var lower = new List<(double X, double Y)>();
            foreach (var p in pts)
            {
                while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<(double X, double Y)>();
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                var p = pts[i];
                while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);

            return lower.Select(p => new XYZ(p.X, p.Y, avgZ)).ToList();
        }

        private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }
}
