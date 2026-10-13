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
        // NPC dialogue, quest and vendor screens: pinned like the menu, but close (about the hint panel's distance) and
        // with the same apparent size (1.6 m at 1 m ~ 3.6 m at 2.2 m). Guesses to tune in-headset.
        private const float NpcPanelDistance = 1.0f;
        private const float NpcPanelWidth = 1.6f;
        // Loading screen: how long to wait for the first head pose before placing the panel anyway
        // (the editor's session may never deliver one).
        private const float LoadingPoseWaitSeconds = 3f;
        private const float FollowDeadzoneDegrees = 35f;
        private const float FollowSharpness = 3f;
        private const KeyCode RecenterKey = KeyCode.F10;
        // The pickup label panel, head-locked in front of the eyes. Guesses to tune in-headset.
        private const float HintDistance = 1.0f;
        private const float HintHeight = -0.12f;
        private const float HintScale = 1.5f;
        // A hint about an object hangs above it in the world; one without an object keeps a spot in front of the body
        // (HintDistance / HintHeight) that only moves when you turn far away or walk off. Guesses to tune in-headset.
        private const float HintLift = 0.15f;              // gap between the top of the object and the label's centre
        private const float HintMaxAbove = 0.9f;           // never higher than this above the object's pivot
        private const float HintMinViewDistance = 0.6f;   // the label is scaled with its distance, between these two
        private const float HintMaxViewDistance = 3f;
        private const float HintDeadzoneDegrees = 40f;     // untargeted: head turned this far from the label = bring it back
        private const float HintFreeMaxDistance = 2.5f;    // untargeted: walked this far from the label = bring it back
        private const float HintFollowSharpness = 4f;

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
        private Vector3 hintBaseScale = Vector3.one; // panel size at HintDistance
        private Transform hintTarget;                // object the panel hangs above, null = free spot
        private Vector3 hintTargetOffset;            // anchor relative to the object's pivot (world axes)
        private bool hintFree;                       // free spot is placed
        private Vector3 hintFreeLocal;               // free spot in rig space (moves with the body, not with the head)
        private bool hintRecentering;
        private bool loggedHint;
        private VRWorldLabels worldLabels;                                                  // teammate names and hit markers in the world
        private readonly System.Collections.Generic.List<Rect> hudHoles = new System.Collections.Generic.List<Rect>();
        private readonly System.Collections.Generic.List<Rect> shownHoles = new System.Collections.Generic.List<Rect>();
        private RenderTexture uiTexture;
        private Camera overlayCamera;
        private Transform loadingHead;
        private float loadingSince;
        private Transform panel;
        private Material panelMaterial;
        private int panelLayer = -1;

        private Camera eyeCamera;
        private float panelYaw;
        private bool needsRecenter = true;
        private bool standalone;
        private bool menuLayout = true;
        private bool npcLayout; // menu layout, but for a conversation screen: nearer and smaller
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
                    // Forget the pose from the last loading screen: the panel waits for a fresh one below.
                    loadingHead.localPosition = Vector3.zero;
                    loadingHead.localRotation = Quaternion.identity;
                    loadingSince = Time.unscaledTime;
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
                // Until the first head pose arrives the camera sits at the tracking origin (the floor): placing
                // the panel now would put it at your feet and then teleport it. Show only the dark background.
                if (loadingHead.localPosition == Vector3.zero && Time.unscaledTime - loadingSince < LoadingPoseWaitSeconds)
                {
                    if (panel != null && panel.gameObject.activeSelf)
                    {
                        panel.gameObject.SetActive(false);
                    }
                    overlayCamera.enabled = true;
                    UpdateHint(null);
                    return;
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
            // (Written only when the bit is actually set: this runs every frame and the mask is almost always clean.)
            int panelBit = 1 << panelLayer;
            int eyeMask = eye.cullingMask;
            if ((eyeMask & panelBit) != 0)
            {
                eye.cullingMask = eyeMask & ~panelBit;
            }

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
            bool inGame = eye != null && !menuLayout && !VRBinoculars.LensVisible;
            bool show = inGame && TryGetHintRect(out hintRect);

            if (show != hintShown)
            {
                hintTarget = null;
                hintFree = false;
                hintRecentering = false;
            }

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
            }

            if (hintPanel.gameObject.activeSelf != show)
            {
                hintPanel.gameObject.SetActive(show);
            }
            if (show)
            {
                PlaceHintPanel(eye);
            }

            // What is cut out of the HUD panel: the hint box, and every teammate name / hit marker that now hangs in the world.
            hudHoles.Clear();
            if (show)
            {
                hudHoles.Add(hintRect);
            }
            if (worldLabels != null)
            {
                if (inGame)
                {
                    float aspect = (float)uiTexture.width / uiTexture.height;
                    worldLabels.Update(eye, CanvasSize, aspect, PanelWidth / PanelDistance, hudHoles);
                }
                else
                {
                    worldLabels.Hide();
                }
            }
            ApplyHudHoles();
        }

        /// <summary>
        /// The HUD panel is the whole texture minus the rectangles in <see cref="hudHoles"/> (canvas units, origin top
        /// left): a band mesh, rebuilt only when the rectangles change. No holes = the plain quad.
        /// </summary>
        private void ApplyHudHoles()
        {
            bool same = hudHoles.Count == shownHoles.Count;
            for (int i = 0; same && i < hudHoles.Count; i++)
            {
                same = hudHoles[i] == shownHoles[i];
            }
            if (same)
            {
                return;
            }

            shownHoles.Clear();
            shownHoles.AddRange(hudHoles);
            if (hudHoles.Count == 0)
            {
                panelFilter.sharedMesh = quadMesh;
                return;
            }

            Vector2 canvasSize = CanvasSize;
            var u0s = new float[hudHoles.Count];
            var u1s = new float[hudHoles.Count];
            var v0s = new float[hudHoles.Count];
            var v1s = new float[hudHoles.Count];
            var bands = new System.Collections.Generic.List<float> { 0f, 1f };
            for (int i = 0; i < hudHoles.Count; i++)
            {
                Rect rect = hudHoles[i];
                u0s[i] = Mathf.Clamp01(rect.xMin / canvasSize.x);
                u1s[i] = Mathf.Clamp01(rect.xMax / canvasSize.x);
                v1s[i] = Mathf.Clamp01(1f - rect.yMin / canvasSize.y);
                v0s[i] = Mathf.Clamp01(1f - rect.yMax / canvasSize.y);
                bands.Add(v0s[i]);
                bands.Add(v1s[i]);
            }
            bands.Sort();

            var vertices = new System.Collections.Generic.List<Vector3>(32);
            var uvs = new System.Collections.Generic.List<Vector2>(32);
            var triangles = new System.Collections.Generic.List<int>(48);
            for (int band = 0; band + 1 < bands.Count; band++)
            {
                float bottom = bands[band];
                float top = bands[band + 1];
                if (top - bottom < 0.00001f)
                {
                    continue;
                }

                // Within this horizontal band the holes cover fixed intervals in u; draw what is left between them.
                float middle = (bottom + top) * 0.5f;
                float cursor = 0f;
                while (true)
                {
                    int next = -1;
                    for (int i = 0; i < hudHoles.Count; i++)
                    {
                        if (v0s[i] <= middle && middle <= v1s[i] && u1s[i] > cursor && (next < 0 || u0s[i] < u0s[next]))
                        {
                            next = i;
                        }
                    }
                    if (next < 0)
                    {
                        break;
                    }
                    if (u0s[next] > cursor)
                    {
                        AddRect(vertices, uvs, triangles, cursor, bottom, u0s[next], top);
                    }
                    cursor = Mathf.Max(cursor, u1s[next]);
                }
                if (cursor < 1f)
                {
                    AddRect(vertices, uvs, triangles, cursor, bottom, 1f, top);
                }
            }

            holeMesh.Clear();
            holeMesh.SetVertices(vertices);
            holeMesh.SetUVs(0, uvs);
            holeMesh.SetTriangles(triangles, 0);
            holeMesh.RecalculateBounds();
            panelFilter.sharedMesh = holeMesh;
        }

        /// <summary>
        /// Places the hint panel. A hint about an object (pickup, door, vehicle...) hangs above that object in the world,
        /// turned towards the eye, so head movement does not move it. Other messages (reload, safety, xp...) get a spot in
        /// front of you, kept in rig space: it follows walking and body turns but not head movement, and comes back to
        /// the front when you look far away from it. The panel is scaled with its distance so it keeps a readable size.
        /// </summary>
        private void PlaceHintPanel(Transform eye)
        {
            Vector3 eyePosition = eye.position;
            Vector3 position;

            Transform target = UnturnedVR.Patches.PlayerUIHintPatch.CurrentTarget;
            if (target != null)
            {
                if (target != hintTarget)
                {
                    hintTarget = target;
                    hintTargetOffset = ComputeHintOffset(target);
                    if (VRDebugFlags.LogGui)
                    {
                        VRLog.Info("hint panel: hangs above \"" + target.name + "\" (offset " + hintTargetOffset + ").");
                    }
                }
                position = target.position + hintTargetOffset;
                hintFree = false;
            }
            else
            {
                hintTarget = null;
                Transform rig = VRRig.RigTransform;
                Vector3 desired = FreeHintPosition(eye, rig);
                if (!hintFree)
                {
                    hintFree = true;
                    hintRecentering = false;
                    hintFreeLocal = rig != null ? rig.InverseTransformPoint(desired) : desired;
                }

                position = rig != null ? rig.TransformPoint(hintFreeLocal) : hintFreeLocal;
                Vector3 toHint = position - eyePosition;
                if (Vector3.Angle(eye.forward, toHint) > HintDeadzoneDegrees || toHint.sqrMagnitude > HintFreeMaxDistance * HintFreeMaxDistance)
                {
                    hintRecentering = true;
                }
                if (hintRecentering)
                {
                    position = Vector3.Lerp(position, desired, 1f - Mathf.Exp(-HintFollowSharpness * Time.unscaledDeltaTime));
                    hintFreeLocal = rig != null ? rig.InverseTransformPoint(position) : position;
                    if (Vector3.Angle(eye.forward, position - eyePosition) < 8f && (position - desired).sqrMagnitude < 0.01f)
                    {
                        hintRecentering = false;
                    }
                }
            }

            Vector3 fromEye = position - eyePosition;
            float distance = fromEye.magnitude;
            hintPanel.position = position;
            hintPanel.rotation = distance > 0.01f ? Quaternion.LookRotation(fromEye / distance, Vector3.up) : eye.rotation;
            hintPanel.localScale = hintBaseScale * (Mathf.Clamp(distance, HintMinViewDistance, HintMaxViewDistance) / HintDistance);
        }

        // Where the untargeted hint sits when it is (re)placed: HintDistance ahead along the head heading, HintHeight below the eye.
        private static Vector3 FreeHintPosition(Transform eye, Transform rig)
        {
            Vector3 forward = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = rig != null ? Vector3.ProjectOnPlane(rig.forward, Vector3.up) : Vector3.forward;
            }
            return eye.position + forward.normalized * HintDistance + Vector3.up * HintHeight;
        }

        // Anchor above the object: over the top of its solid colliders (at most HintMaxAbove over its pivot), as an offset
        // from the pivot so it follows the object when it moves. Done once per target change.
        private static Vector3 ComputeHintOffset(Transform target)
        {
            Vector3 pivot = target.position;
            Bounds bounds = new Bounds(pivot, Vector3.zero);
            foreach (Collider collider in target.GetComponentsInChildren<Collider>())
            {
                if (collider.enabled && !collider.isTrigger)
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            float top = Mathf.Clamp(bounds.max.y, pivot.y + 0.1f, pivot.y + HintMaxAbove);
            return new Vector3(bounds.center.x - pivot.x, top + HintLift - pivot.y, bounds.center.z - pivot.z);
        }

        private void ApplyHintGeometry(Rect rect)
        {
            Vector2 canvasSize = CanvasSize;
            float u0 = Mathf.Clamp01(rect.xMin / canvasSize.x);
            float u1 = Mathf.Clamp01(rect.xMax / canvasSize.x);
            float v1 = Mathf.Clamp01(1f - rect.yMin / canvasSize.y);
            float v0 = Mathf.Clamp01(1f - rect.yMax / canvasSize.y);

            // (The HUD panel's matching hole is cut by ApplyHudHoles, together with the world labels' rectangles.)
            // Near panel: only the label's rectangle, sized to look like the HUD scaled down to HintDistance.
            hintMesh.uv = new[] { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u0, v1), new Vector2(u1, v1) };
            float aspect = (float)uiTexture.width / uiTexture.height;
            float scale = PanelWidth * (HintDistance / PanelDistance) * HintScale;
            hintBaseScale = new Vector3(
                (u1 - u0) * scale,
                (v1 - v0) * scale / aspect,
                1f);
            hintPanel.localScale = hintBaseScale;
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

            // Teammate names and hit markers: cropped from the same texture, hung in the world (VRWorldLabels).
            worldLabels = new VRWorldLabels(transform, panelMaterial, panelLayer);
        }

        private void ResizePanel()
        {
            float aspect = (float)uiTexture.width / uiTexture.height;
            float width = menuLayout ? (npcLayout ? NpcPanelWidth : MenuPanelWidth) : PanelWidth;
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
            // A conversation with an NPC (dialogue, quest offer, shop) is a cursor UI too, but it is placed close in front
            // of you when it opens, like a hint, and then stays put.
            bool wantNpcLayout = wantMenuLayout && (PlayerNPCDialogueUI.active || PlayerNPCQuestUI.active || PlayerNPCVendorUI.active);
            if (wantMenuLayout != menuLayout || wantNpcLayout != npcLayout)
            {
                menuLayout = wantMenuLayout;
                npcLayout = wantNpcLayout;
                needsRecenter = true;
                ResizePanel();
                if (npcLayout)
                {
                    VRLog.Info("gui: conversation screen, panel at " + NpcPanelDistance.ToString("0.0#") + " m.");
                }
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
            float distance = npcLayout ? NpcPanelDistance : MenuPanelDistance;
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
                    menuLocalPosition = rig.InverseTransformPoint(eye.position) + menuLocalRotation * Vector3.forward * distance;
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
                menuPanelPosition = eye.position + rotation * Vector3.forward * distance;
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
            // The binoculars lens replaces the HUD (the game hides it and shows its flat mask, which would float at 2 m).
            bool bigVisible = visible && !attachShown && !VRBinoculars.LensVisible;
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
