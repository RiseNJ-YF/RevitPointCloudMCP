using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitPointCloudMCP.Tools
{
    public static class ModelQueryTools
    {
        public static JsonNode GetLevels(Document doc, JsonObject args)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var arr = new JsonArray();
            foreach (var l in levels)
                arr.Add(new JsonObject { ["id"] = l.Id.Value, ["name"] = l.Name, ["elevation_ft"] = l.Elevation });

            return new JsonObject { ["levels"] = arr };
        }

        public static JsonNode GetWallTypes(Document doc, JsonObject args) =>
            TypesOf<WallType>(doc);

        public static JsonNode GetFloorTypes(Document doc, JsonObject args) =>
            TypesOf<FloorType>(doc);

        public static JsonNode GetCeilingTypes(Document doc, JsonObject args) =>
            TypesOf<CeilingType>(doc);

        private static JsonNode TypesOf<T>(Document doc) where T : ElementType
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(T))
                .Cast<T>()
                .OrderBy(t => t.Name)
                .ToList();

            var arr = new JsonArray();
            foreach (var t in types)
                arr.Add(new JsonObject { ["id"] = t.Id.Value, ["name"] = t.Name });

            return new JsonObject { ["types"] = arr };
        }
    }
}
