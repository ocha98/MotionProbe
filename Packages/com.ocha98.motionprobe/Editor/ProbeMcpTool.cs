#if MOTIONPROBE_MCP
using System.Globalization;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace MotionProbeTool
{
    /// <summary>Exposes MotionProbe as the MCP tool "motion_probe" (same functions as the static MotionProbe API).</summary>
    [McpForUnityTool("motion_probe", Group = "core", Description =
        "Record and analyze avatar motion (PhysBone hair/cloth swing, body animation) so an LLM can judge it. " +
        "action=run starts a capture (enters play mode, fixed time step, exits when done) and returns at once; poll action=status " +
        "(or read MotionProbeRuns/<run>/status.json) until state=done, then read <run>/report.md and its PNGs. " +
        "Drill-down without re-simulating: sheet, sheet_at, trail, chart, chain. action=help shows the scenario language and options.")]
    public static class MotionProbeMcpTool
    {
        public class Parameters
        {
            [ToolParameter("run | status | sheet | sheet_at | trail | chart | chain | rebuild | help")]
            public string action { get; set; }

            [ToolParameter("run: scenario DSL or presets (idle, walk_stop, run_stop, turn, jump, shake, sway, standard), e.g. 'wait 0.5; param DoodleDance 1; wait 3'", Required = false)]
            public string scenario { get; set; }

            [ToolParameter("run: options JSON, e.g. {\"label\":\"x\",\"preroll\":1.5,\"imageFps\":30,\"headViews\":\"front,side,back\"}", Required = false)]
            public string options { get; set; }

            [ToolParameter("Run folder name or 'latest' (default)", Required = false)]
            public string run { get; set; }

            [ToolParameter("sheet/sheet_at: 'body' or 'head' (default body)", Required = false)]
            public string group { get; set; }

            [ToolParameter("Window start in seconds (default 0)", Required = false)]
            public float? t0 { get; set; }

            [ToolParameter("Window end in seconds (default end of run)", Required = false)]
            public float? t1 { get; set; }

            [ToolParameter("sheet: number of evenly spaced columns (default 6)", Required = false)]
            public int? count { get; set; }

            [ToolParameter("sheet/sheet_at: views, comma separated (front,side,back,left,top)", Required = false)]
            public string views { get; set; }

            [ToolParameter("sheet_at: comma separated times in seconds", Required = false)]
            public string times { get; set; }

            [ToolParameter("trail: physbone (default, zoomed per group) | limbs | cycle | body | overview", Required = false)]
            public string kind { get; set; }

            [ToolParameter("trail/chart/chain: substring filter on PhysBone or group names", Required = false)]
            public string filter { get; set; }
        }

        public static object HandleCommand(JObject p)
        {
            string action = Str(p, "action", "help").ToLowerInvariant();
            string run = Str(p, "run", "latest");
            float t0 = Num(p, "t0", 0f), t1 = Num(p, "t1", -1f);
            string result;
            switch (action)
            {
                case "run": result = MotionProbe.Run(Str(p, "scenario", "standard"), Str(p, "options", null)); break;
                case "status": result = MotionProbe.Status(run); break;
                case "sheet": result = MotionProbe.Sheet(run, Str(p, "group", "body"), t0, t1, (int)Num(p, "count", 6f), Str(p, "views", null)); break;
                case "sheet_at": result = MotionProbe.SheetAt(run, Str(p, "group", "body"), Str(p, "times", ""), Str(p, "views", null)); break;
                case "trail": result = MotionProbe.Trail(run, t0, t1, Str(p, "kind", "physbone"), Str(p, "filter", "")); break;
                case "chart": result = MotionProbe.Chart(run, t0, t1, Str(p, "filter", "")); break;
                case "chain": result = MotionProbe.Chain(run, Str(p, "filter", "")); break;
                case "rebuild": result = MotionProbe.Rebuild(run); break;
                case "help": result = MotionProbe.Help(); break;
                default: return new ErrorResponse("unknown action '" + action + "'. " + MotionProbe.Help());
            }
            if (result.StartsWith("ERROR")) return new ErrorResponse(result);
            return new SuccessResponse(result);
        }

        static string Str(JObject p, string key, string def)
        {
            var t = p?[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            string s = t.ToString();
            return string.IsNullOrEmpty(s) ? def : s;
        }

        static float Num(JObject p, string key, float def)
        {
            var t = p?[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            return float.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;
        }
    }
}
#endif
