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

        /// <summary>Layer the GUI panel lives on, or -1 before it is chosen.</summary>
        public static int PanelLayer { get; private set; } = -1;

        private Canvas canvas;
        private Camera uiCamera;
        private RenderTexture uiTexture;
        private Camera overlayCamera;
        private Transform panel;
        private Material panelMaterial;
        private int panelLayer = -1;

        private Camera eyeCamera;
        private float panelYaw;
        private bool needsRecenter = true;
        private bool standalone;
        private bool menuLayout = true;
        private bool menuPoseValid;
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
                UnturnedLog.info("[UnturnedVR] VRGui: overlay camera was destroyed with its parent camera; recreated.");
            }

            if (uiTexture.width != Screen.width || uiTexture.height != Screen.height)
            {
                RecreateTexture();
            }

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
                    overlayCamera.transform.SetParent(transform, worldPositionStays: false);
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
                SetPanelVisible(true);
                PlacePanel(overlayCamera.transform);
                return;
            }

            if (standalone)
            {
                standalone = false;
                overlayCamera.clearFlags = CameraClearFlags.Depth;
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

            SetPanelVisible(true);
            PlacePanel(eye.transform);
        }

        // Temporary: we cannot see the headset, so report what the pipeline is doing every few seconds.
        private void LogDiagnostics(Camera eye)
        {
            if (Time.unscaledTime < nextDiagnosticTime)
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

            UnturnedLog.info("[UnturnedVR] VRGui diag: eye=" + eyeInfo
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
                UnturnedLog.error("[UnturnedVR] VRGui: no free layer for the UI panel.");
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

            canvas = found;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = uiCamera;
            canvas.planeDistance = 1f;

            CreateOverlayCamera();
            CreatePanel();

            UnturnedLog.info("[UnturnedVR] VRGui: Glazier canvas switched to ScreenSpaceCamera -> " + uiTexture.width + "x" + uiTexture.height + " texture, panel layer " + panelLayer + ".");
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

        private void CreatePanel()
        {
            Material source = Canvas.GetDefaultCanvasMaterial();
            panelMaterial = new Material(source);
            panelMaterial.mainTexture = uiTexture;

            GameObject go = new GameObject("UnturnedVR GUI Panel");
            go.layer = panelLayer;
            go.transform.SetParent(transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = BuildQuad();
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = panelMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            panel = go.transform;
            ResizePanel();
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
            if (VRRig.ActiveCamera != null && !VRRig.HasHeadPose)
            {
                return; // wait for the first head pose, or the panel would be placed from the unrotated camera
            }

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
            if (panel != null && panel.gameObject.activeSelf != visible)
            {
                panel.gameObject.SetActive(visible);
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
