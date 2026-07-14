using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitPointCloudMCP.Tools
{
    /// <summary>Small helpers for reading tool arguments out of a JsonObject.</summary>
    internal static class Args
    {
        public static double Double(JsonObject args, string key, double? defaultValue = null)
        {
            if (args.TryGetPropertyValue(key, out var node) && node is not null)
                return node.GetValue<double>();
            if (defaultValue.HasValue) return defaultValue.Value;
            throw new ArgumentException($"Missing required argument '{key}'.");
        }

        public static int Int(JsonObject args, string key, int? defaultValue = null)
        {
            if (args.TryGetPropertyValue(key, out var node) && node is not null)
                return (int)node.GetValue<double>();
            if (defaultValue.HasValue) return defaultValue.Value;
            throw new ArgumentException($"Missing required argument '{key}'.");
        }

        public static string String(JsonObject args, string key, string? defaultValue = null)
        {
            if (args.TryGetPropertyValue(key, out var node) && node is not null)
                return node.GetValue<string>();
            if (defaultValue is not null) return defaultValue;
            throw new ArgumentException($"Missing required argument '{key}'.");
        }

        public static long Long(JsonObject args, string key, long? defaultValue = null)
        {
            if (args.TryGetPropertyValue(key, out var node) && node is not null)
                return (long)node.GetValue<double>();
            if (defaultValue.HasValue) return defaultValue.Value;
            throw new ArgumentException($"Missing required argument '{key}'.");
        }

        public static ElementId ElementId(JsonObject args, string key)
            => new(Long(args, key));

        /// <summary>Reads an {x,y} or {x,y,z} object. Missing z defaults to 0.</summary>
        public static XYZ Xyz(JsonObject args, string key)
        {
            if (!args.TryGetPropertyValue(key, out var node) || node is not JsonObject o)
                throw new ArgumentException($"Missing required point argument '{key}'.");

            var x = o["x"]?.GetValue<double>() ?? throw new ArgumentException($"'{key}.x' is required.");
            var y = o["y"]?.GetValue<double>() ?? throw new ArgumentException($"'{key}.y' is required.");
            var z = o["z"]?.GetValue<double>() ?? 0.0;
            return new XYZ(x, y, z);
        }

        public static List<XYZ> XyzList(JsonObject args, string key)
        {
            if (!args.TryGetPropertyValue(key, out var node) || node is not JsonArray arr)
                throw new ArgumentException($"Missing required array argument '{key}'.");

            var result = new List<XYZ>(arr.Count);
            foreach (var item in arr)
            {
                if (item is not JsonObject o)
                    throw new ArgumentException($"Each item in '{key}' must be an {{x,y}} or {{x,y,z}} object.");
                var x = o["x"]?.GetValue<double>() ?? throw new ArgumentException($"Point in '{key}' missing x.");
                var y = o["y"]?.GetValue<double>() ?? throw new ArgumentException($"Point in '{key}' missing y.");
                var z = o["z"]?.GetValue<double>() ?? 0.0;
                result.Add(new XYZ(x, y, z));
            }
            return result;
        }
    }
}
