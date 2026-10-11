using SDG.Unturned;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SpatialTracking;

namespace UnturnedVR.VR
{
    /// <summary>
    /// M3: head-tracked VR camera. Confirmed in docs/unturned-internals.md that
    /// SDG.Unturned.MainCamera.instance sits under the local player's "First" anchor
    /// (Player.first, i.e. transform.Find("First")) in first-person, with PlayerLook writing
    /// pitch/position directly onto MainCamera's transform every frame and yaw onto the player
    /// root transform. Rather than fight those writes (VHVR's exact problem on Valheim's
    /// GameCamera, and the UUVR test's head-tracking failure - see HANDOFF.md §2/§3), we build a
    /// separate camera rig parented to the same "First" anchor. It is a sibling of MainCamera,
    /// never touched by PlayerLook, so it only inherits body yaw from the parent hierarchy; head
    /// pitch/roll/position come from a TrackedPoseDriver on the HMD camera child. MainCamera itself is just disabled (not
    /// destroyed) so PlayerLook's per-frame writes stay harmless.
    /// </summary>
    internal static class VRRig
    {
        private const string RigObjectName = "UnturnedVR Rig";
        private const string HmdObjectName = "UnturnedVR HMD";

        private static GameObject rigObject;
        private static Camera hmdCamera;
        private static VRRigHeight rigHeight;
        private static Transform hmdTransform;

        /// <summary>True while the rig hangs on the menu camera (no local player yet).</summary>
        public static bool InMenu { get; private set; } = true;

        public static bool HasHeadPose => rigHeight != null && rigHeight.HasPose;

        /// <summary>Rig root: tracking-space poses (head, controllers) are local to it. Null before the rig exists.</summary>
        public static Transform RigTransform => rigObject != null ? rigObject.transform : null;

        /// <summary>The VR camera while the rig exists and is live, otherwise null (menu, loading).</summary>
        public static Camera ActiveCamera => hmdCamera != null && hmdCamera.isActiveAndEnabled ? hmdCamera : null;
        private static bool hasLastHeadYaw;
        private static float lastHeadYaw;
        private static float rigYawOffset;

        public static void AttachToLocalPlayer(Player player)
        {
            if (player == null || player.first == null)
            {
                VRLog.Error("local player or player.first is null, cannot attach.");
                return;
            }

            Camera mainCamera = MainCamera.instance;
            if (mainCamera == null)
            {
                VRLog.Error("MainCamera.instance is null, cannot attach.");
                return;
            }

            Camera vrCamera = EnsureRigObject();

            CopyCameraSettings(mainCamera, vrCamera);

            rigObject.transform.SetParent(player.first, worldPositionStays: false);
            rigObject.transform.localPosition = Vector3.zero;
            rigObject.transform.localRotation = Quaternion.identity;
            hasLastHeadYaw = false; // re-align view with the body on (re)spawn
            rigYawOffset = 0f;
            InMenu = false;

            DisableMainCamera(mainCamera);

            VRLog.Info("attached VR camera rig to local player's head anchor.");
        }

        /// <summary>
        /// The main menu renders through MainCamera too, but nothing applies the headset pose to it, so
        /// the view (and any UI placed in the world) is glued to the head. Hang the same head-tracked rig
        /// on the menu camera, which also follows its scripted pans. Called until the rig exists; the
        /// in-game attach later moves the same rig onto the player.
        /// </summary>
        public static void AttachToMenuCamera()
        {
            Camera mainCamera = MainCamera.instance;
            if (mainCamera == null)
            {
                return;
            }

            Camera vrCamera = EnsureRigObject();
            CopyCameraSettings(mainCamera, vrCamera);

            rigObject.transform.SetParent(mainCamera.transform, worldPositionStays: false);
            rigObject.transform.localPosition = Vector3.zero;
            rigObject.transform.localRotation = Quaternion.identity;
            hasLastHeadYaw = false;
            rigYawOffset = 0f;
            InMenu = true;

            DisableMainCamera(mainCamera);

            VRLog.Info("attached VR camera rig to the menu camera.");
        }

        /// <summary>
        /// How many degrees the headset turned (yaw, in the rig's tracking space) since the last call.
        /// When <paramref name="applyToBody"/> the rig is counter-rotated by the same amount, and the
        /// caller must add the returned value to the player's yaw: the view stays at the physical head
        /// yaw while the body follows it. Returns 0 until a head pose exists.
        /// </summary>
        public static float ConsumeHeadYawDelta(bool applyToBody)
        {
            if (rigObject == null || hmdTransform == null)
            {
                return 0f;
            }

            Vector3 forward = hmdTransform.localRotation * Vector3.forward;
            if (forward.x * forward.x + forward.z * forward.z < 0.04f)
            {
                return 0f; // looking almost straight up/down: yaw is ill-defined, hold.
            }

            float headYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            if (!hasLastHeadYaw)
            {
                // First pose: align the view with the body, so you start facing where the body faces.
                hasLastHeadYaw = true;
                lastHeadYaw = headYaw;
                rigYawOffset = -headYaw;
                rigObject.transform.localRotation = Quaternion.Euler(0f, rigYawOffset, 0f);
                return 0f;
            }

            float delta = Mathf.DeltaAngle(lastHeadYaw, headYaw);
            lastHeadYaw = headYaw;
            if (!applyToBody)
            {
                return 0f;
            }

            rigYawOffset -= delta;
            rigObject.transform.localRotation = Quaternion.Euler(0f, rigYawOffset, 0f);
            return delta;
        }

        /// <summary>
        /// The headset's pitch in the game's convention (0 = straight up, 90 = level, 180 = straight down),
        /// measured against the world so the body yaw and the rig's counter-rotation do not matter. False until
        /// the rig is live and has a head pose.
        /// </summary>
        public static bool TryGetHeadPitch(out float pitch)
        {
            pitch = 90f;
            Camera eye = ActiveCamera;
            if (eye == null || !HasHeadPose)
            {
                return false;
            }

            float up = Mathf.Clamp(eye.transform.forward.y, -1f, 1f);
            pitch = 90f - Mathf.Asin(up) * Mathf.Rad2Deg;
            return true;
        }

        private static Camera EnsureRigObject()
        {
            if (rigObject != null)
            {
                return rigObject.GetComponentInChildren<Camera>(true);
            }

            rigObject = new GameObject(RigObjectName);

            // Camera lives on a child so TrackedPoseDriver can own its local pose while the rig root
            // carries the eye-height offset (VRRigHeight). Same shape as VHVR's SteamVR Player prefab.
            GameObject hmdObject = new GameObject(HmdObjectName);
            hmdObject.transform.SetParent(rigObject.transform, worldPositionStays: false);

            Camera vrCamera = hmdObject.AddComponent<Camera>();
            hmdObject.AddComponent<AudioListener>();

            TrackedPoseDriver driver = hmdObject.AddComponent<TrackedPoseDriver>();
            driver.SetPoseSource(TrackedPoseDriver.DeviceType.GenericXRDevice, TrackedPoseDriver.TrackedPose.Center);
            driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            driver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            driver.UseRelativeTransform = true;

            hmdCamera = vrCamera;
            hmdTransform = hmdObject.transform;
            hasLastHeadYaw = false;
            rigYawOffset = 0f;
            rigHeight = rigObject.AddComponent<VRRigHeight>();
            rigHeight.Hmd = hmdObject.transform;
            if (VRDebugFlags.SessionMonitor)
            {
                rigObject.AddComponent<VRSessionMonitor>();
            }
            hmdObject.AddComponent<VROpenVRPoseFallback>();
            VRHands.Create(rigObject.transform);
#if UNITY_EDITOR
            rigObject.AddComponent<VRDebugKeys>();
#endif

            VRLog.Info("created VR camera rig GameObject.");
            return vrCamera;
        }

        /// <summary>
        /// GraphicsSettings applies the render path (deferred/forward), HDR, MSAA, far clip and cull
        /// distances to MainCamera only, and can do so after our one-time copy or when the player
        /// changes settings in game. Mirror them onto the VR camera every frame (setters only on change).
        /// </summary>
        public static void SyncGraphicsSettings()
        {
            Camera from = MainCamera.instance;
            Camera to = hmdCamera;
            if (from == null || to == null)
            {
                return;
            }

            VRHighlighting.Sync(from, to);

            // The menu camera's mask and clear settings change between menu pages / loading.
            int mask = from.cullingMask & ~(1 << Mathf.Max(VRGui.PanelLayer, 0));
            if (VRGui.PanelLayer < 0)
            {
                mask = from.cullingMask;
            }
            if (VRArms.Active && !VRDebugFlags.PerfNoViewmodel)
            {
                mask |= 1 << LayerMasks.VIEWMODEL; // the arms/equipped item, drawn in the world by this camera
            }
            MirrorSettings(from, to, mask);
        }

        /// <summary>
        /// Copies the settings the game applies to MainCamera only (render path, HDR, MSAA, clip planes, clear mode,
        /// cull distances) onto another camera; setters only on change. <paramref name="mask"/> is the culling mask
        /// the target should have. Used for the VR camera and for the binoculars' zoom camera (VRBinoculars).
        /// </summary>
        internal static void MirrorSettings(Camera from, Camera to, int mask)
        {
            if (to.renderingPath != from.renderingPath)
            {
                to.renderingPath = from.renderingPath;
                VRLog.Info("VR camera renderingPath -> " + from.renderingPath);
            }
            if (to.allowHDR != from.allowHDR)
            {
                to.allowHDR = from.allowHDR;
            }
            if (to.allowMSAA != from.allowMSAA)
            {
                to.allowMSAA = from.allowMSAA;
            }
            if (to.cullingMask != mask)
            {
                to.cullingMask = mask;
            }
            if (to.clearFlags != from.clearFlags)
            {
                to.clearFlags = from.clearFlags;
            }
            if (to.backgroundColor != from.backgroundColor)
            {
                to.backgroundColor = from.backgroundColor;
            }
            if (to.nearClipPlane != from.nearClipPlane)
            {
                to.nearClipPlane = from.nearClipPlane;
            }
            if (to.farClipPlane != from.farClipPlane)
            {
                to.farClipPlane = from.farClipPlane;
            }
            if (to.layerCullSpherical != from.layerCullSpherical)
            {
                to.layerCullSpherical = from.layerCullSpherical;
            }

            float[] distances = from.layerCullDistances;
            if (!SameDistances(distances, to.layerCullDistances))
            {
                to.layerCullDistances = distances;
            }
        }

        private static bool SameDistances(float[] a, float[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        // FOV is deliberately not copied: once this camera is the one XR renders in stereo, the
        // XR loader overrides its projection per eye from the headset, making Camera.fieldOfView
        // irrelevant - see the FOV open design question in docs/unturned-internals.md, resolved
        // by not reusing MainCamera's Camera object at all.
        private static void CopyCameraSettings(Camera from, Camera to)
        {
            to.nearClipPlane = from.nearClipPlane;
            to.farClipPlane = from.farClipPlane;
            to.clearFlags = from.clearFlags;
            to.cullingMask = from.cullingMask;
            to.renderingPath = from.renderingPath;
            to.depthTextureMode = from.depthTextureMode;
            to.backgroundColor = from.backgroundColor;
            to.allowHDR = from.allowHDR;
            to.allowMSAA = from.allowMSAA; // Unturned sets false; ours defaults to true
            to.allowDynamicResolution = from.allowDynamicResolution;
            to.opaqueSortMode = from.opaqueSortMode;
            to.transparencySortMode = from.transparencySortMode;
            to.clearStencilAfterLightingPass = from.clearStencilAfterLightingPass;
            to.layerCullDistances = from.layerCullDistances;
            to.layerCullSpherical = from.layerCullSpherical;
            to.useOcclusionCulling = from.useOcclusionCulling;

            CopyPostProcessLayer(from, to);
            VRHighlighting.LogCameraComponents(from);
            VRHighlighting.Sync(from, to);
        }

        // MainCamera carries a PostProcessLayer (deferred fog and the base lighting/post effects,
        // see UnturnedPostProcess.setBaseCamera); without a layer on the VR camera those effects
        // are missing. Global volumes still apply since the new layer uses the same volume layer.
        internal static void CopyPostProcessLayer(Camera from, Camera to)
        {
            PostProcessLayer source = from.GetComponent<PostProcessLayer>();
            if (source == null || to.GetComponent<PostProcessLayer>() != null)
            {
                return;
            }

            // PostProcessLayer.OnEnable needs its resources, so configure it while inactive.
            GameObject target = to.gameObject;
            bool wasActive = target.activeSelf;
            target.SetActive(false);

            PostProcessLayer layer = target.AddComponent<PostProcessLayer>();
            PostProcessResources resources = (PostProcessResources)typeof(PostProcessLayer)
                .GetField("m_Resources", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(source);
            layer.Init(resources);

            layer.volumeTrigger = target.transform;
            layer.volumeLayer = source.volumeLayer;
            layer.antialiasingMode = source.antialiasingMode;
            layer.stopNaNPropagation = source.stopNaNPropagation;
            layer.fog.enabled = source.fog.enabled;
            layer.fog.excludeSkybox = source.fog.excludeSkybox;

            target.SetActive(wasActive);

            VRLog.Info("copied PostProcessLayer to VR camera (resources " + (resources != null ? "found" : "MISSING") + ").");
        }

        private static void DisableMainCamera(Camera mainCamera)
        {
            if (!mainCamera.enabled)
            {
                return;
            }

            mainCamera.enabled = false;

            AudioListener mainCameraListener = mainCamera.GetComponent<AudioListener>();
            if (mainCameraListener != null)
            {
                mainCameraListener.enabled = false;
            }

            VRLog.Info("disabled MainCamera (PlayerLook keeps writing to its transform harmlessly).");
        }
    }
}
