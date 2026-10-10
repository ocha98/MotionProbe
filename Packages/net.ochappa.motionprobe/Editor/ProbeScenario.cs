using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MotionProbeTool
{
    internal class ProbeCommand
    {
        public float Time;
        public string Op;
        public float[] Num;
        public string Name;
        public string Text;
        public string Label;
    }

    /// <summary>Tiny scenario language: commands fire at the current time, "wait S" advances it.</summary>
    internal class ProbeScenario
    {
        public readonly List<ProbeCommand> Commands = new List<ProbeCommand>();
        public float Duration;
        public string Label = "custom";
        public string Expanded = "";
        public float MaxJump;

        public static readonly Dictionary<string, string> Presets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "idle", "wait 3" },
            { "walk_stop", "wait 0.3; walk 1.6; wait 1.5; stop; wait 2.5" },
            { "run_stop", "wait 0.3; run 4; wait 1.2; stop; wait 2.5" },
            { "turn", "wait 0.3; turn 180 0.35; wait 2.5" },
            { "jump", "wait 0.3; jump 0.35; wait 2.5" },
            { "shake", "wait 0.3; shake 30 2 1.5; wait 3" },
            { "sway", "wait 0.3; sway 6 1.5 2; wait 3" },
            { "standard", "wait 0.3; walk 1.6; wait 1.2; stop; wait 2; turn 180 0.35; wait 2; jump 0.35; wait 2; shake 30 2 1.2; wait 2.5" },
        };

        static readonly char[] Ws = { ' ', '\t' };

        public static ProbeScenario Parse(string src, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(src)) src = "standard";
            var sc = new ProbeScenario();
            var lines = new List<string>();
            var presetNames = new List<string>();
            if (!Expand(src, lines, 0, presetNames, out error)) return null;
            if (presetNames.Count > 0) sc.Label = string.Join("+", presetNames.Take(3));

            float t = 0f;
            var expanded = new List<string>();
            foreach (var line in lines)
            {
                var tok = line.Split(Ws, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length == 0) continue;
                string op = tok[0].ToLowerInvariant();
                var cmd = new ProbeCommand { Time = t, Op = op, Text = line };
                switch (op)
                {
                    case "wait":
                    case "w":
                        if (!Num(tok, 1, out float s) || s < 0f) { error = "wait needs a non-negative number: '" + line + "'"; return null; }
                        t += s;
                        expanded.Add("wait " + ProbeIO.F(s));
                        continue;
                    case "walk":
                        cmd.Op = "move"; cmd.Num = new[] { 0f, Opt(tok, 1, 1.6f), Opt(tok, 2, 8f) }; cmd.Label = "WALK";
                        break;
                    case "run":
                        cmd.Op = "move"; cmd.Num = new[] { 0f, Opt(tok, 1, 4f), Opt(tok, 2, 10f) }; cmd.Label = "RUN";
                        break;
                    case "move":
                        if (!Num(tok, 1, out float vx) || !Num(tok, 2, out float vz)) { error = "move needs VX VZ: '" + line + "'"; return null; }
                        cmd.Num = new[] { vx, vz, Opt(tok, 3, 8f) }; cmd.Label = "MOVE";
                        break;
                    case "stop":
                        cmd.Op = "move"; cmd.Num = new[] { 0f, 0f, Opt(tok, 1, 14f) }; cmd.Label = "STOP";
                        break;
                    case "turn":
                        if (!Num(tok, 1, out float deg)) { error = "turn needs DEG: '" + line + "'"; return null; }
                        cmd.Num = new[] { deg, Math.Max(0.05f, Opt(tok, 2, 0.35f)) }; cmd.Label = "TURN";
                        break;
                    case "jump":
                        cmd.Num = new[] { Math.Max(0.02f, Math.Min(2f, Opt(tok, 1, 0.35f))) }; cmd.Label = "JUMP";
                        sc.MaxJump = Math.Max(sc.MaxJump, cmd.Num[0]);
                        break;
                    case "shake":
                        cmd.Num = new[] { Opt(tok, 1, 30f), Math.Max(0.1f, Opt(tok, 2, 2f)), Math.Max(0.1f, Opt(tok, 3, 1.5f)) }; cmd.Label = "SHAKE";
                        break;
                    case "sway":
                        cmd.Num = new[] { Opt(tok, 1, 6f), Math.Max(0.1f, Opt(tok, 2, 1.5f)), Math.Max(0.1f, Opt(tok, 3, 2f)) }; cmd.Label = "SWAY";
                        break;
                    case "bob":
                        cmd.Num = new[] { Opt(tok, 1, 3f), Math.Max(0.1f, Opt(tok, 2, 2f)), Math.Max(0.1f, Opt(tok, 3, 1.5f)) }; cmd.Label = "BOB";
                        break;
                    case "param":
                        if (tok.Length < 3 || !ParseValue(tok[2], out float pv)) { error = "param needs NAME VALUE: '" + line + "'"; return null; }
                        cmd.Name = tok[1]; cmd.Num = new[] { pv }; cmd.Label = tok[1] + "=" + ProbeIO.F(pv, pv == (int)pv ? 0 : 2);
                        break;
                    case "gesture":
                        cmd.Num = new[] { Opt(tok, 1, 0f), Opt(tok, 2, 0f) }; cmd.Label = "GESTURE " + (int)cmd.Num[0] + "/" + (int)cmd.Num[1];
                        break;
                    case "mark":
                        cmd.Label = tok.Length > 1 ? string.Join(" ", tok.Skip(1)).ToUpperInvariant() : "MARK";
                        break;
                    default:
                        error = "unknown command '" + tok[0] + "' (see MotionProbe.Help())";
                        return null;
                }
                sc.Commands.Add(cmd);
                expanded.Add(line);
            }
            sc.Duration = t;
            sc.Expanded = string.Join("; ", expanded);
            if (sc.Duration < 0.05f) { error = "scenario has no duration; add e.g. 'wait 2'"; return null; }
            if (sc.Duration > 120f) { error = "scenario too long (max 120 s)"; return null; }
            return sc;
        }

        static bool Expand(string src, List<string> lines, int depth, List<string> presetNames, out string error)
        {
            error = null;
            if (depth > 4) { error = "preset nesting too deep"; return false; }
            foreach (var raw in src.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                var tok = line.Split(Ws, StringSplitOptions.RemoveEmptyEntries);
                string presetName = null;
                if (tok.Length == 1 && Presets.ContainsKey(tok[0])) presetName = tok[0];
                else if (tok.Length >= 2 && tok[0].Equals("preset", StringComparison.OrdinalIgnoreCase) && Presets.ContainsKey(tok[1])) presetName = tok[1];
                if (presetName != null)
                {
                    if (depth == 0) presetNames.Add(presetName.ToLowerInvariant());
                    lines.Add("mark " + presetName.ToUpperInvariant());
                    if (!Expand(Presets[presetName], lines, depth + 1, presetNames, out error)) return false;
                    continue;
                }
                lines.Add(line);
            }
            return true;
        }

        static bool Num(string[] tok, int i, out float v)
        {
            v = 0f;
            return i < tok.Length && float.TryParse(tok[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        static float Opt(string[] tok, int i, float def) => Num(tok, i, out float v) ? v : def;

        static bool ParseValue(string s, out float v)
        {
            switch (s.ToLowerInvariant())
            {
                case "true": case "on": v = 1f; return true;
                case "false": case "off": v = 0f; return true;
            }
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
