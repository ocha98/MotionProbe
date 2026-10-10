using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MotionProbeTool;
using UnityEditor;
using UnityEngine;

/// <summary>
/// MotionProbe: records an avatar while it moves (fixed game-time steps), then writes a report,
/// contact sheets, motion trails and charts that an LLM can read.
/// Call from MCP execute_code, e.g.  return MotionProbe.Run("walk_stop");  then  return MotionProbe.Status();
/// Outputs go to &lt;project&gt;/MotionProbeRuns/&lt;run&gt;/ (read report.md first). See MotionProbe.Help().
/// </summary>
public static class MotionProbe
{
    public const string Version = "1.0";

    /// <summary>Queue a capture run. Enters play mode if needed and returns immediately.</summary>
    public static string Run(string scenario = "standard", string optionsJson = null)
    {
        try
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "ERROR: Unity is compiling or importing. Retry in a few seconds.";
            if (EditorUtility.scriptCompilationFailed)
                return "ERROR: Script compilation errors exist, so play mode cannot start. Check the console.";
            if (ProbeRunner.Active != null)
                return "ERROR: A run is already in progress: " + ProbeRunner.Active.RunDir;

            var opt = MotionProbeOptions.Parse(optionsJson, out string optErr);
            if (opt == null) return "ERROR: options: " + optErr;
            var sc = ProbeScenario.Parse(scenario, out string scErr);
            if (sc == null) return "ERROR: scenario: " + scErr;

            ProbeIO.EnsureBaseDir();
            string label = ProbeIO.Sanitize(string.IsNullOrEmpty(opt.label) ? sc.Label : opt.label);
            string runDir = ProbeIO.RunsDir + "/" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + label;
            Directory.CreateDirectory(runDir);

            bool playing = EditorApplication.isPlaying;
            var req = new MotionProbeRequest
            {
                runDir = runDir,
                scenario = scenario ?? "",
                optionsJson = JsonUtility.ToJson(opt),
                exitPlayMode = opt.exitPlayMode && !playing,
                requestedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            File.WriteAllText(ProbeIO.PendingPath, JsonUtility.ToJson(req, true));
            File.WriteAllText(ProbeIO.LatestPath, runDir);
            ProbeIO.WriteStatus(runDir, new MotionProbeStatus
            {
                state = "queued",
                runDir = runDir,
                duration = sc.Duration,
                message = playing ? "starting in the current play session" : "entering play mode (domain reload can take 5-30 s)"
            });

            if (playing) ProbeBoot.TryStartPending();
            else EditorApplication.EnterPlaymode();

            var sb = new StringBuilder();
            sb.AppendLine("OK: run queued.");
            sb.AppendLine("runDir: " + runDir);
            sb.AppendLine("scenario (" + ProbeIO.F(sc.Duration) + " s): " + sc.Expanded);
            sb.AppendLine("Next: poll MotionProbe.Status() (or read runDir/status.json from disk) until state is 'done' or 'error'. Then read runDir/report.md first.");
            return sb.ToString();
        }
        catch (Exception e)
        {
            return "ERROR: " + e;
        }
    }

    /// <summary>Status of a run ("latest" by default).</summary>
    public static string Status(string run = "latest")
    {
        string dir = ProbeIO.ResolveRun(run);
        if (dir == null) return "No MotionProbe runs found in " + ProbeIO.RunsDir;
        var st = ProbeIO.ReadStatus(dir);
        if (st == null) return "No status.json in " + dir;
        var sb = new StringBuilder();
        sb.Append("state: ").Append(st.state);
        if (st.frames > 0)
            sb.Append("  frame ").Append(st.frame).Append('/').Append(st.frames)
              .Append("  t=").Append(ProbeIO.F(st.simTime)).Append("s of ").Append(ProbeIO.F(st.duration)).Append('s');
        sb.AppendLine();
        sb.AppendLine("runDir: " + dir);
        if (!string.IsNullOrEmpty(st.message)) sb.AppendLine("message: " + st.message);
        if (st.state == "queued" && !EditorApplication.isPlayingOrWillChangePlaymode)
            sb.AppendLine("note: play mode is not running. If this persists the run did not start (check the console), call MotionProbe.Run again.");
        if (st.outputs != null && st.outputs.Length > 0)
        {
            sb.AppendLine("outputs (in runDir):");
            foreach (var o in st.outputs) sb.AppendLine("  " + o);
        }
        return sb.ToString();
    }

    /// <summary>Contact sheet with evenly spaced times. group: body|head. views: e.g. "front,side" (default: all of the group).</summary>
    public static string Sheet(string run = "latest", string group = "body", float t0 = 0f, float t1 = -1f, int count = 6, string views = null)
    {
        try
        {
            var d = ProbeData.LoadRun(run, out string err);
            if (d == null) return "ERROR: " + err;
            ClampWindow(d, ref t0, ref t1);
            count = Mathf.Clamp(count, 1, 24);
            var times = new List<float>();
            for (int i = 0; i < count; i++) times.Add(count == 1 ? t0 : t0 + (t1 - t0) * i / (count - 1));
            string name = "sheet_" + ProbeIO.Sanitize(group) + "_" + ProbeIO.F(t0) + "-" + ProbeIO.F(t1) + ".png";
            return "OK: " + ProbeImaging.Sheet(d, group, times, null, views, Path.Combine(d.dir, name), null);
        }
        catch (Exception e) { return "ERROR: " + e; }
    }

    /// <summary>Contact sheet at explicit times, e.g. times = "1.80,1.85,1.90,2.00".</summary>
    public static string SheetAt(string run, string group, string times, string views = null)
    {
        try
        {
            var d = ProbeData.LoadRun(run, out string err);
            if (d == null) return "ERROR: " + err;
            var list = new List<float>();
            foreach (var tok in (times ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out float t))
                    list.Add(Mathf.Clamp(t, 0f, d.meta.duration));
            if (list.Count == 0) return "ERROR: no valid times";
            if (list.Count > 24) list.RemoveRange(24, list.Count - 24);
            string name = "sheet_" + ProbeIO.Sanitize(group) + "_at_" + ProbeIO.F(list[0]) + "-" + ProbeIO.F(list[list.Count - 1]) + ".png";
            return "OK: " + ProbeImaging.Sheet(d, group, list, null, views, Path.Combine(d.dir, name), null);
        }
        catch (Exception e) { return "ERROR: " + e; }
    }

    /// <summary>Motion trails. kind: physbone (tips relative to their parent bone) | body (hands/feet/head relative to root).</summary>
    public static string Trail(string run = "latest", float t0 = 0f, float t1 = -1f, string kind = "physbone", string filter = "")
    {
        try
        {
            var d = ProbeData.LoadRun(run, out string err);
            if (d == null) return "ERROR: " + err;
            ClampWindow(d, ref t0, ref t1);
            var a = ProbeAnalysis.Run(d);
            string name = "trail_" + ProbeIO.Sanitize(kind) + (string.IsNullOrEmpty(filter) ? "" : "_" + ProbeIO.Sanitize(filter)) + "_" + ProbeIO.F(t0) + "-" + ProbeIO.F(t1) + ".png";
            return "OK: " + ProbeImaging.Trail(d, a, t0, t1, kind, filter, Path.Combine(d.dir, name));
        }
        catch (Exception e) { return "ERROR: " + e; }
    }

    /// <summary>Displacement-over-time chart, optionally filtered by PhysBone/group name substring.</summary>
    public static string Chart(string run = "latest", float t0 = 0f, float t1 = -1f, string filter = "")
    {
        try
        {
            var d = ProbeData.LoadRun(run, out string err);
            if (d == null) return "ERROR: " + err;
            ClampWindow(d, ref t0, ref t1);
            var a = ProbeAnalysis.Run(d);
            string name = "chart" + (string.IsNullOrEmpty(filter) ? "" : "_" + ProbeIO.Sanitize(filter)) + "_" + ProbeIO.F(t0) + "-" + ProbeIO.F(t1) + ".png";
            return "OK: " + ProbeImaging.Chart(d, a, t0, t1, filter, Path.Combine(d.dir, name));
        }
        catch (Exception e) { return "ERROR: " + e; }
    }

    /// <summary>Re-runs analysis and regenerates report/images of a finished run from its saved data (no re-simulation).</summary>
    public static string Rebuild(string run = "latest")
    {
        try
        {
            var d = ProbeData.LoadRun(run, out string err);
            if (d == null) return "ERROR: " + err;
            var res = ProbeOutputs.GenerateAll(d);
            return "OK: " + d.dir + "\n" + res.headline + "\noutputs: " + string.Join(", ", res.files);
        }
        catch (Exception e) { return "ERROR: " + e; }
    }

    /// <summary>Detailed numbers for chains whose name/group contains filter.</summary>
    public static string Chain(string run = "latest", string filter = "")
    {
        try
        {
            var d = ProbeData.LoadRun(run, out string err);
            if (d == null) return "ERROR: " + err;
            return ProbeReport.ChainDetail(d, ProbeAnalysis.Run(d), filter ?? "");
        }
        catch (Exception e) { return "ERROR: " + e; }
    }

    public static string Help()
    {
        return HelpText;
    }

    static void ClampWindow(ProbeData d, ref float t0, ref float t1)
    {
        if (t1 < 0f || t1 > d.meta.duration) t1 = d.meta.duration;
        t0 = Mathf.Clamp(t0, 0f, t1);
        if (t1 - t0 < d.meta.dt) { t0 = 0f; t1 = d.meta.duration; }
    }

    const string HelpText =
@"MotionProbe " + Version + @" - make avatar motion legible to an LLM.
Docs: Packages/net.ochappa.motionprobe/Documentation~/scenarios.md (scenario language, recipes) and report-guide.md (every metric explained).

RUN (enters play mode, records, analyzes, exits play mode; returns immediately):
  MotionProbe.Run(""walk_stop"")
  MotionProbe.Run(""wait 0.5; param DoodleDance 1; wait 3; param DoodleDance 0; wait 2"", ""{\""label\"":\""doodle\""}"")
POLL:
  MotionProbe.Status()   -> state: queued|starting|preroll|running|analyzing|done|error|aborted
  (or read MotionProbeRuns/<run>/status.json from disk: the MCP connection drops during the play-mode domain reload)
READ:
  MotionProbeRuns/<run>/report.md first, then the PNG files it lists.
DRILL DOWN (edit mode is fine, no re-simulation; each call writes a PNG into the run folder and returns its path):
  MotionProbe.Sheet(""latest"", ""head"", 1.8f, 2.6f, 6, ""side,back"")       contact sheet, evenly spaced times
  MotionProbe.SheetAt(""latest"", ""body"", ""1.80,1.85,1.90,2.00"", ""front,side"")
  MotionProbe.Trail(""latest"", 1.8f, 4.3f, ""physbone"", ""Hair"")            motion trails, kind:
      physbone = zoomed panels per PhysBone group | limbs = zoomed panels per limb | cycle = one detected loop
      body = whole-body limb paths | overview = every PhysBone tip in one picture
  MotionProbe.Chart(""latest"", 0f, -1f, ""Hair"")                          displacement over time
  MotionProbe.Chain(""latest"", ""HairS2"")                                  per-chain numbers as text
  MotionProbe.Rebuild(""latest"")                                         regenerate report + images from saved data

SCENARIO DSL (';' or newline separated, game-time seconds; commands fire at the current time, 'wait' advances it):
  wait S | walk [V=1.6] | run [V=4] | move VX VZ [ACC=8] | stop [DEC=14]
  turn DEG [SEC=0.35] | jump [H=0.35] | shake DEG HZ SEC | sway CM HZ SEC | bob CM HZ SEC
  param NAME VALUE (avatar parameter via GestureManager; bool = 0/1) | gesture L R | mark LABEL
  presets: idle, walk_stop, run_stop, turn, jump, shake, sway, standard (combine with ';')
  Root motion really moves the avatar (PhysBones feel the inertia) and also sets VelocityX/Z/Y and Grounded.

OPTIONS JSON (all optional): avatar, label, fps(60), imageFps(30), preroll(1.5), exitPlayMode(true),
  bodyViews(""front,side,back""), headViews(""front,side,back"", """"=off), bodyWidth(300), bodyHeight(400), headSize(300),
  sheetColumns(6), csv(true)
VIEWS: front, back, side (=seen from the avatar's right, avatar faces right), left, top.
  Cameras are orthographic and locked to the avatar root's yaw and horizontal position (not its height):
  'front' always shows the face, travel is cancelled out, jumps stay visible.";
}

namespace MotionProbeTool
{
    [Serializable]
    public class MotionProbeOptions
    {
        public string avatar = "";
        public string label = "";
        public float fps = 60f;
        public float imageFps = 30f;
        public float preroll = 1.5f;
        public bool exitPlayMode = true;
        public string bodyViews = "front,side,back";
        public string headViews = "front,side,back";
        public int bodyWidth = 300;
        public int bodyHeight = 400;
        public int headSize = 300;
        public int sheetColumns = 6;
        public bool csv = true;

        public static MotionProbeOptions Parse(string json, out string error)
        {
            error = null;
            var o = new MotionProbeOptions();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, o); }
                catch (Exception e) { error = e.Message; return null; }
            }
            o.fps = Mathf.Clamp(o.fps, 20f, 240f);
            o.imageFps = Mathf.Clamp(o.imageFps, 1f, o.fps);
            o.preroll = Mathf.Clamp(o.preroll, 0.1f, 10f);
            o.bodyWidth = Mathf.Clamp(o.bodyWidth, 64, 1024);
            o.bodyHeight = Mathf.Clamp(o.bodyHeight, 64, 1024);
            o.headSize = Mathf.Clamp(o.headSize, 64, 1024);
            o.sheetColumns = Mathf.Clamp(o.sheetColumns, 2, 12);
            if (o.bodyViews == null) o.bodyViews = "";
            if (o.headViews == null) o.headViews = "";
            return o;
        }
    }

    [Serializable]
    public class MotionProbeRequest
    {
        public string runDir;
        public string scenario;
        public string optionsJson;
        public bool exitPlayMode;
        public string requestedAtUtc;
    }

    [Serializable]
    public class MotionProbeStatus
    {
        public string state;
        public string runDir;
        public int frame;
        public int frames;
        public float simTime;
        public float duration;
        public string message;
        public string updatedAt;
        public string[] outputs;
    }

    internal static class ProbeIO
    {
        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
        public static string BaseDir => ProjectRoot + "/MotionProbeRuns";
        public static string RunsDir => BaseDir;
        public static string PendingPath => BaseDir + "/pending.json";
        public static string LatestPath => BaseDir + "/latest.txt";

        public static void EnsureBaseDir()
        {
            Directory.CreateDirectory(BaseDir);
            string gi = BaseDir + "/.gitignore";
            if (!File.Exists(gi)) File.WriteAllText(gi, "*\n");
        }

        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "run";
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_' || c == '.' ? c : '_');
            string r = sb.ToString().Trim('_', '.');
            if (r.Length > 40) r = r.Substring(0, 40);
            return r.Length == 0 ? "run" : r;
        }

        public static void WriteStatus(string runDir, MotionProbeStatus st)
        {
            st.updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string tmp = runDir + "/status.json.tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(st, true));
            string dst = runDir + "/status.json";
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }

        public static MotionProbeStatus ReadStatus(string runDir)
        {
            string p = runDir + "/status.json";
            if (!File.Exists(p)) return null;
            try { return JsonUtility.FromJson<MotionProbeStatus>(File.ReadAllText(p)); }
            catch { return null; }
        }

        /// <summary>"latest" / "" / null -> newest run; otherwise a folder name inside MotionProbeRuns or an absolute path.</summary>
        public static string ResolveRun(string run)
        {
            if (string.IsNullOrEmpty(run) || run == "latest")
            {
                if (File.Exists(LatestPath))
                {
                    string p = File.ReadAllText(LatestPath).Trim();
                    if (Directory.Exists(p)) return p.Replace('\\', '/');
                }
                if (!Directory.Exists(RunsDir)) return null;
                string newest = null;
                foreach (var d in Directory.GetDirectories(RunsDir))
                    if (newest == null || string.CompareOrdinal(Path.GetFileName(d), Path.GetFileName(newest)) > 0) newest = d;
                return newest?.Replace('\\', '/');
            }
            if (Directory.Exists(run)) return run.Replace('\\', '/');
            string inRuns = RunsDir + "/" + run;
            return Directory.Exists(inRuns) ? inRuns : null;
        }

        public static string F(float v, int dec = 2)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "-";
            return v.ToString("F" + dec, CultureInfo.InvariantCulture);
        }

        public static string Cm(float meters, int dec = 1) => F(meters * 100f, dec);
    }

    /// <summary>Starts a queued run once play mode is entered (survives the play-mode domain reload via pending.json).</summary>
    [InitializeOnLoad]
    internal static class ProbeBoot
    {
        static ProbeBoot()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (EditorApplication.isPlaying) EditorApplication.delayCall += TryStartPending;
        }

        static void OnPlayModeChanged(PlayModeStateChange s)
        {
            switch (s)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    EditorApplication.delayCall += TryStartPending;
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    ProbeRunner.Active?.Abort("play mode was exited before the run finished");
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    ExpireStalePending();
                    break;
            }
        }

        public static void TryStartPending()
        {
            if (!EditorApplication.isPlaying || ProbeRunner.Active != null) return;
            if (!File.Exists(ProbeIO.PendingPath)) return;
            MotionProbeRequest req;
            try
            {
                req = JsonUtility.FromJson<MotionProbeRequest>(File.ReadAllText(ProbeIO.PendingPath));
                File.Delete(ProbeIO.PendingPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MotionProbe] could not read pending request: " + e.Message);
                return;
            }
            if (req == null || string.IsNullOrEmpty(req.runDir)) return;
            if (DateTime.TryParse(req.requestedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime at)
                && (DateTime.UtcNow - at).TotalMinutes > 5)
            {
                ProbeIO.WriteStatus(req.runDir, new MotionProbeStatus { state = "error", runDir = req.runDir, message = "stale request ignored (older than 5 minutes)" });
                return;
            }
            ProbeRunner.Start(req);
        }

        static void ExpireStalePending()
        {
            try
            {
                if (!File.Exists(ProbeIO.PendingPath)) return;
                var req = JsonUtility.FromJson<MotionProbeRequest>(File.ReadAllText(ProbeIO.PendingPath));
                if (req != null && DateTime.TryParse(req.requestedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime at)
                    && (DateTime.UtcNow - at).TotalMinutes > 2)
                {
                    File.Delete(ProbeIO.PendingPath);
                    if (Directory.Exists(req.runDir))
                        ProbeIO.WriteStatus(req.runDir, new MotionProbeStatus { state = "error", runDir = req.runDir, message = "play mode did not start for this run" });
                }
            }
            catch { }
        }
    }
}
