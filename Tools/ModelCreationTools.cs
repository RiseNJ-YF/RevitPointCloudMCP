using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitPointCloudMCP.Tools
{
    /// <summary>
    /// Everything here modifies the document, so every method opens and commits
    /// its own Transaction. Nothing is silently batched across tool calls - each
    /// call is one undo step (except create_walls_batch, which is deliberately
    /// one Transaction for many walls).
    /// </summary>
    public static class ModelCreationTools
    {
        public static JsonNode CreateLevel(Document doc, JsonObject args)
        {
            var name = Args.String(args, "name");
            var elevation = Args.Double(args, "elevation_ft");

            using var tx = new Transaction(doc, "Create Level (MCP)");
            tx.Start();
            var level = Level.Create(doc, elevation);
            level.Name = name;
            tx.Commit();

            return new JsonObject { ["id"] = level.Id.Value, ["name"] = level.Name, ["elevation_ft"] = level.Elevation };
        }

        public static JsonNode CreateWall(Document doc, JsonObject args)
        {
            using var tx = new Transaction(doc, "Create Wall (MCP)");
            tx.Start();
            var id = CreateWallInternal(doc, args);
            tx.Commit();

            return new JsonObject { ["id"] = id.Value };
        }

        public static JsonNode CreateWallsBatch(Document doc, JsonObject args)
        {
            if (!args.TryGetPropertyValue("walls", out var node) || node is not JsonArray arr)
                throw new ArgumentException("Missing required array argument 'walls'.");

            var ids = new JsonArray();

            using var tx = new Transaction(doc, "Create Walls (MCP batch)");
            tx.Start();
            foreach (var item in arr)
            {
                if (item is not JsonObject wallArgs)
                    throw new ArgumentException("Each item in 'walls' must be an object.");
                ids.Add(CreateWallInternal(doc, wallArgs).Value);
            }
            tx.Commit();

            return new JsonObject { ["created_ids"] = ids, ["count"] = ids.Count };
        }

        public static JsonNode CreateFloorByBoundary(Document doc, JsonObject args)
        {
            var boundary = Args.XyzList(args, "boundary");
            var levelId = Args.ElementId(args, "level_id");
            var floorTypeId = Args.ElementId(args, "floor_type_id");

            if (boundary.Count < 3)
                throw new ArgumentException("'boundary' needs at least 3 points.");

            var loop = BuildLoop(boundary);

            using var tx = new Transaction(doc, "Create Floor (MCP)");
            tx.Start();
            var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorTypeId, levelId);
            tx.Commit();

            return new JsonObject { ["id"] = floor.Id.Value };
        }

        public static JsonNode CreateCeilingByBoundary(Document doc, JsonObject args)
        {
            var boundary = Args.XyzList(args, "boundary");
            var levelId = Args.ElementId(args, "level_id");
            var ceilingTypeId = Args.ElementId(args, "ceiling_type_id");

            if (boundary.Count < 3)
                throw new ArgumentException("'boundary' needs at least 3 points.");

            var loop = BuildLoop(boundary);

            using var tx = new Transaction(doc, "Create Ceiling (MCP)");
            tx.Start();
            var ceiling = Ceiling.Create(doc, new List<CurveLoop> { loop }, ceilingTypeId, levelId);
            tx.Commit();

            return new JsonObject { ["id"] = ceiling.Id.Value };
        }

        // --- shared internals ---

        /// <summary>Assumes a Transaction is already open. Does not commit.</summary>
        private static ElementId CreateWallInternal(Document doc, JsonObject w)
        {
            var start = Args.Xyz(w, "start");
            var end = Args.Xyz(w, "end");
            var levelId = Args.ElementId(w, "base_level_id");
            var height = Args.Double(w, "height_ft");
            var wallTypeId = Args.ElementId(w, "wall_type_id");
            var offset = Args.Double(w, "base_offset_ft", 0.0);

            if (start.IsAlmostEqualTo(end))
                throw new ArgumentException("Wall start and end points are the same point.");

            var line = Line.CreateBound(start, end);
            var wall = Wall.Create(doc, line, wallTypeId, levelId, height, offset, false, false);
            return wall.Id;
        }

        private static CurveLoop BuildLoop(List<XYZ> points)
        {
            var loop = new CurveLoop();
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if (a.IsAlmostEqualTo(b)) continue; // guard against an accidental repeated closing point
                loop.Append(Line.CreateBound(a, b));
            }
            return loop;
        }
    }
}
