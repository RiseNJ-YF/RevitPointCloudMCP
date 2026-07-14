using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;

namespace RevitPointCloudMCP.Tools
{
    public static class PointCloudTools
    {
        public static JsonNode ListPointClouds(Document doc, JsonObject args)
        {
            var clouds = new FilteredElementCollector(doc)
                .OfClass(typeof(PointCloudInstance))
                .Cast<PointCloudInstance>()
                .ToList();

            var arr = new JsonArray();
            foreach (var pc in clouds)
            {
                var bbox = pc.get_BoundingBox(null);
                arr.Add(new JsonObject
                {
                    ["id"] = pc.Id.Value,
                    ["name"] = pc.Name,
                    ["min"] = bbox is null ? null : PointJson(bbox.Min),
                    ["max"] = bbox is null ? null : PointJson(bbox.Max),
                });
            }

            return new JsonObject { ["point_clouds"] = arr };
        }

        public static JsonNode GetPointCloudPoints(Document doc, JsonObject args)
        {
            var pc = ResolvePointCloud(doc, args);
            var min = Args.Xyz(args, "min");
            var max = Args.Xyz(args, "max");
            var maxPoints = System.Math.Clamp(Args.Int(args, "max_points", 5000), 1, 50000);

            var points = ReadRegion(pc, min, max, maxPoints, averageDistanceFt: 0.02);

            var arr = new JsonArray();
            foreach (var p in points)
                arr.Add(PointJson(p));

            return new JsonObject
            {
                ["point_count"] = points.Count,
                ["points"] = arr
            };
        }

        // --- shared helpers, also used by PlaneDetectionTools ---

        internal static PointCloudInstance ResolvePointCloud(Document doc, JsonObject args)
        {
            var id = Args.ElementId(args, "point_cloud_id");
            return doc.GetElement(id) as PointCloudInstance
                ?? throw new System.ArgumentException($"Element {id.Value} is not a linked point cloud (see list_point_clouds).");
        }

        /// <summary>Pulls up to <paramref name="maxPoints"/> points from an axis-aligned
        /// box region of a point cloud, as a flat list of Revit XYZ (feet).</summary>
        internal static List<XYZ> ReadRegion(
            PointCloudInstance pc, XYZ min, XYZ max, int maxPoints, double averageDistanceFt)
        {
            // Six axis-aligned half-space planes intersect to form the min/max box:
            // the "min" planes keep points >= min on each axis, the "max" planes
            // (normal flipped) keep points <= max.
            var planes = new List<Plane>
            {
                Plane.CreateByNormalAndOrigin(XYZ.BasisX, min),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisX, max),
                Plane.CreateByNormalAndOrigin(XYZ.BasisY, min),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisY, max),
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, min),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisZ, max),
            };

            var filter = PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
            var collection = pc.GetPoints(filter, averageDistanceFt, maxPoints);

            var result = new List<XYZ>(collection.Count);
            foreach (CloudPoint cp in collection)
                result.Add(new XYZ(cp.X, cp.Y, cp.Z));
            return result;
        }

        internal static JsonObject PointJson(XYZ p) => new()
        {
            ["x"] = p.X,
            ["y"] = p.Y,
            ["z"] = p.Z
        };
    }
}
