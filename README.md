# Point Cloud MCP Bridge

A Revit add-in that runs a local MCP server inside Revit, so Claude can read
linked point clouds and create walls/floors/ceilings from them directly in
your model. Full read + write, standalone (doesn't touch or depend on any
other Revit MCP connector you have running).

**Heads up - this has not been compiled or run against a live Revit install.**
I don't have a Windows/Revit environment to test in. The Revit API calls
(`Wall.Create`, `Floor.Create`, `Ceiling.Create`, `PointCloudInstance.GetPoints`,
the MCP HTTP transport) are all written against APIs I'm confident about, but
expect to fix a handful of small mismatches on your first build - wrong
namespace, a renamed parameter, that kind of thing. The architecture and the
RANSAC/geometry logic are the parts most worth reviewing carefully; the Revit
API glue is the part most likely to need a small tweak.

## How it fits together

```
Claude  <--HTTP (localhost:8765)-->  McpServer.cs  <--ExternalEvent-->  Revit API thread
                                           |
                                    Tools/*.cs (point cloud read,
                                    RANSAC plane detection, wall/
                                    floor/ceiling creation)
```

- The HTTP server runs on a background thread inside the Revit process.
- Revit API calls only work on Revit's main thread, so every tool call gets
  queued and executed via `ExternalEvent` (`RevitCommandDispatcher.cs`) -
  the same pattern any modeless Revit UI uses.
- Every write (`create_wall`, `create_floor_by_boundary`, etc.) opens and
  commits its own `Transaction`, so **undo (Ctrl+Z) works normally** for
  anything Claude creates.

## What it can do

**Read:**
- `list_point_clouds` - every linked point cloud, with id/name/bbox
- `get_point_cloud_points` - raw points in a bounding box region
- `detect_planes` - RANSAC plane segmentation on a region, returning wall
  candidates (centerline + base/top height) and floor/ceiling candidates
  (boundary loop + elevation)
- `get_levels`, `get_wall_types`, `get_floor_types`, `get_ceiling_types`

**Write:**
- `create_level`
- `create_wall`, `create_walls_batch`
- `create_floor_by_boundary`, `create_ceiling_by_boundary`

Roofs, doors/windows, and MEP aren't in this first pass - see "Extending it"
below for where to add them; the pattern is the same for all of them.

## Prerequisites

- Revit 2026 or 2027 (see below for older versions)
- .NET 8 SDK and/or .NET 10 SDK, matching whichever Revit version(s) you're
  building for (only needed if building locally; GitHub Actions handles both
  for you if you'd rather not install anything on a locked-down machine)
- Your point cloud already linked into the model the normal way
  (Insert tab -> Point Cloud, pointing at the ReCap-indexed `.rcp`/`.rcs`
  file from your SiteWalk 360 / Insta360 SLAM export)

Revit 2027 (released ~April 2026) moved from .NET 8 to .NET 10, so this
project multi-targets both: `net8.0-windows` for Revit 2026, `net10.0-windows`
for Revit 2027. One `dotnet build` produces both outputs.

Worth knowing: Revit 2027 ships its own official Autodesk Assistant and a
public MCP server built into the product. From what's public about it so far,
its tool groups cover model queries, sheets, rooms, schedules, exports, and
element operations - nothing for point clouds or wall/floor/ceiling creation
from scan data, so this add-in still covers ground the built-in one doesn't.
Worth keeping an eye on as it matures, though.

## Build

**Locally (Visual Studio 2022+):** open `RevitPointCloudMCP.csproj`, build in
`Debug` config. Because the project multi-targets both Revit years, this
builds two DLLs (`bin/Debug/net8.0-windows/` and `bin/Debug/net10.0-windows/`)
in one go. The `.csproj` has a post-build step that copies each one, with its
`.addin` manifest, straight into the matching
`%AppData%\Autodesk\Revit\Addins\2026\` / `...\2027\` folder for you.

If you only have one of the two SDKs installed, `dotnet build` will fail on
the target framework you're missing - install the matching .NET SDK, or
temporarily remove the other entry from `<TargetFrameworks>` in the `.csproj`.

**GitHub Actions (locked-down machine):** push this repo, the workflow in
`.github/workflows/build.yml` builds on `windows-latest` with both .NET 8 and
.NET 10 installed, and uploads a `RevitPointCloudMCP` artifact zip containing
two folders, `Revit2026/` and `Revit2027/` - each already laid out to match
Revit's own Addins folder, so you can extract the one you need straight into
`%AppData%\Autodesk\Revit\Addins\2026\` or `...\2027\`.

### Targeting an older Revit version

If you're on Revit 2022-2024 (.NET Framework 4.8, not .NET 8/10):
1. In `RevitPointCloudMCP.csproj`, add `net48` to `<TargetFrameworks>` (or
   replace the list entirely if you don't need 2026/2027 too), and add a
   matching conditional `<PropertyGroup Condition="'$(TargetFramework)'=='net48'">`
   with `<RevitVersion>2024</RevitVersion>` (or your version) inside it,
   following the same pattern as the net8.0-windows/net10.0-windows blocks.
2. `Element.Id.Value` (a `long`) was introduced in the 2024 API; on versions
   older than that, use `.IntegerValue` (an `int`) instead, in `Args.cs` and
   every `Tools/*.cs` file that does `xyz.Id.Value`.
3. `required` properties (`Geometry/RansacPlaneSegmentation.cs`) need C# 11+;
   on `net48` add `<LangVersion>11</LangVersion>` or rewrite those as normal
   settable properties with a constructor.

## One-time setup: let Revit open a local port

Revit doesn't run as admin, and `HttpListener` needs permission to bind a
port without it. Run this once, as Administrator (adjust the port if you
changed `App.Port`):

```
netsh http add urlacl url=http://localhost:8765/mcp/ user=Everyone
```

If you skip this, Revit will show a TaskDialog on startup telling you the
same thing.

## Connect Claude to it

In Claude Desktop, add a custom connector pointing at:

```
http://localhost:8765/mcp
```

Revit needs to already be running (with a project open) for the server to
respond - `tools/call` will fail with "No active Revit document" otherwise.

## Suggested workflow

1. Link your point cloud into Revit as usual (Insert -> Point Cloud).
2. Ask Claude to `list_point_clouds` to get its element id and overall bbox.
3. Work **room by room**: give Claude a tight `min`/`max` region (a single
   room, not the whole building) and ask it to `detect_planes` there.
   RANSAC on a full-building point cloud in one call will be slow and the
   results harder to trust.
4. Review what comes back before building anything - `detect_planes` is a
   coarse first pass (see the caveats in each tool's description). A good
   habit: ask Claude to summarize the candidate walls/floor/ceiling it
   found and flag anything that looks off (a wall that's too short, a
   floor boundary that looks concave when your room isn't) before creating
   geometry from it.
5. Have Claude pull `get_levels` / `get_wall_types` / `get_floor_types` to
   pick real types instead of guessing ids.
6. Build with `create_walls_batch` (one undo step for the whole room) and
   `create_floor_by_boundary` / `create_ceiling_by_boundary`.
7. **Save a backup before a big batch.** Everything here is a normal
   Transaction and undoes fine, but for anything building out a whole floor
   at once, a `.rvt` backup first is cheap insurance.

## Extending it

Adding a new tool is three small edits:
1. Write the method in `Tools/` (any `Func<Document, JsonObject, JsonNode>`).
2. Register it in `Mcp/ToolRegistry.cs`.
3. Add its schema entry in `Mcp/ToolSchemas.cs`.

Roofs are the natural next addition (`RoofBase.Create` / footprint roofs work
similarly to `Floor.Create`, but need a `ModelCurveArray` and slope-defining
lines rather than a plain `CurveLoop`). Doors/windows would use
`Document.Create.NewFamilyInstance` hosted on the walls you've already created.

## A note on `detect_planes`

The RANSAC segmentation (`Geometry/RansacPlaneSegmentation.cs`) is a
from-scratch implementation - sequential RANSAC plane fitting, classification
by normal verticality (>0.85 horizontal, <0.15 vertical, anything between is
left as "sloped" and not auto-classified), and a convex-hull boundary for
horizontal planes. It's deliberately simple:

- Convex hull can't represent concave rooms (L-shaped rooms, alcoves) - it'll
  give you the hull of the room, not the room itself. Fine as a starting
  sketch; not something to build directly from without checking.
- It doesn't merge coplanar patches split across scan gaps, and it doesn't
  handle furniture/clutter occluding a wall - a heavily furnished room will
  need a tighter region and a human eye more than a bare one will.
- `distance_threshold_ft` and `min_inliers` are the two knobs worth tuning
  per-scan if results look noisy - loosen the threshold for sparser scans,
  raise `min_inliers` to suppress small furniture-sized false planes.

If you outgrow this, the natural upgrade path is a real point-cloud library
(Open3D) as an external service doing the segmentation, with this add-in
staying purely as the Revit read/write bridge - the same split you flagged
as your other option. The tool boundary (`detect_planes` in, plane
candidates out) is designed to make that swap easy without touching anything
else.
