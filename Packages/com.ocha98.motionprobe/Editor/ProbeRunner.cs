using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace MotionProbeTool
{
    internal struct MotionProbeDriveStep { }
    internal struct MotionProbeCaptureStep { }

    /// <summary>
    /// Player-loop hooks: Drive runs at the end of Update (before animation and PhysBones),
    /// Capture runs right after FinishFrameRendering (all LateUpdates, skinning and rendering are done).
    /// </summary>
    internal static class ProbeLoop
    {
        static bool installed;

        public static void Install()
        {
            var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            Strip(ref loop);
            var subs = loop.subSystemList;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type == typeof(UnityEngine.PlayerLoop.Update))
                {
                    var list = new List<UnityEngine.LowLevel.PlayerLoopSystem>(subs[i].subSystemList ?? new UnityEngine.LowLevel.PlayerLoopSystem[0]);
                    list.Add(new UnityEngine.LowLevel.PlayerLoopSystem { type = typeof(MotionProbeDriveStep), updateDelegate = OnDrive });
                    subs[i].subSystemList = list.ToArray();
                }
                else if (subs[i].type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                {
                    var list = new List<UnityEngine.LowLevel.PlayerLoopSystem>(subs[i].subSystemList ?? new UnityEngine.LowLevel.PlayerLoopSystem[0]);
                    int idx = list.FindIndex(s => s.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate.FinishFrameRendering));
                    var sys = new UnityEngine.LowLevel.PlayerLoopSystem { type = typeof(MotionProbeCaptureStep), updateDelegate = OnCapture };
                    if (idx >= 0) list.Insert(idx + 1, sys); else list.Add(sys);
                    subs[i].subSystemList = list.ToArray();
                }
            }
            loop.subSystemList = subs;
            UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
            installed = true;
        }

        public static void Uninstall()
        {
            if (!installed) return;
            var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            Strip(ref loop);
            UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
            installed = false;
        }

        static void Strip(ref UnityEngine.LowLevel.PlayerLoopSystem sys)
        {
            if (sys.subSystemList == null) return;
            var list = sys.subSystemList.Where(s => s.type != typeof(MotionProbeDriveStep) && s.type != typeof(MotionProbeCaptureStep)).ToArray();
            for (int i = 0; i < list.Length; i++) Strip(ref list[i]);
            sys.subSystemList = list;
        }

        static void OnDrive()
        {
            var r = ProbeRunner.Active;
            if (r != null && Application.isPlaying) r.Drive();
        }

        static void OnCapture()
        {
            var r = ProbeRunner.Active;
            if (r != null && Application.isPlaying) r.Capture();
        }
    }

    /// <summary>Reflection bridge to Gesture Manager (optional dependency).</summary>
    internal class GmBridge
    {
        Component gm;
        FieldInfo moduleField;
        object module;
        MethodInfo getParam;
        FieldInfo avatarField;
        readonly Dictionary<Type, MethodInfo> setCache = new Dictionary<Type, MethodInfo>();
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static GmBridge Find()
        {
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name != "GestureManager" || t.Namespace == null || !t.Namespace.StartsWith("BlackStartX")) continue;
                var f = t.GetField("Module", All);
                return f == null ? null : new GmBridge { gm = mb, moduleField = f };
            }
            return null;
        }

        bool Refresh()
        {
            if (gm == null) return false;
            var m = moduleField.GetValue(gm);
            if (m == null) { module = null; return false; }
            if (!ReferenceEquals(m, module))
            {
                module = m;
                var mt = m.GetType();
                getParam = mt.GetMethod("GetParam", All, null, new[] { typeof(string) }, null);
                avatarField = mt.GetField("Avatar", BindingFlags.Public | BindingFlags.Instance);
                setCache.Clear();
            }
            return getParam != null;
        }

        public GameObject ModuleAvatar()
        {
            if (!Refresh() || avatarField == null) return null;
            return avatarField.GetValue(module) as GameObject;
        }

        public bool TrySet(string name, float value, out string error)
        {
            error = null;
            try
            {
                if (!Refresh()) { error = "GestureManager module not ready"; return false; }
                var p = getParam.Invoke(module, new object[] { name });
                if (p == null) { error = "parameter not found on the avatar"; return false; }
                var pt = p.GetType();
                if (!setCache.TryGetValue(pt, out MethodInfo set))
                {
                    set = pt.GetMethod("Set", BindingFlags.Public | BindingFlags.Instance, null, new[] { module.GetType(), typeof(float), typeof(object) }, null);
                    setCache[pt] = set;
                }
                if (set == null) { error = "GestureManager Set(float) not found"; return false; }
                set.Invoke(p, new object[] { module, value, null });
                return true;
            }
            catch (Exception e)
            {
                error = e.InnerException?.Message ?? e.Message;
                return false;
            }
        }
    }

    /// <summary>One capture run inside play mode, driven by the player-loop hooks.</summary>
    internal class ProbeRunner
    {
        public static ProbeRunner Active;
        public string RunDir => req.runDir;

        enum Phase { WaitAvatar, Preroll, Running, Finishing, Done }

        class TurnProc { public float start, dur, deg; }
        class OscProc { public int kind; public float start, dur, amp, hz; } // kind 0 yaw (deg), 1 sway x (m), 2 bob y (m)
        class JumpProc { public float start, v0; }
        const float Gravity = 9.81f;

        readonly MotionProbeRequest req;
        readonly MotionProbeOptions opt;
        readonly ProbeScenario sc;
        Phase phase = Phase.WaitAvatar;
        float dt;
        int waitFrames, prerollFrames, prerollCount, k, totalFrames, imageEvery, cmdIndex;
        float savedCaptureDt;
        bool savedRunInBg;
        DateTime startReal;
        GmBridge gm;
        GameObject avatar;
        Transform root;
        Animator animator;
        AnimatorCullingMode savedCulling;
        readonly List<KeyValuePair<SkinnedMeshRenderer, bool>> savedOffscreen = new List<KeyValuePair<SkinnedMeshRenderer, bool>>();
        ProbeTargets targets;
        ProbeCams cams;
        ProbeRecorder rec;
        readonly List<string> notes = new List<string>();
        readonly List<ProbeEventRec> events = new List<ProbeEventRec>();
        readonly HashSet<string> paramWarned = new HashSet<string>();
        readonly Dictionary<string, float> paramLast = new Dictionary<string, float>();

        Vector3 basePos, travel, velCur, velTarget;
        Quaternion baseRot;
        float baseYaw, accel = 8f;
        readonly List<TurnProc> turns = new List<TurnProc>();
        readonly List<OscProc> oscs = new List<OscProc>();
        JumpProc jump;

        ProbeRunner(MotionProbeRequest r, MotionProbeOptions o, ProbeScenario s)
        {
            req = r; opt = o; sc = s;
        }

        public static void Start(MotionProbeRequest r)
        {
            var o = MotionProbeOptions.Parse(r.optionsJson, out _) ?? new MotionProbeOptions();
            var s = ProbeScenario.Parse(r.scenario, out string err);
            if (s == null)
            {
                ProbeIO.WriteStatus(r.runDir, new MotionProbeStatus { state = "error", runDir = r.runDir, message = "scenario: " + err });
                return;
            }
            var run = new ProbeRunner(r, o, s);
            Active = run;
            run.Begin();
        }

        void Begin()
        {
            dt = 1f / opt.fps;
            startReal = DateTime.Now;
            totalFrames = Mathf.Max(1, Mathf.CeilToInt(sc.Duration / dt - 1e-3f));
            prerollFrames = Mathf.Max(1, Mathf.RoundToInt(opt.preroll / dt));
            imageEvery = Mathf.Max(1, Mathf.RoundToInt(opt.fps / opt.imageFps));
            savedCaptureDt = Time.captureDeltaTime;
            Time.captureDeltaTime = dt;
            savedRunInBg = Application.runInBackground;
            Application.runInBackground = true;
            ProbeLoop.Install();
            Status("starting", "waiting for the avatar and Gesture Manager");
        }

        public void Drive()
        {
            if (phase == Phase.Finishing || phase == Phase.Done) return;
            try
            {
                switch (phase)
                {
                    case Phase.WaitAvatar: WaitAvatar(); break;
                    case Phase.Preroll:
                        prerollCount++;
                        if (root != null) root.SetPositionAndRotation(basePos, baseRot);
                        break;
                    case Phase.Running:
                        k++;
                        StepScenario();
                        break;
                }
            }
            catch (Exception e) { Fail("drive: " + e); }
        }

        public void Capture()
        {
            if (phase == Phase.Finishing || phase == Phase.Done) return;
            try
            {
                if (phase == Phase.Preroll && prerollCount >= prerollFrames) BeginRecording();
                else if (phase == Phase.Running)
                {
                    rec.Record(k);
                    if (k % imageEvery == 0) cams.Capture(k);
                    if (k % 30 == 0) Status("running", null);
                    if (k >= totalFrames) Finish();
                }
            }
            catch (Exception e) { Fail("capture: " + e); }
        }

        void WaitAvatar()
        {
            waitFrames++;
            if (gm == null && waitFrames % 5 == 1) gm = GmBridge.Find();
            if (avatar == null && waitFrames % 5 == 1) avatar = FindAvatar(opt.avatar, gm);
            if (avatar == null)
            {
                if (waitFrames > 600) Fail("No active avatar (VRCAvatarDescriptor) found in the scene" + (string.IsNullOrEmpty(opt.avatar) ? "" : " named '" + opt.avatar + "'"));
                return;
            }
            bool ready = gm == null ? waitFrames > 10 : gm.ModuleAvatar() == avatar;
            if (!ready)
            {
                if (waitFrames < 900) return;
                notes.Add("Gesture Manager did not attach to '" + avatar.name + "': avatar parameters and locomotion animation may not apply.");
            }
            if (gm == null) notes.Add("Gesture Manager not found: avatar parameters are set on the Animator directly (may have no effect).");

            root = avatar.transform;
            animator = avatar.GetComponent<Animator>();
            if (animator != null)
            {
                savedCulling = animator.cullingMode;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                savedOffscreen.Add(new KeyValuePair<SkinnedMeshRenderer, bool>(smr, smr.updateWhenOffscreen));
                smr.updateWhenOffscreen = true;
            }
            CheckProxyLocomotion();
            basePos = root.position;
            baseRot = root.rotation;
            baseYaw = root.eulerAngles.y;
            phase = Phase.Preroll;
            prerollCount = 0;
            Status("preroll", "settling for " + ProbeIO.F(opt.preroll) + " s before recording");
        }

        /// <summary>VRChat's proxy_* locomotion clips are single-frame placeholders outside the VRChat client: walking legs freeze in one pose.</summary>
        void CheckProxyLocomotion()
        {
            try
            {
                Component desc = avatar.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "VRCAvatarDescriptor");
                if (desc == null) return;
                var so = new SerializedObject(desc);
                var layers = so.FindProperty("baseAnimationLayers");
                if (layers == null || layers.arraySize == 0) return;
                var ctrl = layers.GetArrayElementAtIndex(0).FindPropertyRelative("animatorController").objectReferenceValue as RuntimeAnimatorController;
                bool usesDefault = layers.GetArrayElementAtIndex(0).FindPropertyRelative("isDefault").boolValue || ctrl == null;
                var proxies = usesDefault ? new List<string> { "(VRChat default locomotion)" }
                    : ctrl.animationClips.Where(c => c != null && c.name.StartsWith("proxy_") && c.length <= 0.04f && (c.name.Contains("walk") || c.name.Contains("run")))
                        .Select(c => c.name).Distinct().ToList();
                if (proxies.Count > 0)
                    notes.Add("Locomotion uses VRChat proxy animations (" + string.Join(", ", proxies.Take(4)) + (proxies.Count > 4 ? ", ..." : "")
                        + "). Outside the VRChat client these are single-frame placeholder poses, so during walk/run the legs hold one stride pose instead of cycling. "
                        + "Root movement, acceleration and the PhysBone inertia it causes are real; leg-driven swing (e.g. skirt hit by stepping legs) is not represented.");
            }
            catch (Exception e) { notes.Add("locomotion check failed: " + e.Message); }
        }

        static GameObject FindAvatar(string name, GmBridge gm)
        {
            var list = new List<GameObject>();
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (mb != null && mb.GetType().Name == "VRCAvatarDescriptor" && mb.gameObject.activeInHierarchy) list.Add(mb.gameObject);
            if (!string.IsNullOrEmpty(name))
                return list.FirstOrDefault(g => g.name == name) ?? list.FirstOrDefault(g => g.name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            var ga = gm?.ModuleAvatar();
            if (ga != null) return ga;
            return list.FirstOrDefault();
        }

        void BeginRecording()
        {
            targets = ProbeTargets.Discover(avatar, animator, notes);
            cams = ProbeCams.Setup(targets, opt, req.runDir, sc.MaxJump, notes);
            rec = new ProbeRecorder(targets, totalFrames + 1);
            k = 0;
            rec.Record(0);
            cams.Capture(0);
            phase = Phase.Running;
            Status("running", null);
        }

        void StepScenario()
        {
            float tCmd = (k - 1) * dt + 1e-5f;
            while (cmdIndex < sc.Commands.Count && sc.Commands[cmdIndex].Time <= tCmd)
            {
                Execute(sc.Commands[cmdIndex]);
                cmdIndex++;
            }
            Integrate(k * dt);
        }

        void Execute(ProbeCommand c)
        {
            switch (c.Op)
            {
                case "move":
                    velTarget = new Vector3(c.Num[0], 0f, c.Num[1]);
                    accel = Mathf.Max(0.1f, c.Num[2]);
                    break;
                case "turn":
                    turns.Add(new TurnProc { start = c.Time, deg = c.Num[0], dur = c.Num[1] });
                    events.Add(new ProbeEventRec { t = c.Time + c.Num[1], label = "TURN END", text = "(automatic: turn finished)" });
                    break;
                case "jump":
                    jump = new JumpProc { start = c.Time, v0 = Mathf.Sqrt(2f * Gravity * c.Num[0]) };
                    events.Add(new ProbeEventRec { t = c.Time + 2f * jump.v0 / Gravity, label = "LAND", text = "(automatic: feet back on the ground)" });
                    break;
                case "shake":
                case "sway":
                case "bob":
                    int kind = c.Op == "shake" ? 0 : c.Op == "sway" ? 1 : 2;
                    oscs.Add(new OscProc { kind = kind, start = c.Time, amp = kind == 0 ? c.Num[0] : c.Num[0] * 0.01f, hz = c.Num[1], dur = c.Num[2] });
                    events.Add(new ProbeEventRec { t = c.Time + c.Num[2], label = c.Label + " END", text = "(automatic: oscillation finished)" });
                    break;
                case "param":
                    SetParam(c.Name, c.Num[0], false);
                    break;
                case "gesture":
                    SetParam("GestureLeft", Mathf.Round(c.Num[0]), false);
                    SetParam("GestureRight", Mathf.Round(c.Num[1]), false);
                    break;
            }
            events.Add(new ProbeEventRec { t = c.Time, label = c.Label, text = c.Text });
        }

        void Integrate(float tau)
        {
            Vector3 dv = velTarget - velCur;
            float maxDv = accel * dt;
            if (dv.magnitude > maxDv) dv = dv.normalized * maxDv;
            velCur += dv;

            float turnYaw = 0f;
            foreach (var t in turns)
            {
                float p = Mathf.Clamp01((tau - t.start) / t.dur);
                turnYaw += t.deg * p * p * (3f - 2f * p);
            }
            float shakeYaw = 0f, swayX = 0f, bobY = 0f;
            foreach (var o in oscs)
            {
                float local = tau - o.start;
                if (local < 0f || local > o.dur) continue;
                float ramp = Mathf.Min(0.15f, o.dur * 0.25f);
                float env = Smooth(local / ramp) * Smooth((o.dur - local) / ramp);
                float v = o.amp * Mathf.Sin(2f * Mathf.PI * o.hz * local) * env;
                if (o.kind == 0) shakeYaw += v; else if (o.kind == 1) swayX += v; else bobY += v;
            }
            float heading = baseYaw + turnYaw;
            Quaternion headingRot = Quaternion.Euler(0f, heading, 0f);
            travel += headingRot * velCur * dt;

            float jumpY = 0f, vy = 0f;
            bool grounded = true;
            if (jump != null)
            {
                float j = tau - jump.start;
                float air = 2f * jump.v0 / Gravity;
                if (j >= 0f && j <= air) { jumpY = jump.v0 * j - 0.5f * Gravity * j * j; vy = jump.v0 - Gravity * j; grounded = false; }
                else if (j > air) jump = null;
            }

            Vector3 pos = basePos + travel + headingRot * new Vector3(swayX, 0f, 0f) + Vector3.up * (jumpY + bobY);
            Quaternion rot = Quaternion.Euler(0f, heading + shakeYaw - baseYaw, 0f) * baseRot;
            root.SetPositionAndRotation(pos, rot);

            SetParamIfChanged("VelocityX", velCur.x);
            SetParamIfChanged("VelocityZ", velCur.z);
            SetParamIfChanged("VelocityY", vy);
            SetParamIfChanged("VelocityMagnitude", new Vector3(velCur.x, vy, velCur.z).magnitude);
            SetParamIfChanged("Grounded", grounded ? 1f : 0f);
        }

        static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        void SetParamIfChanged(string name, float v)
        {
            if (paramLast.TryGetValue(name, out float last) && Mathf.Abs(last - v) < 1e-4f) return;
            paramLast[name] = v;
            SetParam(name, v, true);
        }

        bool SetParam(string name, float v, bool quiet)
        {
            string err = null;
            bool ok = gm != null && gm.TrySet(name, v, out err);
            if (!ok && animator != null) ok = TrySetAnimator(name, v);
            if (!ok && !quiet && paramWarned.Add(name)) notes.Add("Could not set parameter '" + name + "': " + (err ?? "not found"));
            return ok;
        }

        bool TrySetAnimator(string name, float v)
        {
            if (animator.runtimeAnimatorController == null) return false;
            foreach (var p in animator.parameters)
            {
                if (p.name != name) continue;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: animator.SetFloat(name, v); return true;
                    case AnimatorControllerParameterType.Int: animator.SetInteger(name, Mathf.RoundToInt(v)); return true;
                    case AnimatorControllerParameterType.Bool: animator.SetBool(name, v > 0.5f); return true;
                    case AnimatorControllerParameterType.Trigger: if (v > 0.5f) animator.SetTrigger(name); return true;
                }
            }
            return false;
        }

        void Finish()
        {
            phase = Phase.Finishing;
            EditorApplication.delayCall += Complete;
        }

        void Complete()
        {
            try
            {
                Status("analyzing", "writing data, report and images");
                cams.Dispose();
                var meta = new ProbeMeta
                {
                    version = MotionProbe.Version,
                    label = System.IO.Path.GetFileName(req.runDir),
                    avatar = avatar != null ? avatar.name : "?",
                    scenario = req.scenario,
                    scenarioExpanded = sc.Expanded,
                    startedAt = startReal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    optionsJson = req.optionsJson,
                    fps = opt.fps,
                    dt = dt,
                    duration = totalFrames * dt,
                    imageFps = opt.fps / imageEvery,
                    imageEvery = imageEvery,
                    preroll = opt.preroll,
                    floorY = cams.floorY,
                    refRootY = cams.refRootY,
                    realSeconds = (float)(DateTime.Now - startReal).TotalSeconds,
                    events = events.Where(e => e.t <= totalFrames * dt + 1e-4f).OrderBy(e => e.t).ToList(),
                    views = cams.views.Select(v => v.rec).ToList(),
                    imageFrames = cams.imageFrames.ToList(),
                    notes = notes
                };
                var data = rec.Build(meta, req.runDir);
                data.Save();
                var result = ProbeOutputs.GenerateAll(data);
                ProbeIO.WriteStatus(req.runDir, new MotionProbeStatus
                {
                    state = "done", runDir = req.runDir, frame = k, frames = totalFrames, simTime = k * dt, duration = sc.Duration,
                    message = result.headline, outputs = result.files.ToArray()
                });
            }
            catch (Exception e)
            {
                ProbeIO.WriteStatus(req.runDir, new MotionProbeStatus { state = "error", runDir = req.runDir, frame = k, frames = totalFrames, message = "analysis: " + e });
                Debug.LogError("[MotionProbe] " + e);
            }
            finally
            {
                Cleanup();
                if (req.exitPlayMode && EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
            }
        }

        void Status(string state, string msg)
        {
            try
            {
                ProbeIO.WriteStatus(req.runDir, new MotionProbeStatus
                {
                    state = state, runDir = req.runDir, frame = k, frames = totalFrames, simTime = k * dt, duration = sc.Duration,
                    message = msg ?? ("real time " + (DateTime.Now - startReal).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s")
                });
            }
            catch { }
        }

        void Fail(string msg)
        {
            if (phase == Phase.Done) return;
            Debug.LogError("[MotionProbe] " + msg);
            ProbeIO.WriteStatus(req.runDir, new MotionProbeStatus { state = "error", runDir = req.runDir, frame = k, frames = totalFrames, message = msg });
            Cleanup();
            if (req.exitPlayMode && EditorApplication.isPlaying) EditorApplication.delayCall += () => { if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode(); };
        }

        public void Abort(string reason)
        {
            if (phase == Phase.Done || phase == Phase.Finishing) return;
            ProbeIO.WriteStatus(req.runDir, new MotionProbeStatus { state = "aborted", runDir = req.runDir, frame = k, frames = totalFrames, message = reason });
            Cleanup();
        }

        void Cleanup()
        {
            if (phase == Phase.Done) return;
            phase = Phase.Done;
            ProbeLoop.Uninstall();
            Time.captureDeltaTime = savedCaptureDt;
            Application.runInBackground = savedRunInBg;
            try { cams?.Dispose(); } catch { }
            if (!req.exitPlayMode && EditorApplication.isPlaying)
            {
                try
                {
                    if (root != null) root.SetPositionAndRotation(basePos, baseRot);
                    if (animator != null) animator.cullingMode = savedCulling;
                    foreach (var kv in savedOffscreen) if (kv.Key != null) kv.Key.updateWhenOffscreen = kv.Value;
                    SetParam("VelocityX", 0f, true); SetParam("VelocityZ", 0f, true); SetParam("VelocityY", 0f, true);
                    SetParam("VelocityMagnitude", 0f, true); SetParam("Grounded", 1f, true);
                }
                catch { }
            }
            if (Active == this) Active = null;
        }
    }
}
