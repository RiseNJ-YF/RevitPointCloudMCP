using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitPointCloudMCP;

namespace RevitPointCloudMCP.Mcp
{
    /// <summary>
    /// A minimal MCP server over the "Streamable HTTP" transport: one endpoint
    /// (POST /mcp) that speaks JSON-RPC 2.0, returning plain JSON responses
    /// (no SSE - every tool here answers synchronously, so we don't need it).
    ///
    /// Binds to localhost only. Point Claude Desktop's custom connector at
    /// http://localhost:{port}/mcp - see README.md for the one-time Windows
    /// URL-ACL command you may need to run first.
    /// </summary>
    public sealed class McpServer
    {
        private readonly HttpListener _listener = new();
        private readonly RevitCommandDispatcher _dispatcher;
        private readonly ToolRegistry _tools;
        private CancellationTokenSource? _cts;
        private string? _sessionId;

        public McpServer(RevitCommandDispatcher dispatcher, int port)
        {
            _dispatcher = dispatcher;
            _tools = new ToolRegistry(dispatcher);
            _listener.Prefixes.Add($"http://localhost:{port}/mcp/");
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener.Start();
            _ = Task.Run(() => ListenLoop(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener.Stop(); } catch { /* ignore */ }
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    if (token.IsCancellationRequested) break;
                    continue;
                }

                _ = Task.Run(() => HandleRequestSafe(ctx));
            }
        }

        private async Task HandleRequestSafe(HttpListenerContext ctx)
        {
            try
            {
                await HandleRequest(ctx);
            }
            catch
            {
                try { ctx.Response.Close(); } catch { /* client probably disconnected */ }
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx)
        {
            var request = ctx.Request;
            var response = ctx.Response;

            // Minimal DNS-rebinding guard: only allow local origins (or none, which
            // covers non-browser clients like Claude Desktop's main process).
            var origin = request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) &&
                !origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) &&
                !origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 403;
                response.Close();
                return;
            }

            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                response.Close();
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(body);
            }
            catch
            {
                await WriteJsonRpcError(response, null, -32700, "Parse error");
                return;
            }

            if (root is not JsonObject)
            {
                // Batched ([...]) requests aren't supported by this minimal server.
                await WriteJsonRpcError(response, null, -32600, "Invalid request (batching not supported)");
                return;
            }

            var idNode = CloneId(root["id"]);
            var method = root["method"]?.GetValue<string>();
            var isNotification = root["id"] is null;

            if (string.IsNullOrEmpty(method))
            {
                await WriteJsonRpcError(response, idNode, -32600, "Invalid request");
                return;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                    {
                        var clientProtocolVersion =
                            root["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
                        _sessionId = Guid.NewGuid().ToString("N");
                        response.Headers["Mcp-Session-Id"] = _sessionId;

                        var result = new JsonObject
                        {
                            ["protocolVersion"] = clientProtocolVersion,
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject
                            {
                                ["name"] = "revit-point-cloud-mcp",
                                ["version"] = "1.0.0"
                            }
                        };
                        await WriteJsonRpcResult(response, idNode, result);
                        return;
                    }

                    case "notifications/initialized":
                    case "notifications/cancelled":
                        response.StatusCode = 202;
                        response.Close();
                        return;

                    case "tools/list":
                        await WriteJsonRpcResult(response, idNode, _tools.ListToolsSchema());
                        return;

                    case "tools/call":
                    {
                        var toolName = root["params"]?["name"]?.GetValue<string>();
                        var args = root["params"]?["arguments"] as JsonObject;

                        if (string.IsNullOrEmpty(toolName))
                        {
                            await WriteJsonRpcError(response, idNode, -32602, "Missing tool name");
                            return;
                        }

                        JsonObject callResult;
                        try
                        {
                            var text = await _tools.Call(toolName, args);
                            callResult = new JsonObject
                            {
                                ["content"] = new JsonArray(new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = text
                                }),
                                ["isError"] = false
                            };
                        }
                        catch (Exception ex)
                        {
                            callResult = new JsonObject
                            {
                                ["content"] = new JsonArray(new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = "Tool error: " + ex.Message
                                }),
                                ["isError"] = true
                            };
                        }

                        await WriteJsonRpcResult(response, idNode, callResult);
                        return;
                    }

                    default:
                        if (isNotification)
                        {
                            response.StatusCode = 202;
                            response.Close();
                        }
                        else
                        {
                            await WriteJsonRpcError(response, idNode, -32601, "Method not found: " + method);
                        }
                        return;
                }
            }
            catch (Exception ex)
            {
                await WriteJsonRpcError(response, idNode, -32603, "Internal error: " + ex.Message);
            }
        }

        /// <summary>
        /// JsonNode instances can only belong to one parent, so the "id" pulled out
        /// of the request body can't be reused as-is in the response - it has to be
        /// rebuilt as a fresh, detached node.
        /// </summary>
        private static JsonNode? CloneId(JsonNode? id)
        {
            if (id is null) return null;
            if (id is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return JsonValue.Create(l);
                if (v.TryGetValue<double>(out var d)) return JsonValue.Create(d);
                if (v.TryGetValue<string>(out var s)) return JsonValue.Create(s);
            }
            return JsonValue.Create(id.ToString());
        }

        private static Task WriteJsonRpcResult(HttpListenerResponse response, JsonNode? id, JsonNode result)
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result
            };
            return WriteJson(response, payload);
        }

        private static Task WriteJsonRpcError(HttpListenerResponse response, JsonNode? id, int code, string message)
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
            };
            // Parse/invalid-request errors happen before we can trust the connection
            // enough to keep talking JSON-RPC "200 OK" semantics; everything else
            // (tool errors, unknown methods) is reported inside a normal 200 response.
            var status = code is -32700 or -32600 ? 400 : 200;
            return WriteJson(response, payload, status);
        }

        private static async Task WriteJson(HttpListenerResponse response, JsonNode payload, int statusCode = 200)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
    }
}
