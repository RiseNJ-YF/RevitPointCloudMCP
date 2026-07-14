using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitPointCloudMCP.Geometry;

namespace RevitPointCloudMCP.Tools
{
    public static class PlaneDetectionTools
    {
        public static JsonNode DetectPlanes(Document doc, JsonObject args)
        {
            var pc = PointCloudTools.ResolvePointCloud(doc, args);
            var min = Args.Xyz(args, "min");
            var max = Args.Xyz(args, "max");
            var maxPointsToSample = Args.Int(args, "max_points_to_sample", 40000);
            var distanceThreshold = Args.Double(args, "distance_threshold_ft", 0.05);
            var minInliers = Args.Int(args, "min_inliers", 300);
            var maxPlanes = Args.Int(args, "max_planes", 12);

            var points = PointCloudTools.ReadRegion(pc, min, max, maxPointsToSample, averageDistanceFt: 0.02);

            var planes = RansacPlaneSegmentation.DetectPlanes(
                points, distanceThreshold, minInliers, maxPlanes);

            var arr = new JsonArray();
            foreach (var p in planes)
            {
                var entry = new JsonObject
                {
                    ["classification"] = p.Classification.ToString().ToLowerInvariant(),
                    ["inlier_count"] = p.Inliers.Count,
                    ["normal"] = PointCloudTools.PointJson(p.Normal),
                };

                if (p.Classification == PlaneClass.Wall)
                {
                    entry["wall_start"] = PointCloudTools.PointJson(p.WallStart!);
                    entry["wall_end"] = PointCloudTools.PointJson(p.WallEnd!);
                    entry["base_z"] = p.BaseZ;
                    entry["top_z"] = p.TopZ;
                    entry["length_ft"] = p.WallStart!.DistanceTo(p.WallEnd!);
                }
                else if (p.Classification is PlaneClass.Floor or PlaneClass.Ceiling)
                {
                    entry["elevation_ft"] = p.Elevation;
                    var loop = new JsonArray();
                    foreach (var v in p.BoundaryLoop!)
                        loop.Add(PointCloudTools.PointJson(v));
                    entry["boundary_loop"] = loop;
                }

                arr.Add(entry);
            }

            return new JsonObject
            {
                ["sampled_point_count"] = points.Count,
                ["planes_found"] = planes.Count,
                ["planes"] = arr,
                ["note"] = "Coarse candidates - review before creating geometry. Convex-hull boundaries can't represent concave rooms; treat them as a starting sketch."
            };
        }
    }
}
