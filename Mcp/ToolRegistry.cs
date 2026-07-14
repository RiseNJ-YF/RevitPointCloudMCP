using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitPointCloudMCP.Tools;
using RevitPointCloudMCP;

namespace RevitPointCloudMCP.Mcp
{
    /// <summary>
    /// Maps MCP tool names to their implementations and handles the plumbing of
    /// getting each call onto Revit's main thread with an active Document.
    /// Add a new tool by: writing the method in Tools/, adding one line to
    /// _tools below, and one schema entry in ToolSchemas.cs.
    /// </summary>
    public sealed class ToolRegistry
    {
        private readonly RevitCommandDispatcher _dispatcher;
        private readonly Dictionary<string, Func<Document, JsonObject, JsonNode>> _tools;

        public ToolRegistry(RevitCommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _tools = new Dictionary<string, Func<Document, JsonObject, JsonNode>>
            {
                ["list_point_clouds"] = PointCloudTools.ListPointClouds,
                ["get_point_cloud_points"] = PointCloudTools.GetPointCloudPoints,
                ["detect_planes"] = PlaneDetectionTools.DetectPlanes,

                ["get_levels"] = ModelQueryTools.GetLevels,
                ["get_wall_types"] = ModelQueryTools.GetWallTypes,
                ["get_floor_types"] = ModelQueryTools.GetFloorTypes,
                ["get_ceiling_types"] = ModelQueryTools.GetCeilingTypes,

                ["create_level"] = ModelCreationTools.CreateLevel,
                ["create_wall"] = ModelCreationTools.CreateWall,
                ["create_walls_batch"] = ModelCreationTools.CreateWallsBatch,
                ["create_floor_by_boundary"] = ModelCreationTools.CreateFloorByBoundary,
                ["create_ceiling_by_boundary"] = ModelCreationTools.CreateCeilingByBoundary,
            };
        }

        public async Task<string> Call(string toolName, JsonObject? args)
        {
            if (!_tools.TryGetValue(toolName, out var handler))
                throw new InvalidOperationException("Unknown tool: " + toolName);

            var argsObj = args ?? new JsonObject();

            var result = await _dispatcher.RunAsync(app =>
            {
                var doc = app.ActiveUIDocument?.Document
                    ?? throw new InvalidOperationException("No active Revit document - open a project first.");
                return (object)handler(doc, argsObj);
            }, TimeSpan.FromSeconds(90));

            return ((JsonNode)result!).ToJsonString();
        }

        public JsonNode ListToolsSchema()
        {
            var tools = new JsonArray();
            foreach (var def in ToolSchemas.All())
                tools.Add(def);
            return new JsonObject { ["tools"] = tools };
        }
    }
}
