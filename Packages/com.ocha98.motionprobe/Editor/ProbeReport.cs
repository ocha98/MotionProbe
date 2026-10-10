using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace MotionProbeTool
{
    /// <summary>Markdown report + CSV export. Written for an LLM reader: numbers first, explicit units and frames.</summary>
    internal static class ProbeReport
    {
        static string F(float v, int dec = 2) => ProbeIO.F(v, dec);
        static string Cm(float m, int dec = 1) => ProbeIO.Cm(m, dec);

        public static string Write(ProbeData d, ProbeAnalysis a, List<string> files, List<string> errors)
        {
            var m = d.meta;
            var sb = new StringBuilder();
            string run = Path.GetFileName(d.dir);
            sb.AppendLine("# MotionProbe report: " + run);
            sb.AppendLine();
            sb.AppendLine("- avatar: " + m.avatar + " | " + F(m.duration) + " s simulated at " + F(m.fps, 0) + " fps (" + m.frameCount + " frames, fixed time step) after " + F(m.preroll) + " s of settling | images at " + F(m.imageFps, 0) + " fps | real time " + F(m.realSeconds, 1) + " s");
            sb.AppendLine("- scenario: `" + m.scenarioExpanded + "`");
            sb.AppendLine("- axes: x = avatar's right, y = up, z = avatar's forward. Distances in cm, times in s from the scenario start (t=0 is the settled rest pose).");
            sb.AppendLine("- PhysBone displacement = tip position minus where the tip would be if the chain followed its parent bone rigidly in the t=0 rest pose, so body animation is cancelled out and only the physics swing remains.");
            sb.AppendLine();

            sb.AppendLine("## Headline");
            foreach (var h in a.headline) sb.AppendLine("- " + h);
            sb.AppendLine();

            sb.AppendLine("## Images (in this folder)");
            var desc = new Dictionary<string, string>
            {
                { "sheet_body.png", "full body; rows = views (front, side = seen from the avatar's right, back), columns = key moments (largest swing in each segment + start/end), header = time and event context" },
                { "sheet_head.png", "same moments as sheet_body, head/hair close-up" },
                { "trail_physbone.png", "one zoomed front+side panel pair per most-active PhysBone group: tip paths relative to the parent bone (body motion removed), colour = time (blue -> green -> yellow -> red), white = rest shape, orange = shape at the group's peak, bar = scale" },
                { "trail_body.png", "whole-body front/side: head/hips/hands/feet paths relative to the avatar root, dots every 0.1 s, ring = start, square = end" },
                { "trail_cycle.png", "zoomed per-limb paths over exactly one loop of the detected periodic motion; numbered dots 0-7 = the phases shown in sheet_cycle.png" },
                { "chart.png", "top: PhysBone displacement over time for the most active groups; bottom: root speed and limb speeds; vertical lines = events" },
                { "sheet_cycle.png", "one period of the detected periodic body motion in 8 phases (front, side)" },
                { "data/chains.csv", "per-chain summary numbers" },
                { "data/timeseries_cm.csv", "tip displacement of every chain over time" },
                { "data/body.csv", "root pose and limb positions relative to the root over time" },
            };
            foreach (var f in files) sb.AppendLine("- " + f + (desc.TryGetValue(f, out string ds) ? ": " + ds : ""));
            sb.AppendLine();

            sb.AppendLine("## Timeline");
            sb.AppendLine("| t | event | command |");
            sb.AppendLine("|---|---|---|");
            foreach (var e in m.events) sb.AppendLine("| " + F(e.t) + " | " + e.label + " | `" + e.text + "` |");
            sb.AppendLine("| " + F(m.duration) + " | END | |");
            sb.AppendLine();

            WriteBody(sb, d, a);
            WriteGroups(sb, d, a);

            sb.AppendLine("## Warnings");
            if (a.warnings.Count == 0) sb.AppendLine("- none");
            foreach (var w in a.warnings) sb.AppendLine("- " + w);
            sb.AppendLine();
            sb.AppendLine("## Info");
            if (a.infos.Count == 0) sb.AppendLine("- none");
            foreach (var w in a.infos) sb.AppendLine("- " + w);
            sb.AppendLine("- Rule of thumb (not a strict rule): natural hair/cloth shows a clear lag, 1-3 visible swings after a stop, settles within roughly 0.5-2 s, and swings at a frequency in the same range as a hanging rod of the same length (\"rod\" column). Much faster = springy/stiff, much slower = floaty. Jitter, clipping and never settling read as broken.");
            sb.AppendLine();

            WriteResponses(sb, d, a);
            WriteSeries(sb, d, a);
            WritePairs(sb, d, a);
            WriteSettings(sb, d, a);

            sb.AppendLine("## Notes");
            if (m.notes.Count == 0 && errors.Count == 0) sb.AppendLine("- none");
            foreach (var n in m.notes) sb.AppendLine("- " + n);
            foreach (var e in errors) sb.AppendLine("- image/data error: " + e);
            sb.AppendLine();

            sb.AppendLine("## Drill-down (call via execute_code; no re-simulation needed)");
            sb.AppendLine("```");
            sb.AppendLine("MotionProbe.SheetAt(\"" + run + "\", \"head\", \"1.80,1.90,2.00,2.20\", \"side,back\")");
            sb.AppendLine("MotionProbe.Sheet(\"" + run + "\", \"body\", 0f, " + F(m.duration) + "f, 8, \"front,side\")");
            sb.AppendLine("MotionProbe.Trail(\"" + run + "\", 0f, -1f, \"physbone\", \"Hair\")");
            sb.AppendLine("MotionProbe.Chart(\"" + run + "\", 0f, -1f, \"Hair\")");
            sb.AppendLine("MotionProbe.Chain(\"" + run + "\", \"" + (a.groups.Count > 0 ? a.groups[0].name : "Hair") + "\")");
            sb.AppendLine("```");
            return sb.ToString();
        }

        static void WriteBody(StringBuilder sb, ProbeData d, ProbeAnalysis a)
        {
            sb.AppendLine("## Body motion");
            sb.AppendLine("- root: traveled " + F(a.travel) + " m, max speed " + F(a.maxSpeed) + " m/s, max yaw change " + F(a.yawMaxAbs, 0) + " deg (net " + F(a.yawTotal, 0) + "), max rise " + Cm(a.maxRise) + " cm. Body still during the last 0.5 s: " + (a.staticAtEnd ? "yes" : "no") + ".");
            if (a.effPt.Length == 0) { sb.AppendLine("- (no humanoid bones)"); sb.AppendLine(); return; }
            sb.AppendLine();
            sb.AppendLine("'steady' = the segment without its first 0.6 s, i.e. after the transition into the new motion has finished.");
            sb.AppendLine();
            sb.AppendLine("| segment | t | limb RMS cm whole / steady | max limb speed m/s | loop period (steady) | largest ranges whole segment (cm x/y/z) | largest ranges steady part (cm x/y/z) |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var b in a.body)
            {
                string Top(Dictionary<string, Vector3> r) => string.Join(", ", r.Where(kv => ProbeAnalysis.EffMain.Contains(kv.Key)).OrderByDescending(kv => kv.Value.magnitude).Take(3)
                    .Select(kv => kv.Key + " " + Cm(kv.Value.x) + "/" + Cm(kv.Value.y) + "/" + Cm(kv.Value.z)));
                sb.AppendLine("| " + b.seg.label + " | " + F(b.seg.t0) + "-" + F(b.seg.t1) + " | " + Cm(b.motionRms) + " / " + (b.steadyT0 >= 0f ? Cm(b.steadyRms) : "-") + " | " + F(b.maxEffSpeed) + " | "
                    + (b.period > 0f ? F(b.period, 3) + " s (r=" + F(b.periodR) + ")" : "-") + " | " + Top(b.range) + " | " + (b.steadyT0 >= 0f ? Top(b.steadyRange) : "-") + " |");
            }
            sb.AppendLine();
            sb.AppendLine("Pose samples at the key moments (positions relative to the root in cm, x right / y up / z forward):");
            sb.AppendLine();
            var cols = new[] { "Head", "LeftHand", "RightHand", "LeftFoot", "RightFoot" };
            sb.AppendLine("| t | context | " + string.Join(" | ", cols) + " |");
            sb.AppendLine("|---|---|" + string.Concat(cols.Select(_ => "---|")));
            for (int i = 0; i < a.keyTimes.Count; i++)
            {
                int f = d.FrameAt(a.keyTimes[i]);
                var cells = cols.Select(n =>
                {
                    int e = Array.IndexOf(a.effName, n);
                    if (e < 0) return "-";
                    var v = a.effRel[e][f];
                    return Cm(v.x, 0) + "," + Cm(v.y, 0) + "," + Cm(v.z, 0);
                });
                sb.AppendLine("| " + F(f * d.meta.dt) + " | " + a.Ctx(f * d.meta.dt) + " | " + string.Join(" | ", cells) + " |");
            }
            sb.AppendLine();
        }

        static void WriteGroups(StringBuilder sb, ProbeData d, ProbeAnalysis a)
        {
            sb.AppendLine("## PhysBone groups (sorted by peak displacement)");
            if (a.groups.Count == 0) { sb.AppendLine("- none"); sb.AppendLine(); return; }
            int last = a.segs.Count - 1;
            string lastLabel = last >= 0 ? a.segs[last].label : "-";
            sb.AppendLine("Groups merge components whose names differ only by number/side (e.g. HairB1..HairB14, HairB8.L/.R). 'after " + lastLabel + "' columns describe the final segment.");
            sb.AppendLine();
            sb.AppendLine("| group | chains | max len cm | peak cm | %len | max deg | at t (context) | settle after " + lastLabel + " s | freq Hz (rod) | decay/half-swing | main axis | most active chain |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var g in a.groups)
            {
                var best = a.cs[g.best];
                string settle = "-", freq = "-", decay = "-";
                if (last >= 0)
                {
                    var moving = g.chains.Where(c => a.cs[c].segs[last].peak > 0.005f).ToList();
                    if (moving.Count > 0)
                    {
                        int notSettled = moving.Count(c => a.cs[c].segs[last].settle < 0f);
                        var st = moving.Where(c => a.cs[c].segs[last].settle >= 0f).Select(c => a.cs[c].segs[last].settle).OrderBy(x => x).ToList();
                        settle = notSettled > 0 ? "no (" + notSettled + "/" + moving.Count + ")" : F(st[st.Count / 2]);
                        var fr = moving.Select(c => a.cs[c].segs[last].freq).Where(x => x > 0f).OrderBy(x => x).ToList();
                        if (fr.Count > 0) freq = F(fr[fr.Count / 2]);
                        var de = moving.Select(c => a.cs[c].segs[last].decay).Where(x => x >= 0f).OrderBy(x => x).ToList();
                        if (de.Count > 0) decay = F(de[de.Count / 2]);
                    }
                }
                freq += " (" + F(best.rodFreq) + ")";
                int segIdx = a.segs.FindIndex(s => best.peakT >= s.t0 - 1e-4f && best.peakT <= s.t1 + 1e-4f);
                string axis = segIdx >= 0 ? ProbeAnalysis.AxisWords(best.segs[segIdx].axis) : "-";
                if (a.HangNote(g.best).Length > 0) axis += "; hangs under gravity (parent rotated)";
                sb.AppendLine("| " + g.name + " | " + g.chains.Count + " | " + Cm(g.len) + " | " + Cm(g.peak) + " | " + F(g.pctLen, 0) + " | " + F(g.peakAng, 0)
                    + " | " + F(g.peakT) + " (" + a.Ctx(g.peakT) + ") | " + settle + " | " + freq + " | " + decay + " | " + axis + " | " + d.meta.chains[g.best].name + " |");
            }
            sb.AppendLine();
        }

        static void WriteResponses(StringBuilder sb, ProbeData d, ProbeAnalysis a)
        {
            sb.AppendLine("## Response per segment (top chains by peak displacement inside the segment)");
            for (int si = 0; si < a.segs.Count; si++)
            {
                var sg = a.segs[si];
                if (sg.f1 - sg.f0 < 5) continue;
                var top = Enumerable.Range(0, a.cs.Length).Where(c => a.cs[c].segs[si].peak > 0.003f)
                    .OrderByDescending(c => a.cs[c].segs[si].peak).Take(6).ToList();
                sb.AppendLine("### " + sg.label + " (t " + F(sg.t0) + "-" + F(sg.t1) + ")");
                if (top.Count == 0) { sb.AppendLine("- nothing moves more than 0.3 cm"); continue; }
                sb.AppendLine("| chain | peak cm (+s after start) | max deg | main axis | swings | freq Hz (rod) | decay/half-swing | settle s |");
                sb.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (int c in top)
                {
                    var s = a.cs[c].segs[si];
                    float maxDeg = 0f;
                    for (int f = sg.f0; f <= Mathf.Min(sg.f1, a.F - 1); f++) maxDeg = Mathf.Max(maxDeg, a.ang[c][f]);
                    sb.AppendLine("| " + d.meta.chains[c].name + " | " + Cm(s.peak) + " (+" + F(s.peakT - sg.t0) + ") | " + F(maxDeg, 0) + " | " + ProbeAnalysis.AxisWords(s.axis)
                        + " | " + s.swings + " | " + (s.freq > 0f ? F(s.freq) : "-") + " (" + F(a.cs[c].rodFreq) + ") | " + (s.decay >= 0f ? F(s.decay) : "-")
                        + " | " + (s.settle >= 0f ? F(s.settle) : "not settled") + " |");
                }
            }
            sb.AppendLine();
        }

        static List<int> TopChains(ProbeAnalysis a, int n) =>
            a.groups.Where(g => g.peak > 0.002f).Take(n).Select(g => g.best).ToList();

        static void WriteSeries(StringBuilder sb, ProbeData d, ProbeAnalysis a)
        {
            var top = TopChains(a, 8);
            if (top.Count == 0) return;
            float step = Mathf.Max(0.1f, Mathf.Ceil(a.T / 40f / 0.05f) * 0.05f);
            int n = Mathf.FloorToInt(a.T / step + 1e-3f) + 1;
            sb.AppendLine("## Tip displacement time series (cm, every " + F(step) + " s; most active chain of each top group)");
            sb.AppendLine("```");
            int nameW = Mathf.Min(28, top.Max(c => d.meta.chains[c].name.Length));
            var line = new StringBuilder("t".PadRight(nameW) + " |");
            for (int i = 0; i < n; i++) line.Append(' ').Append(F(i * step, 1).PadLeft(4));
            sb.AppendLine(line.ToString());
            line.Clear().Append("event".PadRight(nameW) + " |");
            for (int i = 0; i < n; i++)
            {
                var ev = d.meta.events.FirstOrDefault(e => e.t > (i - 0.5f) * step && e.t <= (i + 0.5f) * step);
                line.Append(' ').Append((ev != null ? ev.label.Substring(0, Math.Min(4, ev.label.Length)) : "").PadLeft(4));
            }
            sb.AppendLine(line.ToString());
            foreach (int c in top)
            {
                string nm = d.meta.chains[c].name;
                if (nm.Length > nameW) nm = nm.Substring(0, nameW);
                line.Clear().Append(nm.PadRight(nameW) + " |");
                for (int i = 0; i < n; i++) line.Append(' ').Append(Cm(a.mag[c][d.FrameAt(i * step)]).PadLeft(4));
                sb.AppendLine(line.ToString());
            }
            sb.AppendLine("```");
            sb.AppendLine();
        }

        static void WritePairs(StringBuilder sb, ProbeData d, ProbeAnalysis a)
        {
            if (a.pairs.Count == 0) return;
            sb.AppendLine("## Left/right pairs (peak displacement cm)");
            var rows = a.pairs.OrderByDescending(p => Mathf.Abs(p.pa - p.pb)).Take(12)
                .Select(p => d.meta.comps[p.a].name + " " + Cm(p.pa) + " / " + d.meta.comps[p.b].name + " " + Cm(p.pb)
                    + (Mathf.Max(p.pa, p.pb) > 0.01f && Mathf.Abs(p.pa - p.pb) / Mathf.Max(p.pa, p.pb) > 0.4f ? "  <- differs" : ""));
            foreach (var r in rows) sb.AppendLine("- " + r);
            if (a.pairs.Count > 12) sb.AppendLine("- ... " + (a.pairs.Count - 12) + " more pairs, all closer than the ones above");
            sb.AppendLine();
        }

        static void WriteSettings(StringBuilder sb, ProbeData d, ProbeAnalysis a)
        {
            sb.AppendLine("## PhysBone settings by group (spring is shown as 'Momentum' in the inspector for Advanced integration; ~ = curve set)");
            foreach (var g in a.groups)
            {
                var variants = g.comps.GroupBy(ci => d.meta.comps[ci].settings).ToList();
                var comp0 = d.meta.comps[g.comps[0]];
                string cols = comp0.colliders != null && comp0.colliders.Length > 0 ? " colliders: " + string.Join(",", comp0.colliders) : "";
                if (variants.Count == 1)
                    sb.AppendLine("- " + g.name + " (" + g.comps.Count + " comp, parent " + comp0.parentName + "): " + variants[0].Key + cols);
                else
                {
                    sb.AppendLine("- " + g.name + " (" + g.comps.Count + " comp, " + variants.Count + " variants):");
                    foreach (var v in variants)
                    {
                        var names = v.Select(ci => d.meta.comps[ci].name).ToList();
                        sb.AppendLine("  - " + string.Join(", ", names.Take(6)) + (names.Count > 6 ? " +" + (names.Count - 6) : "") + ": " + v.Key);
                    }
                }
            }
            sb.AppendLine();
        }

        public static string ChainDetail(ProbeData d, ProbeAnalysis a, string filter)
        {
            var sb = new StringBuilder();
            string fl = filter.ToLowerInvariant();
            var list = Enumerable.Range(0, d.meta.chains.Count).Where(c =>
            {
                var ch = d.meta.chains[c];
                var comp = d.meta.comps[ch.comp];
                return fl.Length == 0 || ch.name.ToLowerInvariant().Contains(fl) || comp.group.ToLowerInvariant().Contains(fl);
            }).OrderByDescending(c => a.cs[c].peak).ToList();
            if (list.Count == 0) return "No chain matches '" + filter + "'. Groups: " + string.Join(", ", a.groups.Select(g => g.name));
            sb.AppendLine(list.Count + " chain(s) match '" + filter + "'" + (list.Count > 12 ? " (showing the 12 most active)" : "") + ". Units: cm, deg, s. Axes: x right, y up, z forward.");
            foreach (int c in list.Take(12))
            {
                var ch = d.meta.chains[c];
                var comp = d.meta.comps[ch.comp];
                var s = a.cs[c];
                sb.AppendLine();
                sb.AppendLine("### " + ch.name + "  (group " + comp.group + ", parent bone " + comp.parentName + ", " + ch.pts.Length + " joints, rest length " + Cm(s.restLen) + " cm)");
                sb.AppendLine("path: " + comp.path);
                sb.AppendLine("settings: " + comp.settings + (comp.colliders != null && comp.colliders.Length > 0 ? " [" + string.Join(",", comp.colliders) + "]" : ""));
                sb.AppendLine("peak " + Cm(s.peak) + " cm (" + F(s.pctLen, 0) + "% of length) at " + F(s.peakT) + " (" + a.Ctx(s.peakT) + "); max angle " + F(s.peakAng, 0) + " deg at " + F(s.peakAngT)
                    + "; rod-pendulum freq " + F(s.rodFreq) + " Hz; jitter " + (s.jitAmp > 0f ? F(s.jitAmp * 1000f, 2) + " mm (reverses on " + F(s.jitRatio * 100f, 0) + "% of frames) near " + F(s.jitT) : "none")
                    + "; length " + F(s.strMin * 100f, 0) + "-" + F(s.strMax * 100f, 0) + "% of rest; clearance to own colliders " + (s.clrA < 8f ? Cm(s.clrA) + " cm" : "n/a")
                    + ", to any collider " + (s.clrAll < 8f ? Cm(s.clrAll) + " cm (" + d.meta.colliders[Mathf.Max(0, s.clrAllIdx)].name + ")" : "n/a") + "; end " + Cm(s.endMag) + " cm, end speed " + Cm(s.restSpeed) + " cm/s");
                sb.AppendLine("| segment | peak (+s) | axis | swings | freq | decay | settle |");
                sb.AppendLine("|---|---|---|---|---|---|---|");
                for (int si = 0; si < a.segs.Count; si++)
                {
                    var g = s.segs[si];
                    sb.AppendLine("| " + a.segs[si].label + " " + F(a.segs[si].t0) + "-" + F(a.segs[si].t1) + " | " + Cm(g.peak) + " (+" + F(g.peakT - a.segs[si].t0) + ") | " + ProbeAnalysis.AxisWords(g.axis)
                        + " | " + g.swings + " | " + (g.freq > 0f ? F(g.freq) : "-") + " | " + (g.decay >= 0f ? F(g.decay) : "-") + " | " + (g.settle >= 0f ? F(g.settle) : "no") + " |");
                }
                float step = 0.05f;
                int n = Mathf.FloorToInt(a.T / step + 1e-3f) + 1;
                var mags = new StringBuilder("disp cm every 0.05 s:");
                var angs = new StringBuilder("angle deg every 0.05 s:");
                var dirs = new StringBuilder("disp x/y/z cm every 0.1 s:");
                for (int i = 0; i < n; i++)
                {
                    int f = d.FrameAt(i * step);
                    mags.Append(' ').Append(Cm(a.mag[c][f]));
                    angs.Append(' ').Append(F(a.ang[c][f], 0));
                    if (i % 2 == 0) { var v = a.dev[c][f]; dirs.Append(' ').Append(Cm(v.x)).Append('/').Append(Cm(v.y)).Append('/').Append(Cm(v.z)); }
                }
                sb.AppendLine(mags.ToString());
                sb.AppendLine(angs.ToString());
                sb.AppendLine(dirs.ToString());
            }
            return sb.ToString();
        }

        public static void WriteCsv(ProbeData d, ProbeAnalysis a)
        {
            string dir = d.dir + "/data";
            Directory.CreateDirectory(dir);
            var inv = CultureInfo.InvariantCulture;
            int last = a.segs.Count - 1;

            var sb = new StringBuilder("chain,group,component,parent,joints,rest_cm,peak_cm,peak_t,peak_deg,pct_len,rod_hz,final_settle_s,final_freq_hz,final_decay,jitter_mm,jitter_reversal_frac,stretch_max,stretch_min,clear_own_cm,clear_any_cm,clear_any_collider,end_cm\n");
            for (int c = 0; c < a.cs.Length; c++)
            {
                var ch = d.meta.chains[c];
                var comp = d.meta.comps[ch.comp];
                var s = a.cs[c];
                var ls = last >= 0 ? s.segs[last] : new ChainSeg();
                sb.Append(Q(ch.name)).Append(',').Append(Q(comp.group)).Append(',').Append(Q(comp.name)).Append(',').Append(Q(comp.parentName)).Append(',').Append(ch.pts.Length)
                  .Append(',').Append(Cm(s.restLen, 2)).Append(',').Append(Cm(s.peak, 2)).Append(',').Append(F(s.peakT, 3)).Append(',').Append(F(s.peakAng, 1))
                  .Append(',').Append(F(s.pctLen, 1)).Append(',').Append(F(s.rodFreq, 2)).Append(',').Append(F(ls.settle, 2)).Append(',').Append(F(ls.freq, 2))
                  .Append(',').Append(F(ls.decay, 2)).Append(',').Append(F(s.jitAmp * 1000f, 2)).Append(',').Append(F(s.jitRatio, 2)).Append(',').Append(F(s.strMax, 3))
                  .Append(',').Append(F(s.strMin, 3)).Append(',').Append(s.clrA < 8f ? Cm(s.clrA, 2) : "").Append(',').Append(s.clrAll < 8f ? Cm(s.clrAll, 2) : "")
                  .Append(',').Append(s.clrAllIdx >= 0 && s.clrAllIdx < d.meta.colliders.Count ? Q(d.meta.colliders[s.clrAllIdx].name) : "").Append(',').Append(Cm(s.endMag, 2)).Append('\n');
            }
            File.WriteAllText(dir + "/chains.csv", sb.ToString());

            int every = Mathf.Max(1, d.meta.imageEvery);
            sb.Clear().Append("t");
            foreach (var ch in d.meta.chains) sb.Append(',').Append(Q(ch.name));
            sb.Append('\n');
            for (int f = 0; f < a.F; f += every)
            {
                sb.Append((f * d.meta.dt).ToString("F3", inv));
                for (int c = 0; c < a.cs.Length; c++) sb.Append(',').Append(Cm(a.mag[c][f], 2));
                sb.Append('\n');
            }
            File.WriteAllText(dir + "/timeseries_cm.csv", sb.ToString());

            sb.Clear().Append("t,root_x,root_y,root_z,root_yaw");
            foreach (var n in a.effName) sb.Append(',').Append(n).Append("_x,").Append(n).Append("_y,").Append(n).Append("_z");
            sb.Append('\n');
            for (int f = 0; f < a.F; f += every)
            {
                var rp = d.rootPos[f];
                sb.Append((f * d.meta.dt).ToString("F3", inv)).Append(',').Append(Cm(rp.x, 1)).Append(',').Append(Cm(rp.y, 1)).Append(',').Append(Cm(rp.z, 1))
                  .Append(',').Append(F(d.rootRot[f].eulerAngles.y, 1));
                for (int e = 0; e < a.effPt.Length; e++)
                {
                    var v = a.effRel[e][f];
                    sb.Append(',').Append(Cm(v.x, 1)).Append(',').Append(Cm(v.y, 1)).Append(',').Append(Cm(v.z, 1));
                }
                sb.Append('\n');
            }
            File.WriteAllText(dir + "/body.csv", sb.ToString());
        }

        static string Q(string s) => s.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
