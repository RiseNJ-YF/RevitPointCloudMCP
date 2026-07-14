using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace RevitPointCloudMCP.Mcp
{
    /// <summary>
    /// Static tool descriptors returned from tools/list. All lengths are in feet
    /// and all coordinates are in Revit's internal (model) coordinate system,
    /// matching every other Revit MCP tool you're likely to have connected.
    /// </summary>
    public static class ToolSchemas
    {
        public static IEnumerable<JsonObject> All()
        {
            yield return Tool(
                "list_point_clouds",
                "List every linked point cloud in the active Revit document, with its element id, name, and bounding box in feet.",
                Props());

            yield return Tool(
                "get_point_cloud_points",
                "Get raw XYZ points from a linked point cloud within a bounding box region, in feet. Use this to eyeball a region before running detect_planes on it, or when you need points detect_planes doesn't expose directly.",
                Props(
                    ("point_cloud_id", Int("Element id from list_point_clouds.")),
                    ("min", XyzSchema("Region min corner (feet).")),
                    ("max", XyzSchema("Region max corner (feet).")),
                    ("max_points", Int("Cap on points returned. Default 5000, max 50000."))
                ),
                required: new[] { "point_cloud_id", "min", "max" });

            yield return Tool(
                "detect_planes",
                "Run RANSAC plane segmentation on a bounded region of a linked point cloud and return candidate walls (as centerline + base/top height), floors, and ceilings (as boundary loop + elevation). This is a coarse first pass meant to be reviewed, not modeled blindly - always sanity-check candidates (e.g. against get_boundary_lines of nearby existing elements, or plain visual inspection) before creating geometry from them. Keep regions to roughly one room at a time for both performance and classification accuracy.",
                Props(
                    ("point_cloud_id", Int("Element id from list_point_clouds.")),
                    ("min", XyzSchema("Region min corner (feet).")),
                    ("max", XyzSchema("Region max corner (feet).")),
                    ("max_points_to_sample", Int("Points pulled from the cloud before segmentation. Default 40000.")),
                    ("distance_threshold_ft", Num("Max distance from a plane for a point to count as an inlier. Default 0.05 ft (~15mm). Loosen for noisy/sparse scans.")),
                    ("min_inliers", Int("Minimum points for a plane to be reported. Default 300.")),
                    ("max_planes", Int("Stop after finding this many planes. Default 12."))
                ),
                required: new[] { "point_cloud_id", "min", "max" });

            yield return Tool(
                "get_levels",
                "List all Levels in the model with their element id, name, and elevation in feet.",
                Props());

            yield return Tool(
                "get_wall_types",
                "List all Wall Types in the model with their element id and name, for use with create_wall.",
                Props());

            yield return Tool(
                "get_floor_types",
                "List all Floor Types in the model with their element id and name, for use with create_floor_by_boundary.",
                Props());

            yield return Tool(
                "get_ceiling_types",
                "List all Ceiling Types in the model with their element id and name, for use with create_ceiling_by_boundary.",
                Props());

            yield return Tool(
                "create_level",
                "Create a new Level at the given elevation (feet). Returns the new level's element id. Check get_levels first to avoid creating a duplicate.",
                Props(
                    ("name", Str("Level name, e.g. 'Level 1' or 'T.O. Slab'.")),
                    ("elevation_ft", Num("Elevation in feet from Revit's internal origin."))
                ),
                required: new[] { "name", "elevation_ft" });

            yield return Tool(
                "create_wall",
                "Create a single wall from a horizontal start/end point. Wraps Wall.Create in its own Transaction.",
                Props(
                    ("start", XySchema("Wall centerline start point (feet).")),
                    ("end", XySchema("Wall centerline end point (feet).")),
                    ("base_level_id", Int("Level element id the wall is based on.")),
                    ("height_ft", Num("Unconnected wall height in feet.")),
                    ("wall_type_id", Int("Wall type element id from get_wall_types.")),
                    ("base_offset_ft", Num("Offset from the base level, feet. Default 0."))
                ),
                required: new[] { "start", "end", "base_level_id", "height_ft", "wall_type_id" });

            yield return Tool(
                "create_walls_batch",
                "Create many walls in a single Transaction (faster and more atomic than repeated create_wall calls - use this when building out a whole floor from detect_planes results).",
                Props(
                    ("walls", new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Array of wall definitions, same fields as create_wall.",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = Props(
                                ("start", XySchema("")),
                                ("end", XySchema("")),
                                ("base_level_id", Int("")),
                                ("height_ft", Num("")),
                                ("wall_type_id", Int("")),
                                ("base_offset_ft", Num(""))
                            ),
                            ["required"] = new JsonArray("start", "end", "base_level_id", "height_ft", "wall_type_id")
                        }
                    })
                ),
                required: new[] { "walls" });

            yield return Tool(
                "create_floor_by_boundary",
                "Create a floor from a closed boundary loop (list of XY points, feet, in order - do not repeat the first point at the end).",
                Props(
                    ("boundary", new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Ordered list of {x,y} points (feet) forming a closed loop.",
                        ["items"] = XySchema("")
                    }),
                    ("level_id", Int("Level element id the floor is hosted on.")),
                    ("floor_type_id", Int("Floor type element id from get_floor_types."))
                ),
                required: new[] { "boundary", "level_id", "floor_type_id" });

            yield return Tool(
                "create_ceiling_by_boundary",
                "Create a ceiling from a closed boundary loop (list of XY points, feet, in order - do not repeat the first point at the end).",
                Props(
                    ("boundary", new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Ordered list of {x,y} points (feet) forming a closed loop.",
                        ["items"] = XySchema("")
                    }),
                    ("level_id", Int("Level element id the ceiling is hosted on.")),
                    ("ceiling_type_id", Int("Ceiling type element id from get_ceiling_types."))
                ),
                required: new[] { "boundary", "level_id", "ceiling_type_id" });
        }

        // ---- schema-building helpers -------------------------------------------------

        private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required is { Length: > 0 })
            {
                var req = new JsonArray();
                foreach (var r in required) req.Add(r);
                schema["required"] = req;
            }

            return new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = schema
            };
        }

        private static JsonObject Props(params (string Name, JsonNode Schema)[] props)
        {
            var obj = new JsonObject();
            foreach (var (name, schema) in props)
                obj[name] = schema;
            return obj;
        }

        private static JsonObject Str(string description) =>
            new() { ["type"] = "string", ["description"] = description };

        private static JsonObject Num(string description) =>
            new() { ["type"] = "number", ["description"] = description };

        private static JsonObject Int(string description) =>
            new() { ["type"] = "integer", ["description"] = description };

        private static JsonObject XySchema(string description) => new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["properties"] = new JsonObject { ["x"] = Num(""), ["y"] = Num("") },
            ["required"] = new JsonArray("x", "y")
        };

        private static JsonObject XyzSchema(string description) => new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["properties"] = new JsonObject { ["x"] = Num(""), ["y"] = Num(""), ["z"] = Num("") },
            ["required"] = new JsonArray("x", "y", "z")
        };
    }
}
