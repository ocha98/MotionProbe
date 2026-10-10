using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace MotionProbeTool
{
    internal class SegInfo { public float t0, t1; public int f0, f1; public string label; }

    internal class ChainSeg
    {
        public float peak, peakT, settle = -1f, freq, decay = -1f, endMag;
        public int swings;
        public Vector3 axis;
    }

    internal class ChainStat
    {
        public int chain;
        public float restLen, peak, peakT, peakAng, peakAngT, pctLen, rodFreq;
        public float jitAmp, jitRatio, jitT;
        public float strMax = 1f, strMaxT, strMin = 1f, strMinT;
        public float clrA = 9f, clrAT, clrAll = 9f, clrAllT;
        public int clrAllIdx = -1;
        public float endMag, restSpeed;
        public float downAngAtPeak = 90f; // angle between root->tip and world down at the peak frame
        public ChainSeg[] segs;
    }

    internal class GroupStat
    {
        public string name;
        public readonly List<int> comps = new List<int>();
        public readonly List<int> chains = new List<int>();
        public int best = -1;
        public float peak, peakT, len, pctLen, peakAng;
    }

    internal class BodySeg
    {
        public SegInfo seg;
        public float motionRms, maxEffSpeed, period = -1f, periodR;
        public readonly Dictionary<string, Vector3> range = new Dictionary<string, Vector3>();
        // "steady part" = segment minus its first 0.6 s (transition into the new motion)
        public float steadyT0 = -1f, steadyRms;
        public readonly Dictionary<string, Vector3> steadyRange = new Dictionary<string, Vector3>();
    }

    /// <summary>Turns raw recordings into numbers: PhysBone swing/settle/jitter/collision, body motion, periodicity.</summary>
    internal class ProbeAnalysis
    {
        public ProbeData d;
        public float dt, T;
        public int F;
        public readonly List<SegInfo> segs = new List<SegInfo>();
        public Vector3[][] dev;  // [chain][frame] tip displacement from the rigid rest pose, body axes (m)
        public float[][] mag;    // |dev| (m)
        public float[][] ang;    // swing angle at the chain root (deg)
        public float[][] len;    // chain length (m)
        public ChainStat[] cs;
        public readonly List<GroupStat> groups = new List<GroupStat>();
        public float[] meanMag;
        public readonly List<BodySeg> body = new List<BodySeg>();
        public float travel, maxSpeed, yawTotal, yawMaxAbs, maxRise;
        public bool staticAtEnd;
        public float[] rootSpeed;
        public int[] effPt;
        public string[] effName;
        public Vector3[][] effRel; // [effector][frame] position relative to the root, body axes (m)
        public readonly List<string> warnings = new List<string>();
        public readonly List<string> infos = new List<string>();
        public readonly List<string> headline = new List<string>();
        public readonly List<float> keyTimes = new List<float>();
        public readonly List<string> keyLabels = new List<string>();
        public readonly List<(int a, int b, float pa, float pb)> pairs = new List<(int, int, float, float)>();

        static readonly string[] EffWanted = { "Head", "Hips", "Chest", "LeftHand", "RightHand", "LeftFoot", "RightFoot", "LeftLowerArm", "RightLowerArm", "LeftLowerLeg", "RightLowerLeg" };
        public static readonly string[] EffMain = { "Head", "Hips", "LeftHand", "RightHand", "LeftFoot", "RightFoot" };

        public static ProbeAnalysis Run(ProbeData d)
        {
            var a = new ProbeAnalysis { d = d };
            a.Compute();
            return a;
        }

        void Compute()
        {
            dt = d.meta.dt;
            F = d.F;
            T = (F - 1) * dt;
            BuildSegments();
            ChainSeries();
            ChainStats();
            Groups();
            Body();
            Flags();
            KeyTimes();
            Headline();
        }

        public string Ctx(float t)
        {
            ProbeEventRec best = null;
            foreach (var e in d.meta.events)
                if (e.t <= t + 1e-4f && (best == null || e.t >= best.t)) best = e;
            if (best == null) return "start+" + ProbeIO.F(t);
            return best.label + "+" + ProbeIO.F(t - best.t);
        }

        void BuildSegments()
        {
            var cuts = new SortedSet<int> { 0, F - 1 };
            foreach (var e in d.meta.events)
            {
                int f = Mathf.RoundToInt(e.t / dt);
                if (f > 0 && f < F - 1) cuts.Add(f);
            }
            var list = cuts.ToList();
            for (int i = 0; i + 1 < list.Count; i++)
            {
                int f0 = list[i], f1 = list[i + 1];
                var labels = d.meta.events.Where(e => Mathf.RoundToInt(e.t / dt) == f0).Select(e => e.label).Distinct().ToList();
                segs.Add(new SegInfo { f0 = f0, f1 = f1, t0 = f0 * dt, t1 = f1 * dt, label = labels.Count > 0 ? string.Join("+", labels) : "START" });
            }
        }

        void ChainSeries()
        {
            int C = d.meta.chains.Count;
            dev = new Vector3[C][]; mag = new float[C][]; ang = new float[C][]; len = new float[C][];
            meanMag = new float[F];
            for (int c = 0; c < C; c++)
            {
                var ch = d.meta.chains[c];
                int tip = ch.pts[ch.pts.Length - 1], r0 = ch.pts[0];
                Vector3 refTipL = d.meta.points[tip].refLocal, refRootL = d.meta.points[r0].refLocal;
                dev[c] = new Vector3[F]; mag[c] = new float[F]; ang[c] = new float[F]; len[c] = new float[F];
                for (int f = 0; f < F; f++)
                {
                    Matrix4x4 M = d.FrameM(f, ch.frame);
                    Vector3 refTip = M.MultiplyPoint3x4(refTipL), refRoot = M.MultiplyPoint3x4(refRootL);
                    Vector3 tipW = d.Pt(f, tip);
                    Vector3 dw = tipW - refTip;
                    dev[c][f] = Quaternion.Inverse(d.rootRot[f]) * dw;
                    mag[c][f] = dw.magnitude;
                    Vector3 va = tipW - d.Pt(f, r0), vb = refTip - refRoot;
                    ang[c][f] = va.sqrMagnitude > 1e-10f && vb.sqrMagnitude > 1e-10f ? Vector3.Angle(va, vb) : 0f;
                    float L = 0f;
                    for (int i = 0; i + 1 < ch.pts.Length; i++) L += Vector3.Distance(d.Pt(f, ch.pts[i]), d.Pt(f, ch.pts[i + 1]));
                    len[c][f] = L;
                    meanMag[f] += mag[c][f];
                }
            }
            if (C > 0) for (int f = 0; f < F; f++) meanMag[f] /= C;
        }

        void ChainStats()
        {
            int C = d.meta.chains.Count;
            cs = new ChainStat[C];
            int win = Mathf.Max(8, Mathf.RoundToInt(0.25f / dt));
            int nEnd = Mathf.Clamp(Mathf.RoundToInt(0.5f / dt), 2, F - 1);
            for (int c = 0; c < C; c++)
            {
                var s = new ChainStat { chain = c, restLen = d.meta.chains[c].restLength };
                for (int f = 0; f < F; f++)
                {
                    float t = f * dt;
                    if (mag[c][f] > s.peak) { s.peak = mag[c][f]; s.peakT = t; }
                    if (ang[c][f] > s.peakAng) { s.peakAng = ang[c][f]; s.peakAngT = t; }
                    if (s.restLen > 1e-4f)
                    {
                        float r = len[c][f] / s.restLen;
                        if (r > s.strMax) { s.strMax = r; s.strMaxT = t; }
                        if (r < s.strMin) { s.strMin = r; s.strMinT = t; }
                    }
                    int o = f * d.C + c;
                    if (d.clrA[o] < s.clrA) { s.clrA = d.clrA[o]; s.clrAT = t; }
                    if (d.clrAll[o] < s.clrAll) { s.clrAll = d.clrAll[o]; s.clrAllT = t; s.clrAllIdx = d.clrIdx[o]; }
                }
                s.pctLen = s.restLen > 1e-4f ? s.peak / s.restLen * 100f : 0f;
                s.rodFreq = s.restLen > 0.01f ? Mathf.Sqrt(1.5f * 9.81f / s.restLen) / (2f * Mathf.PI) : 0f;

                // Jitter = the acceleration flips direction (almost) every frame, sustained over a 0.25 s window.
                // Smooth swings, strong onsets and one-off jolts keep the same acceleration sign for several frames.
                var resid = new Vector3[win + 1];
                for (int start = 1; start + win + 1 < F - 1; start += Mathf.Max(1, win / 2))
                {
                    for (int i = 0; i <= win; i++)
                    {
                        int f = start + i;
                        resid[i] = dev[c][f] - 0.5f * (dev[c][f - 1] + dev[c][f + 1]);
                    }
                    int flips = 0;
                    double flipSq = 0;
                    for (int i = 0; i < win; i++)
                    {
                        if (Vector3.Dot(resid[i], resid[i + 1]) < 0f && resid[i].magnitude > 1e-4f && resid[i + 1].magnitude > 1e-4f)
                        {
                            flips++;
                            flipSq += resid[i].sqrMagnitude;
                        }
                    }
                    float flipFrac = flips / (float)win;
                    float amp = flips > 0 ? (float)Math.Sqrt(flipSq / flips) * 0.5f : 0f;
                    if (flipFrac > 0.45f && amp > s.jitAmp) { s.jitAmp = amp; s.jitRatio = flipFrac; s.jitT = (start + win / 2) * dt; }
                }

                int pf = Mathf.Clamp(Mathf.RoundToInt(s.peakT / dt), 0, F - 1);
                var chRec = d.meta.chains[c];
                Vector3 vec = d.Pt(pf, chRec.pts[chRec.pts.Length - 1]) - d.Pt(pf, chRec.pts[0]);
                s.downAngAtPeak = vec.sqrMagnitude > 1e-10f ? Vector3.Angle(vec, Vector3.down) : 90f;

                s.endMag = mag[c][F - 1];
                double sp = 0;
                for (int f = F - nEnd; f < F; f++) sp += (dev[c][f] - dev[c][f - 1]).sqrMagnitude;
                s.restSpeed = (float)Math.Sqrt(sp / nEnd) / dt;
                s.segs = segs.Select(sg => SegStat(c, sg)).ToArray();
                cs[c] = s;
            }
        }

        ChainSeg SegStat(int c, SegInfo sg)
        {
            var r = new ChainSeg();
            int f0 = sg.f0, f1 = Mathf.Min(sg.f1, F - 1);
            if (f1 - f0 < 4) return r;
            int peakF = f0;
            for (int f = f0; f <= f1; f++) if (mag[c][f] > r.peak) { r.peak = mag[c][f]; peakF = f; }
            r.peakT = peakF * dt;
            r.endMag = mag[c][f1];

            int n = f1 - f0 + 1;
            int nTail = Mathf.Max(3, n / 10);
            Vector3 eq = Vector3.zero;
            for (int f = f1 - nTail + 1; f <= f1; f++) eq += dev[c][f];
            eq /= nTail;
            Vector3 mean = Vector3.zero;
            for (int f = f0; f <= f1; f++) mean += dev[c][f];
            mean /= n;
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (int f = f0; f <= f1; f++)
            {
                Vector3 v = dev[c][f] - mean;
                xx += v.x * v.x; xy += v.x * v.y; xz += v.x * v.z; yy += v.y * v.y; yz += v.y * v.z; zz += v.z * v.z;
            }
            Vector3 u = dev[c][peakF] - mean;
            if (u.sqrMagnitude < 1e-12f) u = Vector3.forward;
            u.Normalize();
            for (int it = 0; it < 24; it++)
            {
                var w = new Vector3(xx * u.x + xy * u.y + xz * u.z, xy * u.x + yy * u.y + yz * u.z, xz * u.x + yz * u.y + zz * u.z);
                if (w.sqrMagnitude < 1e-16f) break;
                u = w.normalized;
            }
            r.axis = u;

            float h = Mathf.Max(0.001f, 0.08f * r.peak);
            var extF = new List<int>();
            var extV = new List<float>();
            int trend = 0;
            float exV = Vector3.Dot(dev[c][f0] - eq, u);
            int exF = f0;
            for (int f = f0 + 1; f <= f1; f++)
            {
                float v = Vector3.Dot(dev[c][f] - eq, u);
                if (trend == 0)
                {
                    if (v - exV > h) { trend = 1; exV = v; exF = f; }
                    else if (exV - v > h) { trend = -1; exV = v; exF = f; }
                }
                else if (trend == 1)
                {
                    if (v > exV) { exV = v; exF = f; }
                    else if (exV - v > h) { extF.Add(exF); extV.Add(exV); trend = -1; exV = v; exF = f; }
                }
                else
                {
                    if (v < exV) { exV = v; exF = f; }
                    else if (v - exV > h) { extF.Add(exF); extV.Add(exV); trend = 1; exV = v; exF = f; }
                }
            }
            // free response: only turning points from the peak on (before it the motion is still being driven)
            int firstAfterPeak = extF.FindIndex(f => f >= peakF - 2);
            if (firstAfterPeak > 0) { extF.RemoveRange(0, firstAfterPeak); extV.RemoveRange(0, firstAfterPeak); }
            else if (firstAfterPeak < 0) { extF.Clear(); extV.Clear(); }
            float swingThr = Mathf.Max(0.003f, 0.15f * r.peak);
            foreach (var v in extV) if (Mathf.Abs(v) >= swingThr) r.swings++;
            if (extF.Count >= 3)
            {
                float span = (extF[extF.Count - 1] - extF[0]) * dt;
                if (span > 1e-4f) r.freq = (extF.Count - 1) / (2f * span);
            }
            var ratios = new List<float>();
            for (int i = 0; i + 1 < extV.Count; i++)
                if (Mathf.Abs(extV[i]) >= 0.002f) ratios.Add(Mathf.Abs(extV[i + 1]) / Mathf.Abs(extV[i]));
            if (ratios.Count > 0) { ratios.Sort(); r.decay = ratios[ratios.Count / 2]; }

            float thr = Mathf.Max(0.003f, 0.1f * r.peak);
            int lastAbove = -1;
            for (int f = f0; f <= f1; f++) if (mag[c][f] > thr) lastAbove = f;
            if (lastAbove < 0) r.settle = 0f;
            else if (lastAbove <= f1 - 3) r.settle = (lastAbove + 1 - f0) * dt;
            else r.settle = -1f;
            return r;
        }

        void Groups()
        {
            var map = new Dictionary<string, GroupStat>();
            for (int i = 0; i < d.meta.comps.Count; i++)
            {
                var comp = d.meta.comps[i];
                if (!map.TryGetValue(comp.group, out GroupStat g)) { g = new GroupStat { name = comp.group }; map[comp.group] = g; }
                g.comps.Add(i);
                g.chains.AddRange(comp.chains);
            }
            foreach (var g in map.Values)
            {
                foreach (int c in g.chains)
                {
                    var s = cs[c];
                    if (g.best < 0 || s.peak > g.peak) { g.best = c; g.peak = s.peak; g.peakT = s.peakT; g.pctLen = s.pctLen; }
                    g.len = Mathf.Max(g.len, s.restLen);
                    g.peakAng = Mathf.Max(g.peakAng, s.peakAng);
                }
                if (g.chains.Count > 0) groups.Add(g);
            }
            groups.Sort((x, y) => y.peak.CompareTo(x.peak));

            // left/right pairs by name
            var byKey = new Dictionary<string, List<int>>();
            for (int i = 0; i < d.meta.comps.Count; i++)
            {
                var comp = d.meta.comps[i];
                if (string.IsNullOrEmpty(comp.pairKey)) continue;
                if (!byKey.TryGetValue(comp.pairKey, out var l)) byKey[comp.pairKey] = l = new List<int>();
                l.Add(i);
            }
            foreach (var l in byKey.Values)
            {
                int a = l.FirstOrDefault(i => d.meta.comps[i].side == "L" && d.meta.comps[i].chains.Length > 0);
                int b = l.FirstOrDefault(i => d.meta.comps[i].side == "R" && d.meta.comps[i].chains.Length > 0);
                bool hasA = l.Any(i => d.meta.comps[i].side == "L" && d.meta.comps[i].chains.Length > 0);
                bool hasB = l.Any(i => d.meta.comps[i].side == "R" && d.meta.comps[i].chains.Length > 0);
                if (!hasA || !hasB) continue;
                float pa = d.meta.comps[a].chains.Max(c => cs[c].peak), pb = d.meta.comps[b].chains.Max(c => cs[c].peak);
                pairs.Add((a, b, pa, pb));
            }
        }

        public Vector3 Rel(int f, int point) => Quaternion.Inverse(d.rootRot[f]) * (d.Pt(f, point) - d.rootPos[f]);

        void Body()
        {
            var eff = new List<int>();
            var names = new List<string>();
            foreach (var n in EffWanted)
            {
                var h = d.meta.humans.FirstOrDefault(x => x.name == n);
                if (h != null) { eff.Add(h.point); names.Add(n); }
            }
            effPt = eff.ToArray();
            effName = names.ToArray();
            effRel = new Vector3[effPt.Length][];
            for (int e = 0; e < effPt.Length; e++)
            {
                effRel[e] = new Vector3[F];
                for (int f = 0; f < F; f++) effRel[e][f] = Rel(f, effPt[e]);
            }

            rootSpeed = new float[F];
            float yawPrev = d.rootRot[0].eulerAngles.y, yawAcc = 0f;
            for (int f = 1; f < F; f++)
            {
                Vector3 dp = d.rootPos[f] - d.rootPos[f - 1];
                rootSpeed[f] = dp.magnitude / dt;
                maxSpeed = Mathf.Max(maxSpeed, rootSpeed[f]);
                travel += new Vector2(dp.x, dp.z).magnitude;
                float y = d.rootRot[f].eulerAngles.y;
                yawAcc += Mathf.DeltaAngle(yawPrev, y);
                yawPrev = y;
                yawMaxAbs = Mathf.Max(yawMaxAbs, Mathf.Abs(yawAcc));
            }
            yawTotal = yawAcc;
            for (int f = 0; f < F; f++) maxRise = Mathf.Max(maxRise, d.rootPos[f].y - d.meta.refRootY);

            foreach (var sg in segs)
            {
                var b = new BodySeg { seg = sg };
                int f0 = sg.f0, f1 = Mathf.Min(sg.f1, F - 1), n = f1 - f0 + 1;
                if (effPt.Length > 0 && n > 2)
                {
                    double acc = 0;
                    for (int e = 0; e < effPt.Length; e++)
                    {
                        Vector3 mn = Vector3.one * 1e9f, mx = -Vector3.one * 1e9f, mean = Vector3.zero;
                        for (int f = f0; f <= f1; f++) { var v = effRel[e][f]; mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); mean += v; }
                        mean /= n;
                        for (int f = f0; f <= f1; f++) acc += (effRel[e][f] - mean).sqrMagnitude;
                        for (int f = f0 + 1; f <= f1; f++) b.maxEffSpeed = Mathf.Max(b.maxEffSpeed, (effRel[e][f] - effRel[e][f - 1]).magnitude / dt);
                        b.range[effName[e]] = mx - mn;
                    }
                    b.motionRms = (float)Math.Sqrt(acc / (n * effPt.Length));
                    int skip = Mathf.Min(Mathf.RoundToInt(0.6f / dt), Mathf.RoundToInt((f1 - f0) * 0.3f));
                    int s0 = f0 + skip;
                    if (f1 - s0 >= Mathf.RoundToInt(0.6f / dt))
                    {
                        b.steadyT0 = s0 * dt;
                        int ns = f1 - s0 + 1;
                        double acc2 = 0;
                        for (int e = 0; e < effPt.Length; e++)
                        {
                            Vector3 mn = Vector3.one * 1e9f, mx = -Vector3.one * 1e9f, mean = Vector3.zero;
                            for (int f = s0; f <= f1; f++) { var v = effRel[e][f]; mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); mean += v; }
                            mean /= ns;
                            for (int f = s0; f <= f1; f++) acc2 += (effRel[e][f] - mean).sqrMagnitude;
                            b.steadyRange[effName[e]] = mx - mn;
                        }
                        b.steadyRms = (float)Math.Sqrt(acc2 / (ns * effPt.Length));
                        if (b.steadyRms >= 0.003f) b.period = Periodicity(s0, f1, out b.periodR);
                    }
                }
                body.Add(b);
            }

            int nEnd = Mathf.Clamp(Mathf.RoundToInt(0.5f / dt), 2, F - 1);
            staticAtEnd = true;
            for (int f = F - nEnd; f < F; f++)
            {
                if (rootSpeed[f] > 0.02f) staticAtEnd = false;
                for (int e = 0; e < effPt.Length; e++)
                    if ((effRel[e][f] - effRel[e][f - 1]).magnitude / dt > 0.06f) staticAtEnd = false;
            }
        }

        float Periodicity(int f0, int f1, out float rBest)
        {
            // Autocorrelation of limb velocities (relative to the root): velocities ignore slow drifts and
            // emphasise the repeating part of a loop animation.
            rBest = 0f;
            int n = f1 - f0, E = effPt.Length, D = 3 * E;
            if (E == 0 || n < 20) return -1f;
            var X = new float[n, D];
            for (int j = 0; j < E; j++)
                for (int i = 0; i < n; i++)
                {
                    Vector3 v = effRel[j][f0 + i + 1] - effRel[j][f0 + i];
                    X[i, 3 * j] = v.x; X[i, 3 * j + 1] = v.y; X[i, 3 * j + 2] = v.z;
                }
            int lagMin = Mathf.Max(3, Mathf.RoundToInt(0.15f / dt));
            int lagMax = Mathf.Min(Mathf.RoundToInt(4f / dt), n / 2);
            if (lagMax <= lagMin + 2) return -1f;
            var rr = new float[lagMax + 2];
            for (int L = lagMin - 1; L <= lagMax + 1; L++)
            {
                double num = 0, da = 0, db = 0;
                for (int i = 0; i + L < n; i++)
                    for (int j = 0; j < D; j++) { float a = X[i, j], b = X[i + L, j]; num += a * b; da += a * a; db += b * b; }
                rr[L] = (float)(num / Math.Sqrt(da * db + 1e-12));
            }
            for (int L = lagMin; L <= lagMax; L++)
            {
                if (rr[L] >= 0.7f && rr[L] >= rr[L - 1] && rr[L] >= rr[L + 1])
                {
                    rBest = rr[L];
                    float den = rr[L - 1] - 2f * rr[L] + rr[L + 1];
                    float off = Mathf.Abs(den) > 1e-6f ? Mathf.Clamp(0.5f * (rr[L - 1] - rr[L + 1]) / den, -0.5f, 0.5f) : 0f;
                    return (L + off) * dt;
                }
            }
            return -1f;
        }

        void Flags()
        {
            var jit = new List<(float sev, string s)>();
            var pen = new List<(float sev, string s)>();
            var intr = new List<(float sev, string s)>();
            var str = new List<(float sev, string s)>();
            var unsettled = new List<(float sev, string s)>();
            var restless = new List<(float sev, string s)>();
            var large = new List<(float sev, string s)>();
            var bouncy = new List<(float sev, string s)>();
            var stiff = new List<(float sev, string s)>();
            bool rootMoved = travel > 0.05f || yawMaxAbs > 10f || maxRise > 0.03f || d.meta.events.Any(e => e.label != null && (e.label.StartsWith("SHAKE") || e.label.StartsWith("SWAY") || e.label.StartsWith("BOB")));
            var last = segs.Count > 0 ? segs[segs.Count - 1] : null;

            for (int c = 0; c < cs.Length; c++)
            {
                var s = cs[c];
                var ch = d.meta.chains[c];
                var comp = d.meta.comps[ch.comp];
                string n = ch.name;
                if (s.jitAmp > 0.0005f)
                    jit.Add((s.jitAmp, n + " zig-zags about +-" + Mm(s.jitAmp) + " mm, reversing on " + ProbeIO.F(s.jitRatio * 100f, 0) + "% of frames, near t=" + ProbeIO.F(s.jitT) + " (" + Ctx(s.jitT) + ")"));
                if (s.clrA < -0.005f)
                    pen.Add((-s.clrA, n + " " + ProbeIO.Cm(-s.clrA) + " cm inside an assigned collider at t=" + ProbeIO.F(s.clrAT) + " (" + Ctx(s.clrAT) + ")"));
                if (s.clrAll < -0.01f && s.clrAllIdx >= 0 && s.clrAllIdx < d.meta.colliders.Count)
                {
                    string colName = d.meta.colliders[s.clrAllIdx].name;
                    bool assigned = comp.colliders != null && comp.colliders.Contains(colName);
                    if (!assigned)
                        intr.Add((-s.clrAll, n + " enters collider '" + colName + "' (NOT assigned to this PhysBone) by " + ProbeIO.Cm(-s.clrAll) + " cm at t=" + ProbeIO.F(s.clrAllT) + " (" + Ctx(s.clrAllT) + ")"));
                }
                if (s.strMax > 1.15f) str.Add((s.strMax, n + " stretched to " + Pct(s.strMax) + " of rest length at t=" + ProbeIO.F(s.strMaxT)));
                if (s.strMin < 0.85f) str.Add((1f / s.strMin, n + " squished to " + Pct(s.strMin) + " of rest length at t=" + ProbeIO.F(s.strMinT)));
                if (last != null && staticAtEnd)
                {
                    var ls = s.segs[s.segs.Length - 1];
                    if (ls.peak > 0.005f && s.endMag > Mathf.Max(0.005f, 0.25f * ls.peak))
                        unsettled.Add((s.endMag, n + " still " + ProbeIO.Cm(s.endMag) + " cm off rest at the end (peak " + ProbeIO.Cm(ls.peak) + " cm after " + last.label + ")"));
                    if (s.restSpeed > 0.01f)
                        restless.Add((s.restSpeed, n + " keeps moving at " + ProbeIO.Cm(s.restSpeed) + " cm/s while the body is still (last 0.5 s)"));
                    if (ls.swings >= 6) bouncy.Add((ls.swings, n + " " + ls.swings + " visible swings after " + last.label + " (decay/half-swing " + ProbeIO.F(ls.decay) + ")"));
                }
                if (s.pctLen > 60f || s.peakAng > 70f)
                    large.Add((s.peakAng, n + " swings " + ProbeIO.F(s.peakAng, 0) + " deg / " + ProbeIO.F(s.pctLen, 0) + "% of its length at t=" + ProbeIO.F(s.peakAngT) + " (" + Ctx(s.peakAngT) + ")" + HangNote(c)));
                if (rootMoved && s.restLen > 0.08f && s.peak < 0.003f)
                    stiff.Add((s.restLen, n + " (" + ProbeIO.Cm(s.restLen) + " cm long) moves less than 0.3 cm in the whole run"));
            }
            var asym = new List<(float sev, string s)>();
            foreach (var p in pairs)
            {
                float mx = Mathf.Max(p.pa, p.pb);
                if (mx > 0.01f && Mathf.Abs(p.pa - p.pb) / mx > 0.4f)
                    asym.Add((Mathf.Abs(p.pa - p.pb), d.meta.comps[p.a].name + " vs " + d.meta.comps[p.b].name + ": peak " + ProbeIO.Cm(p.pa) + " vs " + ProbeIO.Cm(p.pb) + " cm"));
            }
            Add(warnings, "JITTER", jit, "high-frequency shaking is almost always unnatural");
            Add(warnings, "ASYMMETRY", asym, "left/right counterparts move differently; fine if the outfit or motion is asymmetric");
            Add(warnings, "PENETRATION", pen, "chain gets inside its own collider");
            Add(warnings, "INTRUSION", intr, "chain passes into a body collider it does not collide with (likely clipping into the body)");
            Add(warnings, "STRETCH", str, "length changes; fine only if maxStretch/maxSquish are intended");
            Add(warnings, "NOT_SETTLED", unsettled, "still displaced although the body stopped");
            Add(warnings, "RESTLESS", restless, "never comes to rest while the body is still");
            Add(infos, "LARGE_SWING", large, "big swings; check visually that it does not look like flailing");
            Add(infos, "BOUNCY", bouncy, "many oscillations: under-damped (spring/momentum high, pull low?)");
            Add(infos, "BARELY_MOVES", stiff, "very stiff or immobile; fine for accessories that should not swing");
        }

        /// <summary>Explains big "displacements" that are just a gravity-driven chain hanging straight down while its parent rotates.</summary>
        public string HangNote(int c)
        {
            var comp = d.meta.comps[d.meta.chains[c].comp];
            if (comp.gravity > 0.3f && cs[c].downAngAtPeak < 30f)
                return " [hangs under gravity: points " + ProbeIO.F(cs[c].downAngAtPeak, 0) + " deg from straight down, so the parent bone rotated, not the chain]";
            return "";
        }

        static void Add(List<string> dst, string tag, List<(float sev, string s)> items, string meaning)
        {
            if (items.Count == 0) return;
            items.Sort((x, y) => y.sev.CompareTo(x.sev));
            var sb = new StringBuilder();
            sb.Append(tag).Append(" x").Append(items.Count).Append(" (").Append(meaning).Append("): ");
            sb.Append(string.Join("; ", items.Take(6).Select(i => i.s)));
            if (items.Count > 6) sb.Append("; ... +").Append(items.Count - 6).Append(" more");
            dst.Add(sb.ToString());
        }

        static string Mm(float m) => ProbeIO.F(m * 1000f, 1);
        static string Pct(float r) => ProbeIO.F(r * 100f, 0) + "%";

        void KeyTimes()
        {
            int cols = 6;
            try { var o = JsonUtility.FromJson<MotionProbeOptions>(d.meta.optionsJson); if (o != null && o.sheetColumns >= 2) cols = o.sheetColumns; } catch { }
            var chosen = new List<(float t, string label)> { (0f, "START") };
            var cands = new List<(float score, float t, string label)>();
            foreach (var sg in segs)
            {
                int best = -1;
                float bv = 0f;
                for (int f = sg.f0 + 1; f <= Mathf.Min(sg.f1, F - 1); f++) if (meanMag[f] > bv) { bv = meanMag[f]; best = f; }
                if (best > 0 && bv > 0.002f) cands.Add((bv, best * dt, Ctx(best * dt)));
                else if (sg.label != "START" && sg.f0 + 1 < F) cands.Add((0f, Mathf.Min(T, sg.t0 + 0.2f), Ctx(Mathf.Min(T, sg.t0 + 0.2f))));
            }
            foreach (var c in cands.OrderByDescending(c => c.score))
            {
                if (chosen.Count >= cols - 1) break;
                if (chosen.Any(x => Mathf.Abs(x.t - c.t) < 0.12f)) continue;
                chosen.Add((c.t, c.label));
            }
            if (!chosen.Any(x => Mathf.Abs(x.t - T) < 0.12f)) chosen.Add((T, "END"));
            for (int i = 1; chosen.Count < cols && i < 64; i++)
            {
                float t = T * ((i * 0.618034f) % 1f);
                if (chosen.Any(x => Mathf.Abs(x.t - t) < Mathf.Max(0.12f, T / (cols * 2f)))) continue;
                chosen.Add((t, Ctx(t)));
            }
            foreach (var c in chosen.OrderBy(x => x.t)) { keyTimes.Add(c.t); keyLabels.Add(c.label); }
        }

        void Headline()
        {
            foreach (var g in groups.Take(3))
            {
                if (g.peak < 0.002f) break;
                headline.Add("Most motion: " + g.name + " (" + g.chains.Count + " chains) peak " + ProbeIO.Cm(g.peak) + " cm = " + ProbeIO.F(g.pctLen, 0) + "% of its length, up to " + ProbeIO.F(g.peakAng, 0) + " deg, at t=" + ProbeIO.F(g.peakT) + " (" + Ctx(g.peakT) + ")" + HangNote(g.best));
            }
            if (cs.Length == 0) headline.Add("No PhysBones found on the avatar.");
            headline.Add(warnings.Count == 0 ? "No warnings." : "Warnings: " + string.Join(", ", warnings.Select(w => { int i = w.IndexOf(" ("); return i > 0 ? w.Substring(0, i) : w; })));
            var bodyBits = new List<string>();
            if (travel > 0.05f) bodyBits.Add("root traveled " + ProbeIO.F(travel) + " m (max " + ProbeIO.F(maxSpeed) + " m/s)");
            if (yawMaxAbs > 10f) bodyBits.Add("turned up to " + ProbeIO.F(yawMaxAbs, 0) + " deg");
            if (maxRise > 0.03f) bodyBits.Add("rose up to " + ProbeIO.Cm(maxRise, 0) + " cm");
            foreach (var b in body)
                if (b.period > 0f) bodyBits.Add("loop motion with period " + ProbeIO.F(b.period, 3) + " s during " + b.seg.label + " (steady part " + ProbeIO.F(b.steadyT0) + "-" + ProbeIO.F(b.seg.t1) + " s, r=" + ProbeIO.F(b.periodR) + ")");
            if (bodyBits.Count > 0) headline.Add("Body: " + string.Join("; ", bodyBits));
        }

        public static string AxisWords(Vector3 u)
        {
            float x = u.x * u.x, y = u.y * u.y, z = u.z * u.z;
            var parts = new List<(float w, string n)> { (z, "fwd/back"), (x, "side"), (y, "up/down") };
            parts.Sort((a, b) => b.w.CompareTo(a.w));
            string s = parts[0].n;
            if (parts[1].w > 0.3f) s += "+" + parts[1].n;
            return s;
        }
    }

    internal class ProbeOutputResult
    {
        public string headline = "";
        public readonly List<string> files = new List<string>();
    }

    internal static class ProbeOutputs
    {
        public static ProbeOutputResult GenerateAll(ProbeData d)
        {
            var res = new ProbeOutputResult();
            var errors = new List<string>();
            var a = ProbeAnalysis.Run(d);
            var opt = MotionProbeOptions.Parse(d.meta.optionsJson, out _) ?? new MotionProbeOptions();
            float T = d.meta.duration;
            string title = d.meta.avatar + "  " + Path.GetFileName(d.dir);

            void Try(string file, Func<string> make)
            {
                try { make(); res.files.Add(file); }
                catch (Exception e) { errors.Add(file + ": " + e.Message); Debug.LogWarning("[MotionProbe] " + file + ": " + e); }
            }

            if (d.meta.views.Any(v => v.group == "body"))
                Try("sheet_body.png", () => ProbeImaging.Sheet(d, "body", a.keyTimes, a.keyLabels, null, d.dir + "/sheet_body.png", title + "  BODY"));
            if (d.meta.views.Any(v => v.group == "head"))
                Try("sheet_head.png", () => ProbeImaging.Sheet(d, "head", a.keyTimes, a.keyLabels, null, d.dir + "/sheet_head.png", title + "  HEAD"));
            if (d.meta.chains.Count > 0)
                Try("trail_physbone.png", () => ProbeImaging.Trail(d, a, 0f, T, "physbone", "", d.dir + "/trail_physbone.png"));
            if (a.effPt.Length > 0 && d.meta.views.Any(v => v.group == "body"))
                Try("trail_body.png", () => ProbeImaging.Trail(d, a, 0f, T, "body", "", d.dir + "/trail_body.png"));
            Try("chart.png", () => ProbeImaging.Chart(d, a, 0f, T, "", d.dir + "/chart.png"));
            var periodic = a.body.FirstOrDefault(b => b.period > 0f);
            if (periodic != null && d.meta.views.Any(v => v.group == "body"))
            {
                float p = periodic.period, start = periodic.steadyT0;
                var times = new List<float>();
                var labels = new List<string>();
                for (int i = 0; i < 8; i++) { times.Add(start + p * i / 8f); labels.Add("PHASE " + i + "/8"); }
                Try("sheet_cycle.png", () => ProbeImaging.Sheet(d, "body", times, labels, "front,side", d.dir + "/sheet_cycle.png", title + "  ONE LOOP, PERIOD " + ProbeIO.F(p, 3) + " S"));
                Try("trail_cycle.png", () => ProbeImaging.Trail(d, a, 0f, T, "cycle", "", d.dir + "/trail_cycle.png"));
            }
            if (opt.csv)
            {
                try { ProbeReport.WriteCsv(d, a); res.files.Add("data/chains.csv"); res.files.Add("data/timeseries_cm.csv"); res.files.Add("data/body.csv"); }
                catch (Exception e) { errors.Add("csv: " + e.Message); }
            }
            File.WriteAllText(d.dir + "/report.md", ProbeReport.Write(d, a, res.files, errors));
            res.files.Insert(0, "report.md");
            res.headline = string.Join(" | ", a.headline);
            return res;
        }
    }
}
