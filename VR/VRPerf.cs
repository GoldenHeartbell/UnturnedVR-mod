using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Frame cost report (VRDebugFlags.LogPerf): every 5 s one log line with the frame rate, the longest frame,
    /// how much time per frame the mod's own scripts took (Stopwatch around the hot entry points), garbage
    /// collections and heap growth. If our scripts are a fraction of the frame time, the cost is in the game or the
    /// renderer, not in the mod. Costs nothing when the flag is off (Begin returns 0).
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class VRPerf : MonoBehaviour
    {
        public const int Arms = 0;
        public const int Hands = 1;
        public const int Controllers = 2;
        public const int KeyMap = 3;
        public const int Pointer = 4;
        public const int Gui = 5;
        private const int Count = 6;
        private static readonly string[] Names = { "arms", "hands", "input", "keys", "pointer", "gui" };
        private const float ReportSeconds = 5f;

        private static readonly long[] ticks = new long[Count];
        private static VRPerf instance;

        private int frames;
        private float frameSum;
        private float frameMax;
        private float nextReport;
        private int collections;
        private long frameStart;
        private long preRenderTicks;
        private int lastCameraCount;
        private readonly UnityEngine.FrameTiming[] timings = new UnityEngine.FrameTiming[1];
        private long heap;
        private readonly StringBuilder text = new StringBuilder(256);

        public static long Begin()
        {
            return VRDebugFlags.LogPerf ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void End(int slot, long start)
        {
            if (start != 0L)
            {
                ticks[slot] += Stopwatch.GetTimestamp() - start;
            }
        }

        /// <summary>Creates the reporter once, if the flag is on.</summary>
        public static void Ensure()
        {
            if (!VRDebugFlags.LogPerf || instance != null)
            {
                return;
            }
            GameObject go = new GameObject("UnturnedVR perf");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<VRPerf>();
            InstallLoopMarkers();
        }

        // ---- Unity player loop markers: time per engine stage (Update, animation, canvases, XR, ...) -------------------
        // A marker system is inserted before every subsystem of every phase; the time between two markers is the
        // cost of the subsystem in between. Reported as the biggest few per frame.
        private static string[] loopLabels;
        private static readonly long[] loopStamps = new long[256];
        private static readonly double[] loopSum = new double[256];
        private static int loopFrames;
        private static int loopLast;
        private static double loopTotal;       // first to last marker of a frame
        private static double loopGap;         // last marker of the previous frame to first marker of this one (end of frame: present, waits)
        private static long previousEndStamp;
        // camera render window (first PreCull to last PostRender of a frame) and onBeforeRender calls
        private static long renderBegin;
        private static long renderEnd;
        private static int renderBeginFrame = -1;
        private static double renderSum;
        private static int beforeRenderCalls;
        // per camera: culling (PreCull to PreRender) and drawing (PreRender to PostRender)
        private const int MaxCameras = 8;
        private static readonly Camera[] camCameras = new Camera[MaxCameras];
        private static readonly long[] camCullStart = new long[MaxCameras];
        private static readonly long[] camRenderStart = new long[MaxCameras];
        private static readonly double[] camCullSum = new double[MaxCameras];
        private static readonly double[] camRenderSum = new double[MaxCameras];

        private static int CameraSlot(Camera camera)
        {
            for (int i = 0; i < MaxCameras; i++)
            {
                if (camCameras[i] == camera)
                {
                    return i;
                }
            }
            for (int i = 0; i < MaxCameras; i++)
            {
                if (camCameras[i] == null)
                {
                    camCameras[i] = camera;
                    return i;
                }
            }
            return -1;
        }

        private static void InstallLoopMarkers()
        {
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoopSystem[] phases = root.subSystemList;
                var labels = new List<string>();
                for (int p = 0; p < phases.Length; p++)
                {
                    PlayerLoopSystem[] children = phases[p].subSystemList ?? new PlayerLoopSystem[0];
                    var list = new List<PlayerLoopSystem>(children.Length * 2 + 1);
                    foreach (PlayerLoopSystem child in children)
                    {
                        list.Add(Marker(labels.Count));
                        labels.Add(phases[p].type.Name + "." + (child.type != null ? child.type.Name : "?"));
                        list.Add(child);
                    }
                    if (p == phases.Length - 1)
                    {
                        loopLast = labels.Count;
                        list.Add(Marker(labels.Count));
                        labels.Add("end");
                    }
                    phases[p].subSystemList = list.ToArray();
                }
                if (labels.Count > loopStamps.Length)
                {
                    VRLog.Warn("perf: too many player loop systems (" + labels.Count + "), loop timing skipped.");
                    return;
                }
                loopLabels = labels.ToArray();
                root.subSystemList = phases;
                PlayerLoop.SetPlayerLoop(root);
                VRLog.Info("perf: player loop timing installed (" + labels.Count + " stages).");
            }
            catch (Exception exception)
            {
                VRLog.Warn("perf: player loop timing failed: " + exception.Message);
            }
        }

        private static PlayerLoopSystem Marker(int index)
        {
            return new PlayerLoopSystem
            {
                type = typeof(VRPerf),
                updateDelegate = () =>
                {
                    loopStamps[index] = Stopwatch.GetTimestamp();
                    if (index == loopLast)
                    {
                        CloseLoopFrame();
                    }
                },
            };
        }

        // Durations between consecutive stamps (stages that did not run this frame, e.g. FixedUpdate, have no stamp).
        private static void CloseLoopFrame()
        {
            int previous = -1;
            long firstStamp = 0L;
            for (int k = 0; k <= loopLast; k++)
            {
                if (loopStamps[k] == 0L)
                {
                    continue;
                }
                if (previous >= 0)
                {
                    loopSum[previous] += loopStamps[k] - loopStamps[previous];
                }
                else
                {
                    firstStamp = loopStamps[k];
                }
                previous = k;
                if (k != loopLast)
                {
                    // keep the stamp until the next frame overwrites it, but forget skipped stages below
                }
            }
            if (firstStamp != 0L)
            {
                loopTotal += loopStamps[loopLast] - firstStamp;
                if (previousEndStamp != 0L)
                {
                    loopGap += firstStamp - previousEndStamp;
                }
            }
            previousEndStamp = loopStamps[loopLast];
            Array.Clear(loopStamps, 0, loopLast + 1);
            loopFrames++;
        }

        private void LogLoop()
        {
            if (loopLabels == null || loopFrames == 0)
            {
                return;
            }
            double toMs = 1000.0 / Stopwatch.Frequency / loopFrames;
            text.Length = 0;
            text.Append("perf: frame split ms/frame: engine loop ").Append((loopTotal * toMs).ToString("0.0"))
                .Append(", between frames (present/waits) ").Append((loopGap * toMs).ToString("0.0"))
                .Append(", camera render window ").Append((renderSum * toMs).ToString("0.0"))
                .Append(", onBeforeRender calls ").Append((beforeRenderCalls / (double)loopFrames).ToString("0.0")).Append(" per frame.");
            VRLog.Info(text.ToString());
            text.Length = 0;
            text.Append("perf: per camera ms/frame (cull / draw):");
            for (int i = 0; i < MaxCameras; i++)
            {
                if (camCameras[i] != null)
                {
                    text.Append(" [").Append(camCameras[i].name).Append(' ').Append((camCullSum[i] * toMs).ToString("0.0")).Append(" / ")
                        .Append((camRenderSum[i] * toMs).ToString("0.0")).Append(']');
                }
                camCullSum[i] = 0d;
                camRenderSum[i] = 0d;
            }
            VRLog.Info(text.ToString());
            loopTotal = 0d;
            loopGap = 0d;
            renderSum = 0d;
            beforeRenderCalls = 0;
            text.Length = 0;
            text.Append("perf: engine stages ms/frame (top 6):");
            for (int rank = 0; rank < 6; rank++)
            {
                int best = -1;
                for (int k = 0; k < loopLast; k++)
                {
                    if (loopSum[k] > 0d && (best < 0 || loopSum[k] > loopSum[best]))
                    {
                        best = k;
                    }
                }
                if (best < 0)
                {
                    break;
                }
                text.Append(' ').Append(loopLabels[best]).Append(' ').Append((loopSum[best] * toMs).ToString("0.0"));
                loopSum[best] = 0d;
            }
            VRLog.Info(text.ToString());
            Array.Clear(loopSum, 0, loopSum.Length);
            loopFrames = 0;
        }

        private void OnEnable()
        {
            Application.onBeforeRender += OnBeforeRender;
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRender;
            Camera.onPreCull -= OnPreCull;
            Camera.onPreRender -= OnPreRender;
            Camera.onPostRender -= OnPostRender;
        }

        private static void OnPreRender(Camera camera)
        {
            int slot = CameraSlot(camera);
            if (slot >= 0)
            {
                long now = Stopwatch.GetTimestamp();
                camRenderStart[slot] = now;
                if (camCullStart[slot] != 0L)
                {
                    camCullSum[slot] += now - camCullStart[slot];
                }
            }
        }

        private static void OnPreCull(Camera camera)
        {
            int cullSlot = CameraSlot(camera);
            if (cullSlot >= 0)
            {
                camCullStart[cullSlot] = Stopwatch.GetTimestamp();
            }
            if (renderBeginFrame != Time.frameCount)
            {
                renderBeginFrame = Time.frameCount;
                renderBegin = Stopwatch.GetTimestamp();
            }
        }

        private static void OnPostRender(Camera camera)
        {
            renderEnd = Stopwatch.GetTimestamp();
            int slot = CameraSlot(camera);
            if (slot >= 0 && camRenderStart[slot] != 0L)
            {
                camRenderSum[slot] += renderEnd - camRenderStart[slot];
            }
            if (renderBeginFrame == Time.frameCount && renderEnd > renderBegin)
            {
                renderSum += renderEnd - renderBegin;
                renderBegin = renderEnd; // count each camera's span once, up to the latest PostRender
            }
        }

        // Main-thread time from the start of Update to just before rendering starts: what the game's scripts and
        // our own take. The rest of the frame is rendering / waiting for the GPU or the compositor.
        private void OnBeforeRender()
        {
            beforeRenderCalls++;
            if (frameStart != 0L)
            {
                preRenderTicks += Stopwatch.GetTimestamp() - frameStart;
            }
        }

        private void Update()
        {
            frameStart = Stopwatch.GetTimestamp();
            HandleSwitches();
            float dt = Time.unscaledDeltaTime;
            frames++;
            frameSum += dt;
            if (dt > frameMax)
            {
                frameMax = dt;
            }

            if (Time.unscaledTime < nextReport)
            {
                return;
            }
            if (nextReport == 0f)
            {
                nextReport = Time.unscaledTime + ReportSeconds;
                collections = GC.CollectionCount(0);
                heap = GC.GetTotalMemory(false);
                Reset();
                return;
            }
            nextReport = Time.unscaledTime + ReportSeconds;

            double toMs = 1000.0 / Stopwatch.Frequency / Math.Max(1, frames);
            text.Length = 0;
            text.Append("perf: ").Append((frames / frameSum).ToString("0.0")).Append(" fps, frame ")
                .Append((frameSum / frames * 1000f).ToString("0.0")).Append(" ms (max ").Append((frameMax * 1000f).ToString("0.0"))
                .Append("), mod scripts per frame:");
            double total = 0;
            for (int i = 0; i < Count; i++)
            {
                double ms = ticks[i] * toMs;
                total += ms;
                text.Append(' ').Append(Names[i]).Append(' ').Append(ms.ToString("0.00"));
            }
            long nowHeap = GC.GetTotalMemory(false);
            int alive = 0;
            for (int i = 0; i < itemParticles.Length; i++)
            {
                if (itemParticles[i] != null)
                {
                    alive += itemParticles[i].particleCount;
                }
            }
            text.Append(" [item particles alive ").Append(alive).Append(']');
            text.Append(" = ").Append(total.ToString("0.00")).Append(" ms; GC gen0 +").Append(GC.CollectionCount(0) - collections)
                .Append(", heap ").Append(((nowHeap - heap) / 1048576.0).ToString("+0.0;-0.0")).Append(" MB; item hand ")
                .Append(VRArms.ItemHand).Append(", cameras ").Append(Camera.allCamerasCount).Append('.');
            text.Insert(text.ToString().IndexOf(", mod scripts", StringComparison.Ordinal),
                ", scripts before render " + (preRenderTicks * toMs).ToString("0.0") + " ms");
            AppendGpu();
            VRLog.Info(text.ToString());
            LogLoop();
            LogCamerasIfChanged();

            collections = GC.CollectionCount(0);
            heap = nowHeap;
            Reset();
        }

        private void Reset()
        {
            frames = 0;
            frameSum = 0f;
            frameMax = 0f;
            Array.Clear(ticks, 0, Count);
            preRenderTicks = 0L;
        }

        // GPU time of the latest frame, if the player has frame timing stats on (0 = not available).
        private void AppendGpu()
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                {
                    text.Append(" frame timing: cpu ").Append(timings[0].cpuFrameTime.ToString("0.0")).Append(" main ")
                        .Append(timings[0].cpuMainThreadFrameTime.ToString("0.0")).Append(" render ")
                        .Append(timings[0].cpuRenderThreadFrameTime.ToString("0.0")).Append(" gpu ")
                        .Append(timings[0].gpuFrameTime.ToString("0.0")).Append(" ms.");
                }
            }
            catch (Exception)
            {
            }
        }

        private void LogCamerasIfChanged()
        {
            if (Camera.allCamerasCount == lastCameraCount)
            {
                return;
            }
            lastCameraCount = Camera.allCamerasCount;
            text.Length = 0;
            text.Append("perf: cameras now ").Append(lastCameraCount).Append(':');
            foreach (Camera camera in Camera.allCameras)
            {
                text.Append(" [").Append(camera.name).Append(" depth ").Append(camera.depth.ToString("0.#"))
                    .Append(camera.targetTexture != null ? " rt " + camera.targetTexture.width + "x" + camera.targetTexture.height : " screen")
                    .Append(" mask ").Append(camera.cullingMask).Append(']');
            }
            VRLog.Info(text.ToString());
        }

        // RightCtrl + keypad 1-4: switch single suspects off at runtime to see which one costs the frame time.
        private static void HandleSwitches()
        {
            if (!Input.GetKey(KeyCode.RightControl))
            {
                return;
            }
            Transform model = SDG.Unturned.Player.player != null && SDG.Unturned.Player.player.equipment != null
                ? SDG.Unturned.Player.player.equipment.firstModel : null;

            if (Input.GetKeyDown(KeyCode.Keypad1))
            {
                VRDebugFlags.PerfNoViewmodel = !VRDebugFlags.PerfNoViewmodel;
                VRLog.Info("perf switch: viewmodel layer drawn by the VR camera = " + !VRDebugFlags.PerfNoViewmodel);
            }
            if (Input.GetKeyDown(KeyCode.Keypad2))
            {
                VRDebugFlags.PerfNoItemPose = !VRDebugFlags.PerfNoItemPose;
                VRLog.Info("perf switch: item follows the controller = " + !VRDebugFlags.PerfNoItemPose);
            }
            if (Input.GetKeyDown(KeyCode.Keypad3) && model != null)
            {
                VRDebugFlags.PerfHideItem = !VRDebugFlags.PerfHideItem;
                foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.enabled = !VRDebugFlags.PerfHideItem;
                }
                VRLog.Info("perf switch: item renderers visible = " + !VRDebugFlags.PerfHideItem);
            }
            if (Input.GetKeyDown(KeyCode.Keypad4) && model != null)
            {
                VRDebugFlags.PerfNoEffects = !VRDebugFlags.PerfNoEffects;
                foreach (ParticleSystem particles in model.GetComponentsInChildren<ParticleSystem>(true))
                {
                    particles.gameObject.SetActive(!VRDebugFlags.PerfNoEffects);
                }
                foreach (Light light in model.GetComponentsInChildren<Light>(true))
                {
                    light.gameObject.SetActive(!VRDebugFlags.PerfNoEffects);
                }
                VRLog.Info("perf switch: item particles and lights on = " + !VRDebugFlags.PerfNoEffects);
            }
        }

        private static ParticleSystem[] itemParticles = new ParticleSystem[0];

        private static string PathOf(Transform t, Transform root)
        {
            string path = t.name;
            for (Transform parent = t.parent; parent != null && parent != root.parent; parent = parent.parent)
            {
                path = parent.name + "/" + path;
            }
            return path;
        }

        /// <summary>One-off scene cost of an equipped item model: what it adds to the renderer.</summary>
        public static void LogModel(Transform model)
        {
            if (!VRDebugFlags.LogPerf || model == null)
            {
                return;
            }

            int renderers = 0;
            int skinned = 0;
            int casters = 0;
            long vertices = 0;
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(false))
            {
                renderers++;
                if (renderer is SkinnedMeshRenderer s)
                {
                    skinned++;
                    if (s.sharedMesh != null) vertices += s.sharedMesh.vertexCount;
                }
                else if (renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
                {
                    vertices += filter.sharedMesh.vertexCount;
                }
                if (renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) casters++;
            }

            int lights = 0;
            int enabledLights = 0;
            foreach (Light light in model.GetComponentsInChildren<Light>(true))
            {
                lights++;
                if (light.enabled && light.gameObject.activeInHierarchy) enabledLights++;
            }
            itemParticles = model.GetComponentsInChildren<ParticleSystem>(true);
            int particles = itemParticles.Length;
            foreach (ParticleSystem system in itemParticles)
            {
                ParticleSystem.MainModule main = system.main;
                VRLog.Info("perf: particle system \"" + PathOf(system.transform, model) + "\": space " + main.simulationSpace + ", scaling " + main.scalingMode
                    + ", max " + main.maxParticles + ", rate/s " + system.emission.rateOverTime.constant + ", rate/m " + system.emission.rateOverDistance.constant
                    + ", lifetime " + main.startLifetime.constant + ", playing " + system.isPlaying + ", active " + system.gameObject.activeInHierarchy
                    + ", sub-emitters " + system.subEmitters.enabled + ", trails " + system.trails.enabled + ", collision " + system.collision.enabled + ".");
            }
            VRLog.Info("perf: item \"" + model.name + "\": " + renderers + " renderers (" + skinned + " skinned, " + casters + " cast shadows), "
                + vertices + " vertices, " + enabledLights + "/" + lights + " lights on, " + particles + " particle systems.");
        }
    }
}
