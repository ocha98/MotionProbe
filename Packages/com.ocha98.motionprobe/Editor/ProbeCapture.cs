using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using VRC.Dynamics;

namespace MotionProbeTool
{
    internal class ProbePoint
    {
        public string name;
        public Transform t;
        public Vector3 offset;
        public bool isVirtual;
        public int frame;
        public int kind;
        public int comp = -1;
        public int depth;
    }

    internal class ProbeChain
    {
        public string name;
        public int comp;
        public int frame;
        public int[] pts;
        public float[] radius;
        // clearance samples: lerp(pts[sa], pts[sb], sw) with bone radius sr
        public int[] sa, sb;
        public float[] sw, sr;
        public bool[] assigned;
        public float[,] baseline;
    }

    internal class ProbeComp
    {
        public VRCPhysBoneBase pb;
        public string name, group, side, pairKey, path, parentName, settings;
        public Transform chainRoot;
        public int frame;
        public bool underHead;
        public readonly List<int> chains = new List<int>();
        public readonly List<int> colliders = new List<int>();
    }

    /// <summary>What gets tracked: humanoid bones, every PhysBone chain (root to each leaf), colliders.</summary>
    internal class ProbeTargets
    {
        public GameObject avatar;
        public Transform root;
        public Animator animator;
        public Transform head;
        public readonly List<Transform> frames = new List<Transform>();
        readonly Dictionary<Transform, int> frameIdx = new Dictionary<Transform, int>();
        public readonly List<ProbePoint> points = new List<ProbePoint>();
        public readonly List<ProbeChain> chains = new List<ProbeChain>();
        public readonly List<ProbeComp> comps = new List<ProbeComp>();
        public readonly List<VRCPhysBoneColliderBase> colliders = new List<VRCPhysBoneColliderBase>();
        readonly Dictionary<VRCPhysBoneColliderBase, int> colliderIdx = new Dictionary<VRCPhysBoneColliderBase, int>();
        public readonly List<ProbeHumanRec> humans = new List<ProbeHumanRec>();

        static readonly (string name, HumanBodyBones bone)[] HumanBones =
        {
            ("Hips", HumanBodyBones.Hips), ("Spine", HumanBodyBones.Spine), ("Chest", HumanBodyBones.Chest),
            ("UpperChest", HumanBodyBones.UpperChest), ("Neck", HumanBodyBones.Neck), ("Head", HumanBodyBones.Head),
            ("LeftShoulder", HumanBodyBones.LeftShoulder), ("LeftUpperArm", HumanBodyBones.LeftUpperArm),
            ("LeftLowerArm", HumanBodyBones.LeftLowerArm), ("LeftHand", HumanBodyBones.LeftHand),
            ("RightShoulder", HumanBodyBones.RightShoulder), ("RightUpperArm", HumanBodyBones.RightUpperArm),
            ("RightLowerArm", HumanBodyBones.RightLowerArm), ("RightHand", HumanBodyBones.RightHand),
            ("LeftUpperLeg", HumanBodyBones.LeftUpperLeg), ("LeftLowerLeg", HumanBodyBones.LeftLowerLeg),
            ("LeftFoot", HumanBodyBones.LeftFoot), ("LeftToes", HumanBodyBones.LeftToes),
            ("RightUpperLeg", HumanBodyBones.RightUpperLeg), ("RightLowerLeg", HumanBodyBones.RightLowerLeg),
            ("RightFoot", HumanBodyBones.RightFoot), ("RightToes", HumanBodyBones.RightToes),
        };

        public int FrameIndex(Transform t)
        {
            if (!frameIdx.TryGetValue(t, out int i)) { i = frames.Count; frames.Add(t); frameIdx[t] = i; }
            return i;
        }

        int AddPoint(ProbePoint p) { points.Add(p); return points.Count - 1; }

        int AddCollider(VRCPhysBoneColliderBase c)
        {
            if (!colliderIdx.TryGetValue(c, out int i)) { i = colliders.Count; colliders.Add(c); colliderIdx[c] = i; }
            return i;
        }

        public static ProbeTargets Discover(GameObject avatar, Animator animator, List<string> notes)
        {
            var T = new ProbeTargets { avatar = avatar, root = avatar.transform, animator = animator };
            T.FrameIndex(T.root);

            if (animator != null && animator.isHuman)
            {
                foreach (var hb in HumanBones)
                {
                    var t = animator.GetBoneTransform(hb.bone);
                    if (t == null) continue;
                    int pi = T.AddPoint(new ProbePoint { name = hb.name, t = t, frame = 0, kind = 0 });
                    T.humans.Add(new ProbeHumanRec { name = hb.name, point = pi });
                }
                T.head = animator.GetBoneTransform(HumanBodyBones.Head);
            }
            else notes.Add("Avatar Animator is not humanoid: body motion analysis is limited.");

            foreach (var c in avatar.GetComponentsInChildren<VRCPhysBoneColliderBase>(false))
                if (c != null && c.enabled) T.AddCollider(c);

            var usedNames = new HashSet<string>();
            foreach (var pb in avatar.GetComponentsInChildren<VRCPhysBoneBase>(false))
            {
                if (pb == null || !pb.enabled) continue;
                Transform chainRoot = pb.rootTransform != null ? pb.rootTransform : pb.transform;
                var ignore = new HashSet<Transform>();
                if (pb.ignoreTransforms != null) foreach (var it in pb.ignoreTransforms) if (it != null) ignore.Add(it);
                var paths = new List<List<Transform>>();
                CollectPaths(chainRoot, new List<Transform>(), ignore, paths);
                bool hasEnd = pb.endpointPosition != Vector3.zero;
                if (!hasEnd && paths.All(p => p.Count < 2))
                {
                    notes.Add("PhysBone '" + pb.name + "' has no child bones and no endpoint: skipped.");
                    continue;
                }

                string baseName = pb.gameObject.name, uniq = baseName;
                for (int n = 2; !usedNames.Add(uniq); n++) uniq = baseName + "#" + n;
                var comp = new ProbeComp
                {
                    pb = pb, name = uniq, chainRoot = chainRoot,
                    group = ProbeNaming.Group(baseName), side = ProbeNaming.Side(baseName), pairKey = ProbeNaming.PairKey(baseName),
                    path = PathOf(pb.transform, T.root),
                    parentName = chainRoot.parent != null ? chainRoot.parent.name : "-",
                    frame = T.FrameIndex(chainRoot.parent != null ? chainRoot.parent : T.root),
                    underHead = T.head != null && chainRoot.IsChildOf(T.head)
                };
                if (pb.colliders != null) foreach (var c in pb.colliders) if (c != null) comp.colliders.Add(T.AddCollider(c));
                comp.settings = ProbeNaming.Settings(pb, comp.colliders.Count, chainRoot.childCount);
                int compIndex = T.comps.Count;
                T.comps.Add(comp);

                float scale = MaxAbs(chainRoot.lossyScale);
                int maxDepth = 1;
                foreach (var p in paths) maxDepth = Mathf.Max(maxDepth, p.Count - 1 + (hasEnd ? 1 : 0));
                var ptIdx = new Dictionary<Transform, int>();
                var endIdx = new Dictionary<Transform, int>();
                foreach (var path in paths)
                {
                    var idx = new List<int>();
                    var depths = new List<int>();
                    for (int dpt = 0; dpt < path.Count; dpt++)
                    {
                        var t = path[dpt];
                        if (!ptIdx.TryGetValue(t, out int pi))
                        {
                            pi = T.AddPoint(new ProbePoint { name = t.name, t = t, frame = comp.frame, kind = 1, comp = compIndex, depth = dpt });
                            ptIdx[t] = pi;
                        }
                        idx.Add(pi); depths.Add(dpt);
                    }
                    if (hasEnd)
                    {
                        var leaf = path[path.Count - 1];
                        if (!endIdx.TryGetValue(leaf, out int ei))
                        {
                            ei = T.AddPoint(new ProbePoint { name = leaf.name + "/end", t = leaf, offset = pb.endpointPosition, isVirtual = true, frame = comp.frame, kind = 1, comp = compIndex, depth = path.Count });
                            endIdx[leaf] = ei;
                        }
                        idx.Add(ei); depths.Add(path.Count);
                    }
                    if (idx.Count < 2) continue;

                    var ch = new ProbeChain { comp = compIndex, frame = comp.frame, pts = idx.ToArray() };
                    ch.name = paths.Count == 1 ? uniq : uniq + "/" + path[path.Count - 1].name;
                    ch.radius = new float[idx.Count];
                    for (int i = 0; i < idx.Count; i++) ch.radius[i] = pb.radius * EvalCurve(pb.radiusCurve, depths[i] / (float)maxDepth) * scale;
                    var sa = new List<int>(); var sb = new List<int>(); var sw = new List<float>(); var sr = new List<float>();
                    for (int i = 0; i + 1 < idx.Count; i++)
                    {
                        sa.Add(i); sb.Add(i + 1); sw.Add(0.5f); sr.Add((ch.radius[i] + ch.radius[i + 1]) * 0.5f);
                        sa.Add(i + 1); sb.Add(i + 1); sw.Add(0f); sr.Add(ch.radius[i + 1]);
                    }
                    ch.sa = sa.ToArray(); ch.sb = sb.ToArray(); ch.sw = sw.ToArray(); ch.sr = sr.ToArray();
                    comp.chains.Add(T.chains.Count);
                    T.chains.Add(ch);
                }
            }

            foreach (var ch in T.chains)
            {
                ch.assigned = new bool[T.colliders.Count];
                foreach (int ci in T.comps[ch.comp].colliders) ch.assigned[ci] = true;
                ch.baseline = new float[ch.sa.Length, Math.Max(1, T.colliders.Count)];
            }
            return T;
        }

        static void CollectPaths(Transform t, List<Transform> prefix, HashSet<Transform> ignore, List<List<Transform>> paths)
        {
            var path = new List<Transform>(prefix) { t };
            bool any = false;
            if (path.Count < 64)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (ignore.Contains(c)) continue;
                    any = true;
                    CollectPaths(c, path, ignore, paths);
                }
            }
            if (!any) paths.Add(path);
        }

        static float EvalCurve(AnimationCurve c, float x) => c != null && c.length > 0 ? c.Evaluate(x) : 1f;

        public static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        public static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }

    internal static class ProbeNaming
    {
        static readonly Regex SideRx = new Regex(@"(?<pre>^|[._\-\s])(?<side>L|R|Left|Right|left|right|LEFT|RIGHT)(?=$|[._\-\s\d])");
        static readonly Regex TrailRx = new Regex(@"[._\-\s]*\d+$");

        public static string Side(string name)
        {
            Match last = null;
            foreach (Match m in SideRx.Matches(name)) last = m;
            if (last == null) return "";
            return char.ToUpperInvariant(last.Groups["side"].Value[0]) == 'L' ? "L" : "R";
        }

        public static string PairKey(string name)
        {
            Match last = null;
            foreach (Match m in SideRx.Matches(name)) last = m;
            if (last == null) return "";
            return name.Substring(0, last.Index) + last.Groups["pre"].Value + "*" + name.Substring(last.Index + last.Length);
        }

        public static string Group(string name)
        {
            string s = SideRx.Replace(name, "${pre}");
            for (int i = 0; i < 3; i++) s = TrailRx.Replace(s.TrimEnd('_', '.', '-', ' '), "");
            s = Regex.Replace(s, @"[._\-\s]{2,}", "_").Trim('_', '.', '-', ' ');
            return s.Length == 0 ? name : s;
        }

        public static string Settings(VRCPhysBoneBase pb, int colliderCount, int rootChildCount)
        {
            string Cv(AnimationCurve c) => c != null && c.length > 0 ? "~" : "";
            string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            bool adv = pb.integrationType == VRCPhysBoneBase.IntegrationType.Advanced;
            var sb = new StringBuilder();
            sb.Append(pb.version == VRCPhysBoneBase.Version.Version_1_1 ? "v1.1 " : "v1.0 ");
            sb.Append(adv ? "Advanced" : "Simplified");
            sb.Append(" pull=").Append(F(pb.pull)).Append(Cv(pb.pullCurve));
            sb.Append(adv ? " momentum=" : " spring=").Append(F(pb.spring)).Append(Cv(pb.springCurve));
            if (adv) sb.Append(" stiffness=").Append(F(pb.stiffness)).Append(Cv(pb.stiffnessCurve));
            sb.Append(" gravity=").Append(F(pb.gravity)).Append(Cv(pb.gravityCurve));
            sb.Append(" gravFalloff=").Append(F(pb.gravityFalloff)).Append(Cv(pb.gravityFalloffCurve));
            sb.Append(" immobile=").Append(F(pb.immobile)).Append(Cv(pb.immobileCurve)).Append('(').Append(pb.immobileType).Append(')');
            sb.Append(" limit=").Append(pb.limitType);
            if (pb.limitType != VRCPhysBoneBase.LimitType.None)
            {
                sb.Append(' ').Append(F(pb.maxAngleX)).Append(Cv(pb.maxAngleXCurve));
                if (pb.limitType == VRCPhysBoneBase.LimitType.Polar) sb.Append('/').Append(F(pb.maxAngleZ));
            }
            sb.Append(" radius=").Append(F(pb.radius)).Append(Cv(pb.radiusCurve));
            sb.Append(" colliders=").Append(colliderCount);
            if (pb.maxStretch > 0f) sb.Append(" maxStretch=").Append(F(pb.maxStretch));
            if (pb.maxSquish > 0f) sb.Append(" maxSquish=").Append(F(pb.maxSquish));
            if (pb.endpointPosition != Vector3.zero) sb.Append(" endpoint=").Append(pb.endpointPosition.ToString("F2"));
            if (rootChildCount > 1) sb.Append(" multiChild=").Append(pb.multiChildType);
            if (pb.isAnimated) sb.Append(" isAnimated");
            return sb.ToString();
        }
    }

    internal struct ColShape
    {
        public bool valid;
        public int type; // 0 sphere, 1 capsule, 2 plane
        public Vector3 a, b, n;
        public float r;
        public bool inside;

        public static ColShape From(VRCPhysBoneColliderBase c)
        {
            var s = new ColShape();
            if (c == null || !c.isActiveAndEnabled) return s;
            Transform t = c.rootTransform != null ? c.rootTransform : c.transform;
            float sc = ProbeTargets.MaxAbs(t.lossyScale);
            Vector3 center = t.TransformPoint(c.position);
            Quaternion rot = t.rotation * c.rotation;
            s.valid = true;
            s.inside = c.insideBounds;
            switch (c.shapeType)
            {
                case VRCPhysBoneColliderBase.ShapeType.Sphere:
                    s.type = 0; s.a = s.b = center; s.r = c.radius * sc;
                    break;
                case VRCPhysBoneColliderBase.ShapeType.Capsule:
                    s.type = 1; s.r = c.radius * sc;
                    float half = Mathf.Max(0f, c.height * sc * 0.5f - s.r);
                    Vector3 axis = rot * Vector3.up;
                    s.a = center - axis * half; s.b = center + axis * half;
                    break;
                default:
                    s.type = 2; s.a = center; s.n = rot * Vector3.up;
                    break;
            }
            return s;
        }

        public float Clearance(Vector3 p, float rb)
        {
            if (type == 2) return Vector3.Dot(p - a, n) - rb;
            Vector3 q = a;
            if (type == 1)
            {
                Vector3 ab = b - a;
                float l2 = ab.sqrMagnitude;
                float t = l2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / l2) : 0f;
                q = a + ab * t;
            }
            float d = Vector3.Distance(p, q);
            return inside ? r - d - rb : d - r - rb;
        }
    }

    /// <summary>Per-frame transform recording into preallocated arrays.</summary>
    internal class ProbeRecorder
    {
        readonly ProbeTargets T;
        readonly ProbeData d;
        readonly ColShape[] shapes;

        public ProbeRecorder(ProbeTargets targets, int frameCount)
        {
            T = targets;
            d = new ProbeData { F = frameCount, P = T.points.Count, R = T.frames.Count, C = T.chains.Count };
            d.Allocate();
            shapes = new ColShape[T.colliders.Count];
        }

        public void Record(int k)
        {
            if (k < 0 || k >= d.F) return;
            int P = d.P, R = d.R, C = d.C, K = shapes.Length;
            d.rootPos[k] = T.root.position;
            d.rootRot[k] = T.root.rotation;
            for (int r = 0; r < R; r++)
            {
                var t = T.frames[r];
                int i = k * R + r;
                if (t != null) { d.fpos[i] = t.position; d.frot[i] = t.rotation; d.fscl[i] = t.lossyScale; }
                else if (k > 0) { d.fpos[i] = d.fpos[i - R]; d.frot[i] = d.frot[i - R]; d.fscl[i] = d.fscl[i - R]; }
                else { d.frot[i] = Quaternion.identity; d.fscl[i] = Vector3.one; }
            }
            for (int p = 0; p < P; p++)
            {
                var pt = T.points[p];
                Vector3 w;
                if (pt.t != null) w = pt.isVirtual ? pt.t.TransformPoint(pt.offset) : pt.t.position;
                else w = k > 0 ? d.pts[(k - 1) * P + p] : Vector3.zero;
                d.pts[k * P + p] = w;
            }
            for (int c = 0; c < K; c++) shapes[c] = ColShape.From(T.colliders[c]);
            for (int ci = 0; ci < C; ci++)
            {
                var ch = T.chains[ci];
                float minA = 9f, minAll = 9f;
                int idxAll = -1;
                for (int s = 0; s < ch.sa.Length; s++)
                {
                    Vector3 p = Vector3.Lerp(d.pts[k * P + ch.pts[ch.sa[s]]], d.pts[k * P + ch.pts[ch.sb[s]]], ch.sw[s]);
                    float rb = ch.sr[s];
                    for (int c = 0; c < K; c++)
                    {
                        if (!shapes[c].valid) continue;
                        float clr = shapes[c].Clearance(p, rb);
                        if (k == 0) ch.baseline[s, c] = clr;
                        if (ch.baseline[s, c] < -0.002f) continue; // started inside this collider: by design, ignore
                        if (clr < minAll) { minAll = clr; idxAll = c; }
                        if (ch.assigned[c] && clr < minA) minA = clr;
                    }
                }
                int o = k * C + ci;
                d.clrA[o] = minA; d.clrAll[o] = minAll; d.clrIdx[o] = idxAll;
            }
        }

        public ProbeData Build(ProbeMeta meta, string dir)
        {
            d.meta = meta;
            d.dir = dir;
            meta.frameCount = d.F;
            meta.frames = T.frames.Select(t => t != null ? t.name : "?").ToList();
            meta.humans = T.humans.ToList();
            meta.colliders = T.colliders.Select(c => new ProbeColliderRec
            {
                name = c != null ? c.gameObject.name : "?",
                shape = c != null ? c.shapeType.ToString() : "?"
            }).ToList();
            meta.points = new List<ProbePointRec>();
            for (int p = 0; p < T.points.Count; p++)
            {
                var pt = T.points[p];
                Vector3 local = d.FrameM(0, pt.frame).inverse.MultiplyPoint3x4(d.Pt(0, p));
                meta.points.Add(new ProbePointRec { name = pt.name, kind = pt.kind, comp = pt.comp, frame = pt.frame, depth = pt.depth, isVirtual = pt.isVirtual, refLocal = local });
            }
            meta.chains = new List<ProbeChainRec>();
            foreach (var ch in T.chains)
            {
                float L = 0f;
                for (int i = 0; i + 1 < ch.pts.Length; i++) L += Vector3.Distance(d.Pt(0, ch.pts[i]), d.Pt(0, ch.pts[i + 1]));
                meta.chains.Add(new ProbeChainRec { name = ch.name, comp = ch.comp, frame = ch.frame, pts = ch.pts, restLength = L });
            }
            meta.comps = T.comps.Select(c => new ProbeCompRec
            {
                name = c.name, group = c.group, side = c.side, pairKey = c.pairKey, path = c.path, parentName = c.parentName,
                settings = c.settings, gravity = c.pb != null ? c.pb.gravity : 0f, underHead = c.underHead, chains = c.chains.ToArray(),
                colliders = c.colliders.Select(ci => T.colliders[ci] != null ? T.colliders[ci].gameObject.name : "?").ToArray()
            }).ToList();
            return d;
        }
    }

    internal class ProbeViewRt
    {
        public ProbeViewRec rec;
        public Camera cam;
        public RenderTexture rt;
        public Texture2D tex;
        public string dir;
    }

    /// <summary>Orthographic cameras rigidly attached to the avatar root's yaw and horizontal position.</summary>
    internal class ProbeCams
    {
        public readonly List<ProbeViewRt> views = new List<ProbeViewRt>();
        public readonly List<int> imageFrames = new List<int>();
        public float floorY, refRootY;
        Transform root;
        static readonly Color Bg = new Color(0.30f, 0.33f, 0.38f, 1f);

        public static ProbeCams Setup(ProbeTargets T, MotionProbeOptions opt, string runDir, float maxJump, List<string> notes)
        {
            var cams = new ProbeCams { root = T.root };
            Vector3 rp = T.root.position;
            float yaw = T.root.eulerAngles.y;
            Quaternion rig = Quaternion.Euler(0f, yaw, 0f);
            Vector3 fwd = rig * Vector3.forward, right = rig * Vector3.right;
            cams.refRootY = rp.y;
            cams.floorY = rp.y;

            bool ok = RendererBounds(T.avatar, out Bounds b);
            float top;
            if (ok && b.max.y - rp.y > 0.2f && b.max.y - rp.y < 4f) top = b.max.y;
            else top = T.head != null ? T.head.position.y + 0.25f : rp.y + 1.6f;
            float height = Mathf.Max(0.3f, top - cams.floorY);
            float extR = 0.15f, extF = 0.15f;
            if (ok)
            {
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 v = corner - rp;
                    extR = Mathf.Max(extR, Mathf.Abs(Vector3.Dot(v, right)));
                    extF = Mathf.Max(extF, Mathf.Abs(Vector3.Dot(v, fwd)));
                }
            }
            float bodyAspect = opt.bodyWidth / (float)opt.bodyHeight;
            float spanY = height + maxJump;
            float orthoBody = Mathf.Max(spanY * 0.5f * 1.08f + 0.03f, (Mathf.Max(extR, extF) * 1.12f + 0.04f) / bodyAspect);
            Vector3 centerBody = new Vector3(rp.x, cams.floorY + spanY * 0.5f, rp.z);

            Vector3 centerHead = rp + Vector3.up * (height * 0.85f);
            float orthoHead = height * 0.18f;
            if (T.head != null)
            {
                var hb = new Bounds(T.head.position, Vector3.zero);
                foreach (var t in T.head.GetComponentsInChildren<Transform>(true)) hb.Encapsulate(t.position);
                hb.Encapsulate(new Vector3(T.head.position.x, top, T.head.position.z));
                orthoHead = Mathf.Max(hb.extents.x, Mathf.Max(hb.extents.y, hb.extents.z)) * 1.12f + 0.05f;
                centerHead = hb.center;
            }

            foreach (var id in Split(opt.bodyViews)) cams.AddView("body", id, centerBody, orthoBody, opt.bodyWidth, opt.bodyHeight, rp, rig, fwd, right, runDir, notes);
            foreach (var id in Split(opt.headViews)) cams.AddView("head", id, centerHead, orthoHead, opt.headSize, opt.headSize, rp, rig, fwd, right, runDir, notes);
            return cams;
        }

        static IEnumerable<string> Split(string csv) =>
            (csv ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).Distinct();

        void AddView(string group, string name, Vector3 center, float ortho, int w, int h, Vector3 rigPos, Quaternion rig, Vector3 fwd, Vector3 right, string runDir, List<string> notes)
        {
            Vector3 dir, up = Vector3.up;
            switch (name)
            {
                case "front": dir = fwd; break;
                case "back": dir = -fwd; break;
                case "side": case "right": name = "side"; dir = right; break;
                case "left": dir = -right; break;
                case "top": dir = Vector3.up; up = fwd; break;
                default: notes.Add("unknown view '" + name + "' ignored"); return;
            }
            if (views.Any(v => v.rec.group == group && v.rec.name == name)) return;
            Vector3 camPos = center + dir * 3f;
            Quaternion camRot = Quaternion.LookRotation(-dir, up);
            var rec = new ProbeViewRec
            {
                id = group + "_" + name, name = name, group = group, w = w, h = h, ortho = ortho,
                camLocalPos = Quaternion.Inverse(rig) * (camPos - rigPos),
                camLocalRot = Quaternion.Inverse(rig) * camRot
            };
            var go = new GameObject("MotionProbeCam_" + rec.id) { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = ortho;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 8f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Bg;
            cam.cullingMask = ~((1 << 5) | (1 << 12) | (1 << 19)); // UI, UiMenu, InternalUI
            cam.allowHDR = false;
            cam.allowMSAA = true;
            cam.useOcclusionCulling = false;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4, hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            string folder = runDir + "/frames/" + rec.id;
            Directory.CreateDirectory(folder);
            views.Add(new ProbeViewRt { rec = rec, cam = cam, rt = rt, tex = tex, dir = folder });
        }

        public void Capture(int k)
        {
            if (views.Count == 0) return;
            Vector3 rp = root.position;
            Vector3 rigPos = new Vector3(rp.x, refRootY, rp.z);
            Quaternion rig = Quaternion.Euler(0f, root.eulerAngles.y, 0f);
            var prev = RenderTexture.active;
            foreach (var v in views)
            {
                v.cam.transform.SetPositionAndRotation(rigPos + rig * v.rec.camLocalPos, rig * v.rec.camLocalRot);
                v.cam.targetTexture = v.rt;
                v.cam.aspect = v.rec.w / (float)v.rec.h;
                v.cam.Render();
                RenderTexture.active = v.rt;
                v.tex.ReadPixels(new Rect(0, 0, v.rec.w, v.rec.h), 0, 0, false);
                v.tex.Apply(false);
                v.cam.targetTexture = null;
                File.WriteAllBytes(v.dir + "/" + k.ToString("D5") + ".jpg", v.tex.EncodeToJPG(90));
            }
            RenderTexture.active = prev;
            imageFrames.Add(k);
        }

        public void Dispose()
        {
            foreach (var v in views)
            {
                if (v.cam != null) UnityEngine.Object.DestroyImmediate(v.cam.gameObject);
                if (v.rt != null) { v.rt.Release(); UnityEngine.Object.DestroyImmediate(v.rt); }
                if (v.tex != null) UnityEngine.Object.DestroyImmediate(v.tex);
                v.cam = null; v.rt = null; v.tex = null;
            }
        }

        static bool RendererBounds(GameObject go, out Bounds b)
        {
            b = default;
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || !(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                var rb = r.bounds;
                if (rb.size.sqrMagnitude < 1e-8f || rb.size.magnitude > 10f) continue;
                if (!any) { b = rb; any = true; }
                else b.Encapsulate(rb);
            }
            return any;
        }
    }
}
