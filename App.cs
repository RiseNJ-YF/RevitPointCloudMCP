using System;
using Autodesk.Revit.UI;
using RevitPointCloudMCP.Mcp;

namespace RevitPointCloudMCP
{
    /// <summary>
    /// Revit external application entry point. Starts a local MCP (Model Context
    /// Protocol) HTTP server when Revit launches, so Claude can query point clouds
    /// linked into the active model and create walls/floors/ceilings from them.
    ///
    /// The server only exposes what's defined in Tools/*.cs - it doesn't do anything
    /// on its own. Every write happens inside a normal Revit Transaction, so undo
    /// (Ctrl+Z) works exactly like it would for anything you modeled by hand.
    /// </summary>
    public class App : IExternalApplication
    {
        /// <summary>TCP port the local MCP server listens on (localhost only).</summary>
        public const int Port = 8765;

        private McpServer? _server;
        private RevitCommandDispatcher? _dispatcher;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                _dispatcher = new RevitCommandDispatcher();
                _dispatcher.Register();

                _server = new McpServer(_dispatcher, Port);
                _server.Start();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(
                    "Point Cloud MCP Bridge",
                    "Failed to start the MCP server on port " + Port + ":\n\n" + ex.Message +
                    "\n\nIf this says access denied, run (once, as admin):\n" +
                    "netsh http add urlacl url=http://localhost:" + Port + "/ user=Everyone");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _server?.Stop();
            _dispatcher?.Unregister();
            return Result.Succeeded;
        }
    }
}
