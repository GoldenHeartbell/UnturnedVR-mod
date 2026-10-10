using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// M4: shows Unturned's UI in the headset. Unturned's default Glazier backend (Glazier_uGUI,
    /// see GlazierFactory) puts everything on one "Glazier" Canvas in ScreenSpaceOverlay mode, which
    /// Unity only draws to the flat window, never to XR eye cameras. Same idea as VHVR's VRGUI:
    ///   1. switch that canvas to ScreenSpaceCamera, rendered by a private UI camera into a RenderTexture,
    ///   2. show the texture on a quad floating in front of the player,
    ///   3. draw the quad with an overlay camera (clears depth only) so walls never hide it.
    /// The RenderTexture matches Screen.width/height, because Glazier maps the mouse with Screen
    /// size, so the flat-screen mouse cursor still lines up with what is drawn.
    /// Works in the menu too: the overlay camera is parented to whichever camera currently feeds the
    /// headset (VR rig camera in game, MainCamera in the menu).
    /// </summary>
    internal class VRGui : MonoBehaviour
    {
        // Guesses to tune in-headset.
        private const float PanelDistance = 2.0f;
        private const float PanelWidth = 2.4f;
        // Menu / loading screen: bigger, and fixed to the camera instead of following the head.
        private const float MenuPanelDistance = 2.2f;
        private const float MenuPanelWidth = 3.6f;
        private const float MenuReanchorDistance = 1.5f;
        private const float FollowDeadzoneDegrees = 35f;
        private const float FollowSharpness = 3f;
        private const KeyCode RecenterKey = KeyCode.F10;
        // The pickup label panel, head-locked in front of the eyes. Guesses to tune in-headset.
        private const float HintDistance = 1.0f;
        private const float HintHeight = -0.12f;
        private const float HintScale = 1.5f;

        /// <summary>Layer the GUI panel lives on, or -1 before it is chosen.</summary>
        public static int PanelLayer { get; private set; } = -1;

        /// <summary>The quad showing the UI (unit quad scaled to the panel size, visible from -Z), or null before it exists.</summary>
        public static Transform PanelTransform { get; private set; }

        /// <summary>
        /// The panel the laser should be intersected with: the attachment menu panel on the gun while it is shown,
        /// else the main panel.
        /// </summary>
        public static Transform PointerPanel => attachShown && attachPanel != null ? attachPanel : PanelTransform;

        /// <summary>The part of the UI texture the pointer panel shows (u/v, origin bottom left).</summary>
        public static Rect PointerUv => attachShown ? attachUv : new Rect(0f, 0f, 1f, 1f);

        private static Transform attachPanel;
        private static bool attachShown;
        private static Rect attachUv = new Rect(0f, 0f, 1f, 1f);

        /// <summary>The camera that renders the Glazier canvas into the UI texture, or null before it exists.</summary>
        public static Camera UiCamera { get; private set; }

        /// <summary>Size of the Glazier canvas in its own units (what the widgets' offsets are measured in).</summary>
        public static Vector2 CanvasSize { get; private set; } = new Vector2(1920f, 1080f);

        // Extra canvas units around the hint boxes so their border / drop shadow is cropped and hidden too.
        private const float HintPad = 8f;

        // The game's "looking at another player" card; private, so read by reflection (resolved once).
        private static readonly System.Reflection.FieldInfo messagePlayerField =
            typeof(PlayerUI).GetField("messagePlayer", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        // Bounding rect (canvas units, origin top left) of every hint / message box the game currently shows:
        // PlayerUI.messageBox (hint: pickup, climb, ...), messageBox2 (message / hint2: reload, safety, xp, ...) and the
        // enemy player card. The game keeps them at fixed bottom-centre spots (PlayerUIHintPatch pins messageBox).
        private static bool TryGetHintRect(out Rect rect)
        {
            rect = default;
            bool any = false;
            Vector2 canvasSize = CanvasSize;
            AddHintBox(PlayerUI.messageBox, canvasSize, ref rect, ref any);
            AddHintBox(PlayerUI.messageBox2, canvasSize, ref rect, ref any);
            AddHintBox(messagePlayerField != null ? messagePlayerField.GetValue(null) as ISleekElement : null, canvasSize, ref rect, ref any);
            return any;
        }

        private static void AddHintBox(ISleekElement box, Vector2 canvasSize, ref Rect rect, ref bool any)
        {
            if (box == null || !box.IsVisible)
            {
                return;
            }

            float left = box.PositionScale_X * canvasSize.x + box.PositionOffset_X;
            float top = box.PositionScale_Y * canvasSize.y + box.PositionOffset_Y;
            float width = box.SizeScale_X * canvasSize.x + box.SizeOffset_X;
            float height = box.SizeScale_Y * canvasSize.y + box.SizeOffset_Y;
            Rect boxRect = Rect.MinMaxRect(left - HintPad, top - HintPad, left + width + HintPad, top + height + HintPad);
            if (!any)
            {
                rect = boxRect;
                any = true;
                return;
            }

            rect = Rect.MinMaxRect(
                Mathf.Min(rect.xMin, boxRect.xMin), Mathf.Min(rect.yMin, boxRect.yMin),
                Mathf.Max(rect.xMax, boxRect.xMax), Mathf.Max(rect.yMax, boxRect.yMax));
        }

        private Canvas canvas;
        private Camera uiCamera;
        private MeshFilter panelFilter;
        private Mesh quadMesh;
        private Mesh holeMesh;
        private Transform hintPanel;
        private Mesh hintMesh;
        private bool hintShown;
        private Rect hintShownRect;
        private bool loggedHint;
        private RenderTexture uiTexture;
        private Camera overlayCamera;
        private Transform loadingHead;
        private Transform panel;
        private Material panelMaterial;
        private int panelLayer = -1;

        private Camera eyeCamera;
        private float panelYaw;
        private bool needsRecenter = true;
        private bool standalone;
        private bool menuLayout = true;
        private bool menuPoseValid;
        private bool hadHeadPose = true;
        private bool menuRailed;
        private Vector3 menuLocalPosition;
        private Quaternion menuLocalRotation = Quaternion.identity;
        private Vector3 menuEyeAnchor;
        private Vector3 menuPanelPosition;
        private Quaternion menuPanelRotation;

        public static void Create()
        {
            GameObject go = new GameObject("UnturnedVR GUI");
            DontDestroyOnLoad(go);
            go.AddComponent<VRGui>();
        }

        private void LateUpdate()
        {
            long t = VRPerf.Begin();
            LateUpdateInner();
            VRPerf.End(VRPerf.Gui, t);
        }

        private void LateUpdateInner()
        {
            if (!VRState.IsActive)
            {
                return;
            }

            // Menu: give the menu camera the same head-tracked rig the game uses.
            if (VRRig.ActiveCamera == null && MainCamera.instance != null)
            {
                VRRig.AttachToMenuCamera();
            }

            if (canvas == null && !TryAttachToCanvas())
            {
                return;
            }

            // The overlay camera hangs on the eye camera, which the game destroys on every scene load
            // (MainCamera.Awake runs again for the world), taking the overlay camera with it.
            if (overlayCamera == null)
            {
                CreateOverlayCamera();
                eyeCamera = null;
                standalone = false;
                VRLog.Info("overlay camera was destroyed with its parent camera; recreated.");
            }

            if (uiTexture.width != Screen.width || uiTexture.height != Screen.height)
            {
                RecreateTexture();
            }
            CanvasSize = ((RectTransform)canvas.transform).rect.size;

            Camera eye = PickEyeCamera();
            LogDiagnostics(eye);
            if (eye == null)
            {
                // Startup / loading screen: no game camera feeds the headset yet, so the overlay camera
                // becomes the only camera and clears to a dark background behind the panel.
                if (!standalone)
                {
                    standalone = true;
                    eyeCamera = null;
                    // Nothing else applies the headset pose here, so without a tracked parent the camera (and
                    // with it the panel) would be glued to the head.
                    EnsureLoadingHead().gameObject.SetActive(true);
                    overlayCamera.transform.SetParent(loadingHead, worldPositionStays: false);
                    overlayCamera.transform.localPosition = Vector3.zero;
                    overlayCamera.transform.localRotation = Quaternion.identity;
                    overlayCamera.clearFlags = CameraClearFlags.SolidColor;
                    overlayCamera.backgroundColor = new Color(0.05f, 0.05f, 0.05f, 1f);
                    needsRecenter = true;
                }

                if (Input.GetKeyDown(RecenterKey))
                {
                    needsRecenter = true;
                }
                if (attachShown)
                {
                    attachShown = false; // a scene change ended the menu: the big panel must come back
                    attachPanel.gameObject.SetActive(false);
                }
                SetPanelVisible(true);
                PlacePanel(overlayCamera.transform);
                UpdateHint(null);
                return;
            }

            if (standalone)
            {
                standalone = false;
                overlayCamera.clearFlags = CameraClearFlags.Depth;
                if (loadingHead != null)
                {
                    loadingHead.gameObject.SetActive(false);
                }
            }

            if (eye != eyeCamera)
            {
                eyeCamera = eye;
                overlayCamera.transform.SetParent(eye.transform, worldPositionStays: false);
                overlayCamera.transform.localPosition = Vector3.zero;
                overlayCamera.transform.localRotation = Quaternion.identity;
                needsRecenter = true;
            }

            // The panel layer must not be drawn by the eye camera itself (the overlay camera does it).
            eye.cullingMask &= ~(1 << panelLayer);

            if (Input.GetKeyDown(RecenterKey))
            {
                needsRecenter = true;
            }

            UpdateAttachPanel(eye.transform);
            SetPanelVisible(true);
            PlacePanel(eye.transform);
            UpdateHint(eye.transform);
        }

        /// <summary>
        /// While the gun's attachment menu is open it is shown on its own small panel floating above the gun
        /// (VRAttachMenu), cropped from the UI texture; the big panel is hidden then (the HUD is closed in that mode).
        /// </summary>
        private void UpdateAttachPanel(Transform eye)
        {
            // The pose cached by VRArms once the gun sat on the controller; reading the hooks here (before VRArms in the
            // frame) would give the game's head-relative animated hold.
            bool show = VRAttachMenu.Open && VRAttachMenu.CachedValid;
            Vector3 position = VRAttachMenu.CachedPosition;
            Quaternion rotation = VRAttachMenu.CachedRotation;

            if (show != attachShown)
            {
                attachShown = show;
                attachPanel.gameObject.SetActive(show);
                if (show)
                {
                    attachPanel.position = position;
                    attachPanel.rotation = rotation;
                    VRLog.Info("attach menu panel shown on the gun.");
                }
            }
            if (!show)
            {
                return;
            }

            Rect rect = VRAttachMenu.CanvasRect;
            float u0 = rect.xMin;
            float u1 = rect.xMax;
            float v0 = 1f - rect.yMax;
            float v1 = 1f - rect.yMin;
            attachUv = Rect.MinMaxRect(u0, v0, u1, v1);

            float aspect = ((u1 - u0) * uiTexture.width) / Mathf.Max(1f, (v1 - v0) * uiTexture.height);
            attachPanel.localScale = new Vector3(VRAttachMenu.PanelWidth, VRAttachMenu.PanelWidth / aspect, 1f);

            // Follows the gun with a little smoothing, so the panel does not shake with the hand.
            float t = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
            attachPanel.position = Vector3.Lerp(attachPanel.position, position, t);
            attachPanel.rotation = Quaternion.Slerp(attachPanel.rotation, rotation, t);
        }

        /// <summary>
        /// Shows the game's hints and messages (pickup label, climb, reload prompt, xp, ...) on the near panel and cuts
        /// their spot out of the HUD panel, only in the in-game HUD layout and only while one of the boxes is visible.
        /// Otherwise the HUD panel is whole.
        /// </summary>
        private void UpdateHint(Transform eye)
        {
            Rect hintRect = default;
            bool show = eye != null && !menuLayout && TryGetHintRect(out hintRect);

            if (show != hintShown || (show && hintRect != hintShownRect))
            {
                hintShown = show;
                if (show)
                {
                    hintShownRect = hintRect;
                    ApplyHintGeometry(hintRect);
                    if (!loggedHint)
                    {
                        loggedHint = true;
                        VRLog.Info("hint panel: rect " + hintRect + " in canvas " + CanvasSize + ".");
                    }
                }
                else
                {
                    panelFilter.sharedMesh = quadMesh;
                }
            }

            if (hintPanel.gameObject.activeSelf != show)
            {
                hintPanel.gameObject.SetActive(show);
            }
            if (show)
            {
                hintPanel.position = eye.TransformPoint(0f, HintHeight, HintDistance);
                hintPanel.rotation = eye.rotation;
            }
        }

        private void ApplyHintGeometry(Rect rect)
        {
            Vector2 canvasSize = CanvasSize;
            float u0 = Mathf.Clamp01(rect.xMin / canvasSize.x);
            float u1 = Mathf.Clamp01(rect.xMax / canvasSize.x);
            float v1 = Mathf.Clamp01(1f - rect.yMin / canvasSize.y);
            float v0 = Mathf.Clamp01(1f - rect.yMax / canvasSize.y);

            // HUD panel: the whole texture except the label's rectangle.
            var vertices = new System.Collections.Generic.List<Vector3>(16);
            var uvs = new System.Collections.Generic.List<Vector2>(16);
            var triangles = new System.Collections.Generic.List<int>(24);
            AddRect(vertices, uvs, triangles, 0f, 0f, 1f, v0);
            AddRect(vertices, uvs, triangles, 0f, v1, 1f, 1f);
            AddRect(vertices, uvs, triangles, 0f, v0, u0, v1);
            AddRect(vertices, uvs, triangles, u1, v0, 1f, v1);
            holeMesh.Clear();
            holeMesh.SetVertices(vertices);
            holeMesh.SetUVs(0, uvs);
            holeMesh.SetTriangles(triangles, 0);
            holeMesh.RecalculateNormals();
            holeMesh.RecalculateBounds();
            panelFilter.sharedMesh = holeMesh;

            // Near panel: only the label's rectangle, sized to look like the HUD scaled down to HintDistance.
            hintMesh.uv = new[] { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u0, v1), new Vector2(u1, v1) };
            float aspect = (float)uiTexture.width / uiTexture.height;
            float scale = PanelWidth * (HintDistance / PanelDistance) * HintScale;
            hintPanel.localScale = new Vector3(
                (u1 - u0) * scale,
                (v1 - v0) * scale / aspect,
                1f);
        }

        // Adds the rectangle (u0,v0)-(u1,v1) of the unit quad, UVs equal to the position in the texture.
        private static void AddRect(System.Collections.Generic.List<Vector3> vertices, System.Collections.Generic.List<Vector2> uvs,
            System.Collections.Generic.List<int> triangles, float u0, float v0, float u1, float v1)
        {
            if (u1 <= u0 || v1 <= v0)
            {
                return;
            }

            int start = vertices.Count;
            vertices.Add(new Vector3(u0 - 0.5f, v0 - 0.5f, 0f));
            vertices.Add(new Vector3(u1 - 0.5f, v0 - 0.5f, 0f));
            vertices.Add(new Vector3(u0 - 0.5f, v1 - 0.5f, 0f));
            vertices.Add(new Vector3(u1 - 0.5f, v1 - 0.5f, 0f));
            uvs.Add(new Vector2(u0, v0));
            uvs.Add(new Vector2(u1, v0));
            uvs.Add(new Vector2(u0, v1));
            uvs.Add(new Vector2(u1, v1));
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 1);
            triangles.Add(start + 2); triangles.Add(start + 3); triangles.Add(start + 1);
        }

        // Off by default (VRDebugFlags.LogGui): reports what the pipeline is doing every 10 s, incl. a full ReadPixels.
        private void LogDiagnostics(Camera eye)
        {
            if (!VRDebugFlags.LogGui || Time.unscaledTime < nextDiagnosticTime)
            {
                return;
            }
            nextDiagnosticTime = Time.unscaledTime + 10f;

            string eyeInfo = eye == null
                ? "NONE (MainCamera " + (MainCamera.instance == null ? "null" : "enabled=" + MainCamera.instance.isActiveAndEnabled) + ", VR rig " + (VRRig.ActiveCamera == null ? "none" : "active") + ")"
                : "\"" + eye.name + "\" pos=" + eye.transform.position + " fwd=" + eye.transform.forward + " mask=" + eye.cullingMask + " stereo=" + eye.stereoTargetEye + " depth=" + eye.depth;

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = uiTexture;
            Texture2D probe = new Texture2D(uiTexture.width, uiTexture.height, TextureFormat.RGBA32, false);
            probe.ReadPixels(new Rect(0, 0, uiTexture.width, uiTexture.height), 0, 0);
            RenderTexture.active = previous;
            Color32[] pixels = probe.GetPixels32();
            Destroy(probe);
            int covered = 0;
            for (int i = 0; i < pixels.Length; i += 16)
            {
                if (pixels[i].a > 0)
                {
                    covered++;
                }
            }

            // Which cameras draw to the headset, and whether the head pose reaches the rig camera.
            var cameras = new System.Text.StringBuilder();
            foreach (Camera camera in Camera.allCameras)
            {
                if (camera.targetTexture == null)
                {
                    cameras.Append("[\"").Append(camera.name).Append("\" depth=").Append(camera.depth)
                        .Append(" stereo=").Append(camera.stereoTargetEye).Append(" parent=")
                        .Append(camera.transform.parent != null ? camera.transform.parent.name : "none").Append("] ");
                }
            }
            Transform rigTransform = VRRig.RigTransform;
            string rigInfo = rigTransform == null ? "no rig"
                : "rig parent=" + (rigTransform.parent != null ? rigTransform.parent.name : "none")
                    + " rigRot=" + rigTransform.rotation.eulerAngles + " hasHeadPose=" + VRRig.HasHeadPose
                    + " hmdLocalRot=" + (eye != null ? eye.transform.localRotation.eulerAngles.ToString() : "n/a");
            VRLog.Info("diag: cameras=" + cameras + "; " + rigInfo + "; inMenu=" + VRRig.InMenu);

            VRLog.Info("diag: eye=" + eyeInfo
                + "; panel " + (panel == null ? "null" : "active=" + panel.gameObject.activeSelf + " pos=" + panel.position + " scale=" + panel.localScale)
                + "; overlayCam enabled=" + (overlayCamera != null && overlayCamera.enabled) + " parent=" + (overlayCamera != null && overlayCamera.transform.parent != null ? overlayCamera.transform.parent.name : "none")
                + "; canvas mode=" + canvas.renderMode + " worldCam=" + (canvas.worldCamera != null) + " rect=" + ((RectTransform)canvas.transform).rect.size
                + "; texture " + uiTexture.width + "x" + uiTexture.height + " covered=" + (covered * 16 * 100L / pixels.Length) + "%"
                + "; material=" + (panelMaterial != null ? panelMaterial.shader.name : "null") + " layer=" + panelLayer);
        }

        private float nextDiagnosticTime;

        private void OnDestroy()
        {
            if (uiTexture != null)
            {
                uiTexture.Release();
            }
        }

        private bool TryAttachToCanvas()
        {
            GameObject glazier = GameObject.Find("Glazier");
            Canvas found = glazier != null ? glazier.GetComponent<Canvas>() : null;
            if (found == null)
            {
                return false; // Glazier not created yet (or not the uGUI backend); retry next frame.
            }

            panelLayer = FindFreeLayer();
            PanelLayer = panelLayer;
            if (panelLayer < 0)
            {
                VRLog.Error("no free layer for the UI panel.");
                enabled = false;
                return false;
            }

            RecreateTexture();

            GameObject cameraObject = new GameObject("UnturnedVR UI Camera");
            cameraObject.transform.SetParent(transform, worldPositionStays: false);
            uiCamera = cameraObject.AddComponent<Camera>();
            uiCamera.orthographic = true;
            uiCamera.clearFlags = CameraClearFlags.SolidColor;
            uiCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            // A ScreenSpaceCamera canvas is still subject to the camera's layer mask (Glazier objects are
            // on Default), so draw everything and keep world geometry out of view by parking the camera
            // far away: the near/far range only reaches the canvas plane.
            uiCamera.cullingMask = ~0;
            uiCamera.transform.position = new Vector3(0f, -20000f, 0f);
            uiCamera.nearClipPlane = 0.3f;
            uiCamera.farClipPlane = 10f;
            uiCamera.allowHDR = false;
            uiCamera.allowMSAA = false;
            uiCamera.depth = -100f;
            uiCamera.stereoTargetEye = StereoTargetEyeMask.None;
            uiCamera.targetTexture = uiTexture;
            UiCamera = uiCamera;

            canvas = found;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = uiCamera;
            canvas.planeDistance = 1f;

            CreateOverlayCamera();
            CreatePanel();

            VRLog.Info("Glazier canvas switched to ScreenSpaceCamera -> " + uiTexture.width + "x" + uiTexture.height + " texture, panel layer " + panelLayer + ".");
            return true;
        }

        private void RecreateTexture()
        {
            int width = Mathf.Max(Screen.width, 64);
            int height = Mathf.Max(Screen.height, 64);

            RenderTexture old = uiTexture;
            uiTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            uiTexture.name = "UnturnedVR GUI";
            uiTexture.useMipMap = false;
            uiTexture.filterMode = FilterMode.Bilinear;
            uiTexture.Create();

            if (uiCamera != null)
            {
                uiCamera.targetTexture = uiTexture;
            }
            if (panelMaterial != null)
            {
                panelMaterial.mainTexture = uiTexture;
            }
            if (panel != null)
            {
                ResizePanel();
            }
            if (old != null)
            {
                old.Release();
                Destroy(old);
            }
        }

        private void CreateOverlayCamera()
        {
            GameObject go = new GameObject("UnturnedVR GUI Overlay Camera");
            go.transform.SetParent(transform, worldPositionStays: false);
            overlayCamera = go.AddComponent<Camera>();
            overlayCamera.clearFlags = CameraClearFlags.Depth;
            overlayCamera.cullingMask = 1 << panelLayer;
            overlayCamera.nearClipPlane = 0.1f;
            overlayCamera.farClipPlane = 20f;
            overlayCamera.allowHDR = false;
            overlayCamera.allowMSAA = false;
            overlayCamera.depth = 100f; // after the eye camera
            overlayCamera.stereoTargetEye = StereoTargetEyeMask.Both;
        }

        // The head pose for the loading screen, when no game camera and no VR rig exist. Same pose sources
        // as the rig's HMD camera (TrackedPoseDriver, plus the direct OpenVR read when XR has no head
        // device); its parent is this object at the origin, so the local pose is the tracking-space pose.
        private Transform EnsureLoadingHead()
        {
            if (loadingHead == null)
            {
                GameObject go = new GameObject("UnturnedVR Loading Head");
                go.transform.SetParent(transform, worldPositionStays: false);

                UnityEngine.SpatialTracking.TrackedPoseDriver driver = go.AddComponent<UnityEngine.SpatialTracking.TrackedPoseDriver>();
                driver.SetPoseSource(UnityEngine.SpatialTracking.TrackedPoseDriver.DeviceType.GenericXRDevice,
                    UnityEngine.SpatialTracking.TrackedPoseDriver.TrackedPose.Center);
                driver.trackingType = UnityEngine.SpatialTracking.TrackedPoseDriver.TrackingType.RotationAndPosition;
                driver.updateType = UnityEngine.SpatialTracking.TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
                driver.UseRelativeTransform = true;
                go.AddComponent<VROpenVRPoseFallback>();

                loadingHead = go.transform;
            }
            return loadingHead;
        }

        private void CreatePanel()
        {
            Material source = Canvas.GetDefaultCanvasMaterial();
            panelMaterial = new Material(source);
            panelMaterial.mainTexture = uiTexture;

            GameObject go = new GameObject("UnturnedVR GUI Panel");
            go.layer = panelLayer;
            go.transform.SetParent(transform, worldPositionStays: false);
            quadMesh = BuildQuad();
            holeMesh = new Mesh { name = "UnturnedVR GUI Quad With Hint Hole" };
            panelFilter = go.AddComponent<MeshFilter>();
            panelFilter.sharedMesh = quadMesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = panelMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            panel = go.transform;
            PanelTransform = panel;
            ResizePanel();

            // Pickup label panel: same texture and layer, cropped to the label, hidden until needed.
            GameObject hint = new GameObject("UnturnedVR GUI Hint Panel");
            hint.layer = panelLayer;
            hint.transform.SetParent(transform, worldPositionStays: false);
            hintMesh = BuildQuad();
            hint.AddComponent<MeshFilter>().sharedMesh = hintMesh;
            MeshRenderer hintRenderer = hint.AddComponent<MeshRenderer>();
            hintRenderer.sharedMaterial = panelMaterial;
            hintRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            hintRenderer.receiveShadows = false;
            hintPanel = hint.transform;
            hint.SetActive(false);

            // Attachment menu panel: same texture and layer, cropped to the menu's rectangle, hidden until needed.
            GameObject attach = new GameObject("UnturnedVR GUI Attach Panel");
            attach.layer = panelLayer;
            attach.transform.SetParent(transform, worldPositionStays: false);
            Mesh attachMesh = BuildQuad();
            Rect crop = VRAttachMenu.CanvasRect;
            attachMesh.uv = new[]
            {
                new Vector2(crop.xMin, 1f - crop.yMax), new Vector2(crop.xMax, 1f - crop.yMax),
                new Vector2(crop.xMin, 1f - crop.yMin), new Vector2(crop.xMax, 1f - crop.yMin),
            };
            attach.AddComponent<MeshFilter>().sharedMesh = attachMesh;
            MeshRenderer attachRenderer = attach.AddComponent<MeshRenderer>();
            attachRenderer.sharedMaterial = panelMaterial;
            attachRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            attachRenderer.receiveShadows = false;
            attachPanel = attach.transform;
            attach.SetActive(false);
        }

        private void ResizePanel()
        {
            float aspect = (float)uiTexture.width / uiTexture.height;
            float width = menuLayout ? MenuPanelWidth : PanelWidth;
            panel.localScale = new Vector3(width, width / aspect, 1f);
        }

        // Unit quad, visible from -Z (looking along +Z), like Unity's built-in Quad.
        private static Mesh BuildQuad()
        {
            Mesh mesh = new Mesh { name = "UnturnedVR GUI Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Camera PickEyeCamera()
        {
            Camera vr = VRRig.ActiveCamera;
            if (vr != null)
            {
                return vr;
            }

            Camera main = MainCamera.instance;
            return main != null && main.isActiveAndEnabled ? main : null;
        }

        private void PlacePanel(Transform eye)
        {
            // In game the panel is a lazily head-following HUD; in the menu and loading screen (no VR
            // rig yet) it stays put in the world so you can look around it.
            // Also while a cursor UI (inventory, pause, dialogues) is open: you point at it, so it must hold still.
            bool cursorUiOpen = PlayerUI.window != null && PlayerUI.window.showCursor;
            bool wantMenuLayout = VRRig.InMenu || VRRig.ActiveCamera == null || cursorUiOpen;
            if (wantMenuLayout != menuLayout)
            {
                menuLayout = wantMenuLayout;
                needsRecenter = true;
                ResizePanel();
            }

            if (menuLayout)
            {
                PlaceMenuPanel(eye);
            }
            else
            {
                PlaceHudPanel(eye);
            }
        }

        // The game camera's transform follows the head, so the panel pose is captured once and kept
        // in world space. It is captured again on recenter, or when the camera is moved far away
        // (scripted menu camera pans), which head movement alone never does.
        private void PlaceMenuPanel(Transform eye)
        {
            // Place from the camera's forward even without a head pose (the editor's OpenXR session often
            // never delivers one); otherwise the panel stays where it was and the rig faces away from it.
            // Re-place once the first pose arrives, since that changes the view direction and height.
            bool hasPose = standalone ? loadingHead.localPosition != Vector3.zero
                : VRRig.ActiveCamera == null || VRRig.HasHeadPose;
            if (hasPose && !hadHeadPose)
            {
                needsRecenter = true;
            }
            hadHeadPose = hasPose;

            // In the main menu the rig hangs on the scripted menu camera (fly-in, page pans). Anchor the panel in
            // the rig's tracking space: it then travels rigidly with that camera (no re-anchoring jumps) and,
            // when the camera comes to rest, is still exactly where it was placed in front of the head.
            Transform rig = VRRig.RigTransform;
            // The same holds in game while a cursor UI (inventory, pause) is open: the rig hangs on the player,
            // which rides along in a vehicle, so a rig-local panel stays put in front of you instead of being
            // re-pinned in world space every time the car has moved 1.5 m.
            bool railed = !standalone && rig != null && VRRig.ActiveCamera != null;
            if (railed != menuRailed)
            {
                menuRailed = railed;
                needsRecenter = true;
            }

            if (railed)
            {
                if (needsRecenter || !menuPoseValid)
                {
                    Vector3 localForward = rig.InverseTransformDirection(eye.forward);
                    localForward.y = 0f;
                    if (localForward.sqrMagnitude < 0.01f)
                    {
                        localForward = Vector3.forward;
                    }

                    menuLocalRotation = Quaternion.LookRotation(localForward.normalized, Vector3.up);
                    menuLocalPosition = rig.InverseTransformPoint(eye.position) + menuLocalRotation * Vector3.forward * MenuPanelDistance;
                    menuPoseValid = true;
                    needsRecenter = false;
                }

                panel.position = rig.TransformPoint(menuLocalPosition);
                panel.rotation = rig.rotation * menuLocalRotation;
                return;
            }

            // Old teleporting code: the panel is pinned in world space, but if the camera moves far away it is re-anchored
            bool cameraMovedFar = (eye.position - menuEyeAnchor).sqrMagnitude > MenuReanchorDistance * MenuReanchorDistance;
            if (needsRecenter || cameraMovedFar || !menuPoseValid)
            {
                Vector3 forward = eye.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f)
                {
                    forward = Vector3.forward; // looking straight up/down: yaw is ill-defined
                }

                Quaternion rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
                menuPanelPosition = eye.position + rotation * Vector3.forward * MenuPanelDistance;
                menuPanelRotation = rotation;
                menuEyeAnchor = eye.position;
                menuPoseValid = true;
                needsRecenter = false;
            }

            panel.position = menuPanelPosition;
            panel.rotation = menuPanelRotation;
        }

        private void PlaceHudPanel(Transform eye)
        {
            Vector3 forward = eye.forward;
            forward.y = 0f;
            // Looking almost straight up/down: yaw is ill-defined, keep the previous one.
            if (forward.sqrMagnitude >= 0.01f)
            {
                float headYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                if (needsRecenter)
                {
                    panelYaw = headYaw;
                    needsRecenter = false;
                }
                else if (Mathf.Abs(Mathf.DeltaAngle(panelYaw, headYaw)) > FollowDeadzoneDegrees)
                {
                    panelYaw = Mathf.LerpAngle(panelYaw, headYaw, 1f - Mathf.Exp(-FollowSharpness * Time.unscaledDeltaTime));
                }
            }

            Quaternion yaw = Quaternion.Euler(0f, panelYaw, 0f);
            panel.position = eye.position + yaw * Vector3.forward * PanelDistance;
            panel.rotation = yaw;
        }

        private void SetPanelVisible(bool visible)
        {
            // The big panel makes way for the attachment menu panel on the gun.
            bool bigVisible = visible && !attachShown;
            if (panel != null && panel.gameObject.activeSelf != bigVisible)
            {
                panel.gameObject.SetActive(bigVisible);
            }
            if (overlayCamera != null && overlayCamera.enabled != visible)
            {
                overlayCamera.enabled = visible;
            }
        }

        // An unnamed layer (3, 6, 7 in Unturned's TagManager): nothing in the game lives on it, so
        // stripping it from the game's camera masks and drawing only it with the overlay camera is safe.
        private static int FindFreeLayer()
        {
            for (int layer = 31; layer >= 0; layer--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer)))
                {
                    return layer;
                }
            }
            return -1;
        }
    }
}
