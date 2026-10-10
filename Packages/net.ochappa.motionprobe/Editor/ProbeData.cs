using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MotionProbeTool
{
    [Serializable] public class ProbeEventRec { public float t; public string label; public string text; }

    [Serializable]
    public class ProbeViewRec
    {
        public string id;      // folder name, e.g. body_front
        public string name;    // front/side/back/left/top
        public string group;   // body/head
        public int w;
        public int h;
        public float ortho;    // orthographic half height (m)
        public Vector3 camLocalPos;      // camera pose in the avatar "rig" frame at t=0
        public Quaternion camLocalRot;
    }

    [Serializable]
    public class ProbePointRec
    {
        public string name;
        public int kind;       // 0 humanoid bone, 1 physbone joint
        public int comp;       // physbone component index (-1 for humanoid)
        public int frame;      // reference frame index (0 = avatar root)
        public int depth;
        public bool isVirtual; // physbone endpoint
        public Vector3 refLocal; // position in the reference frame at t=0
    }

    [Serializable]
    public class ProbeChainRec
    {
        public string name;
        public int comp;
        public int frame;
        public int[] pts;
        public float restLength;
    }

    [Serializable]
    public class ProbeCompRec
    {
        public string name;
        public string group;
        public string side;
        public string pairKey;
        public string path;
        public string parentName;
        public string settings;
        public float gravity;
        public bool underHead;
        public int[] chains;
        public string[] colliders;
    }

    [Serializable] public class ProbeHumanRec { public string name; public int point; }
    [Serializable] public class ProbeColliderRec { public string name; public string shape; }

    [Serializable]
    public class ProbeMeta
    {
        public string version;
        public string label;
        public string avatar;
        public string scenario;
        public string scenarioExpanded;
        public string startedAt;
        public string unityVersion;
        public string optionsJson;
        public float fps;
        public float dt;
        public float duration;
        public float imageFps;
        public int imageEvery;
        public float preroll;
        public float floorY;
        public float refRootY;
        public float realSeconds;
        public int frameCount;
        public List<ProbeEventRec> events = new List<ProbeEventRec>();
        public List<ProbeViewRec> views = new List<ProbeViewRec>();
        public List<ProbePointRec> points = new List<ProbePointRec>();
        public List<ProbeChainRec> chains = new List<ProbeChainRec>();
        public List<ProbeCompRec> comps = new List<ProbeCompRec>();
        public List<string> frames = new List<string>();
        public List<ProbeColliderRec> colliders = new List<ProbeColliderRec>();
        public List<ProbeHumanRec> humans = new List<ProbeHumanRec>();
        public List<int> imageFrames = new List<int>();
        public List<string> notes = new List<string>();
    }

    /// <summary>Everything recorded in one run. Arrays are frame-major: index = frame * count + item.</summary>
    public class ProbeData
    {
        const int Magic = 0x424F5250; // "PROB"
        const int FormatVersion = 1;

        public ProbeMeta meta;
        public string dir;
        public int F, P, R, C;
        public Vector3[] rootPos;
        public Quaternion[] rootRot;
        public Vector3[] fpos;
        public Quaternion[] frot;
        public Vector3[] fscl;
        public Vector3[] pts;
        public float[] clrA;    // min clearance to colliders assigned to the chain (m, negative = inside)
        public float[] clrAll;  // min clearance to any avatar collider the chain was clear of at t=0
        public int[] clrIdx;    // collider index of clrAll

        public Vector3 Pt(int f, int p) => pts[f * P + p];
        public Matrix4x4 FrameM(int f, int r) => Matrix4x4.TRS(fpos[f * R + r], frot[f * R + r], fscl[f * R + r]);
        public Matrix4x4 RootM(int f) => Matrix4x4.TRS(rootPos[f], rootRot[f], Vector3.one);
        public Vector3 RefWorld(int f, int p)
        {
            var pr = meta.points[p];
            return FrameM(f, pr.frame).MultiplyPoint3x4(pr.refLocal);
        }
        public int FrameAt(float t) => Mathf.Clamp(Mathf.RoundToInt(t / meta.dt), 0, F - 1);

        public int NearestImageFrame(float t)
        {
            int f = FrameAt(t);
            var list = meta.imageFrames;
            if (list == null || list.Count == 0) return f;
            int best = list[0];
            foreach (int k in list) if (Math.Abs(k - f) < Math.Abs(best - f)) best = k;
            return best;
        }

        public ProbeViewRec View(string group, string name)
        {
            foreach (var v in meta.views) if (v.group == group && v.name == name) return v;
            return null;
        }

        public string FramePath(ProbeViewRec v, int f) => dir + "/frames/" + v.id + "/" + f.ToString("D5") + ".jpg";

        public void Save()
        {
            Directory.CreateDirectory(dir + "/data");
            File.WriteAllText(dir + "/data/meta.json", JsonUtility.ToJson(meta, true));
            using (var bw = new BinaryWriter(File.Create(dir + "/data/frames.bin")))
            {
                bw.Write(Magic); bw.Write(FormatVersion);
                bw.Write(F); bw.Write(P); bw.Write(R); bw.Write(C);
                for (int f = 0; f < F; f++)
                {
                    W(bw, rootPos[f]); W(bw, rootRot[f]);
                    for (int r = 0; r < R; r++) { int i = f * R + r; W(bw, fpos[i]); W(bw, frot[i]); W(bw, fscl[i]); }
                    for (int p = 0; p < P; p++) W(bw, pts[f * P + p]);
                    for (int c = 0; c < C; c++) { int i = f * C + c; bw.Write(clrA[i]); bw.Write(clrAll[i]); bw.Write(clrIdx[i]); }
                }
            }
        }

        public static ProbeData Load(string dir, out string error)
        {
            error = null;
            string metaPath = dir + "/data/meta.json", binPath = dir + "/data/frames.bin";
            if (!File.Exists(metaPath) || !File.Exists(binPath)) { error = "run has no data (still running, or failed): " + dir; return null; }
            var d = new ProbeData { dir = dir.Replace('\\', '/') };
            d.meta = JsonUtility.FromJson<ProbeMeta>(File.ReadAllText(metaPath));
            using (var br = new BinaryReader(File.OpenRead(binPath)))
            {
                if (br.ReadInt32() != Magic) { error = "bad frames.bin"; return null; }
                br.ReadInt32();
                d.F = br.ReadInt32(); d.P = br.ReadInt32(); d.R = br.ReadInt32(); d.C = br.ReadInt32();
                d.Allocate();
                for (int f = 0; f < d.F; f++)
                {
                    d.rootPos[f] = V3(br); d.rootRot[f] = Q(br);
                    for (int r = 0; r < d.R; r++) { int i = f * d.R + r; d.fpos[i] = V3(br); d.frot[i] = Q(br); d.fscl[i] = V3(br); }
                    for (int p = 0; p < d.P; p++) d.pts[f * d.P + p] = V3(br);
                    for (int c = 0; c < d.C; c++) { int i = f * d.C + c; d.clrA[i] = br.ReadSingle(); d.clrAll[i] = br.ReadSingle(); d.clrIdx[i] = br.ReadInt32(); }
                }
            }
            return d;
        }

        static ProbeData cached;
        static string cachedDir;
        static DateTime cachedStamp;

        public static ProbeData LoadRun(string run, out string error)
        {
            error = null;
            string dir = ProbeIO.ResolveRun(run);
            if (dir == null) { error = "run not found: " + (run ?? "latest"); return null; }
            string bin = dir + "/data/frames.bin";
            if (!File.Exists(bin)) { error = "run has no data yet (state: " + (ProbeIO.ReadStatus(dir)?.state ?? "?") + "): " + dir; return null; }
            var stamp = File.GetLastWriteTimeUtc(bin);
            if (cached != null && cachedDir == dir && cachedStamp == stamp) return cached;
            var d = Load(dir, out error);
            if (d != null) { cached = d; cachedDir = dir; cachedStamp = stamp; }
            return d;
        }

        public void Allocate()
        {
            rootPos = new Vector3[F]; rootRot = new Quaternion[F];
            fpos = new Vector3[F * R]; frot = new Quaternion[F * R]; fscl = new Vector3[F * R];
            pts = new Vector3[F * P];
            clrA = new float[F * C]; clrAll = new float[F * C]; clrIdx = new int[F * C];
        }

        static void W(BinaryWriter bw, Vector3 v) { bw.Write(v.x); bw.Write(v.y); bw.Write(v.z); }
        static void W(BinaryWriter bw, Quaternion q) { bw.Write(q.x); bw.Write(q.y); bw.Write(q.z); bw.Write(q.w); }
        static Vector3 V3(BinaryReader br) => new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        static Quaternion Q(BinaryReader br) => new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
    }
}
