using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Binoculars (UseableOptic) in VR. The game zooms MainCamera.fieldOfView and shows a full-screen mask on the HUD;
    /// the VR camera ignores both (the headset sets the projection, MainCamera is disabled), so nothing zoomed.
    /// The game's own state machine is left alone (it hides the HUD, cancels on dequip / third person, handles the
    /// toggle-aim setting) and gets its "secondary" key from <see cref="Active"/> via VRKeyMap, like the physical ADS
    /// of guns (VRAds): the binoculars are "on" while they are held up to the eyes, and off again when the hand
    /// moves away. While on, the hand only holds them: the view follows the head, like the vanilla zoom does.
    /// The picture is a lens: a second camera at the eye, looking where the head looks, renders into a
    /// RenderTexture with a field of view narrowed by the item's zoom factor. A head-locked disc shows it, surrounded
    /// by a black ring, both on VRGui's panel layer so VRGui's overlay camera draws them on top of the world.
    /// The same picture goes to both eyes (mono); for far targets that is not noticeable.
    /// Detail: vanilla changes nothing but MainCamera's field of view when zooming (no draw distance or LOD bias change;
    /// Unity picks finer LODs because things get larger on screen). The lens does the same with its narrower field of
    /// view and, on top of that, renders with a raised QualitySettings.lodBias (<see cref="LensLodBoostMax"/>), only
    /// for its own render pass (Camera.onPreCull / onPostRender), so the world seen by the headset is not affected.
    /// All numbers are GUESSES; tune by feel. Cost while not raised: one distance test per frame. While raised: one
    /// extra scene render at <see cref="TextureSize"/> squared.
    /// </summary>
    internal static class VRBinoculars
    {
        // The binoculars count as raised when the model itself (its renderers' bounds) is this close to the head. The
        // model is measured, not the controller, because the model's origin and axes are not known (any asset may be used).
        private const float EnterDistance = 0.10f; // metres from the head centre to the nearest part of the model
        private const float ExitDistance = 0.16f;
        private const float EnterDwell = 0.10f;    // seconds the entry zone must hold
        private const float ExitDwell = 0.25f;     // seconds the item must stay out of the zone before the zoom ends
        private const float MinOnSeconds = 0.20f;  // does not drop out right after entering
        private const float NearLogDistance = 0.5f; // diagnostics: "near but not raised" lines appear inside this
        private const float NearLogInterval = 1.5f;
        private const float NoZoomWarnSeconds = 0.5f;

        private const float LensFov = 40f;         // degrees the lens window spans (vertical and horizontal)
        private const float LensDistance = 2f;     // metres in front of the eyes
        private const float SurroundRadius = 12f;  // metres at LensDistance: far outside the headset's field of view
        private const int TextureSize = 1024;
        private const int Segments = 64;
        private const float MirrorInterval = 0.5f; // seconds between copies of the game's graphics settings
        private const float LensLodBoostMax = 4f;  // the lens renders with lodBias * min(zoom, this): GUESS, 1 = same as the game

        private static bool applies;
        private static bool active;
        private static bool changed;
        private static float enterSince = -1f;
        private static float outSince = -1f;
        private static float activeSince;
        private static float lastNearLog;
        private static float raisedSince = -1f;
        private static bool warnedNoZoom;
        private static Transform cachedModel;
        private static Renderer[] cachedRenderers;

        // Smoothing of the lens direction (VRAimFilter). The picture is magnified and head-locked, so a small tremor of
        // the head is large in the lens; the filter is calmer the higher the zoom (cut-off scaled by 1/sqrt(zoom)). The
        // head moves far less than a hand did and the picture lagging a head turn feels wrong, hence a higher cut-off
        // than the hand-aimed lens had (1 Hz / 0.05).
        private const float DefaultMinCutoff = 2f;  // Hz, GUESS
        private const float DefaultBeta = 0.1f;     // GUESS
        private static readonly VRAimFilter viewFilter = new VRAimFilter();
        private static Vector3 viewForward = Vector3.forward; // filtered lens direction, world space

        private static GameObject lensRoot;
        private static Renderer discRenderer;
        private static Renderer surroundRenderer;
        private static Camera zoomCamera;
        private static RenderTexture zoomTexture;
        private static bool lensVisible;
        private static float lastMirror;
        private static float lensLodBoost = 1f;
        private static float savedLodBias;
        private static bool lodBiasRaised;
        private static bool lodHooked;

        /// <summary>The binoculars are at the eyes (the game should zoom).</summary>
        public static bool Active => active && applies && VRArms.Active;

        /// <summary>The equipped item is binoculars on a hand: eye position decides, not the other trigger.</summary>
        public static bool Applies => applies && VRArms.Active;

        /// <summary>Active changed since the last <see cref="AcknowledgeChange"/> (for the toggle-aim setting).</summary>
        public static bool Changed => changed;

        /// <summary>The lens (zoomed picture and black surround) is on screen; VRGui hides the flat HUD panel then.</summary>
        public static bool LensVisible => lensVisible;

        /// <summary>The lens camera while the lens is up, else null (the game's focus foliage is drawn for it, see FoliageFocusLensPatch).</summary>
        public static Camera LensCamera => lensVisible && zoomCamera != null ? zoomCamera : null;

        /// <summary>The camera is the lens camera, whether the lens is up or not.</summary>
        public static bool IsLensCamera(Camera camera)
        {
            return camera != null && camera == zoomCamera;
        }

        public static void AcknowledgeChange()
        {
            changed = false;
        }

        /// <summary>Once per frame, from VRArms.Apply (LateUpdate) after the item has been placed.</summary>
        public static void Update()
        {
            bool wasActive = active;
            bool isActive = wasActive;
            applies = false;

            Camera eye = VRRig.ActiveCamera;
            Player player = Player.LocalPlayer;
            int hand = VRArms.ItemHand;
            Transform model = player != null && player.equipment != null ? player.equipment.firstModel : null;
            bool uiOpen = VRPointer.Active || (PlayerUI.window != null && PlayerUI.window.showCursor);
            if (!uiOpen && eye != null && hand >= 0 && model != null && player.equipment.useable is UseableOptic)
            {
                applies = true;
                Transform head = eye.transform;
                float distance = DistanceToModel(model, head.position);
                float now = Time.unscaledTime;

                if (!wasActive)
                {
                    if (distance > EnterDistance)
                    {
                        enterSince = -1f;
                        if (distance < NearLogDistance && now - lastNearLog >= NearLogInterval)
                        {
                            lastNearLog = now;
                            VRLog.Info("binoculars: near the face but not raised, " + distance.ToString("0.00") + " m from the head (raised at "
                                + EnterDistance.ToString("0.00") + " m or less).");
                        }
                    }
                    else if (enterSince < 0f)
                    {
                        enterSince = now;
                    }
                    else if (now - enterSince >= EnterDwell)
                    {
                        isActive = true;
                        activeSince = now;
                        outSince = -1f;
                        VRGunTuning.GetSmoothing("optic", DefaultMinCutoff, DefaultBeta, out float minCutoff, out float beta);
                        viewFilter.MinCutoff = minCutoff;
                        viewFilter.Beta = beta;
                        viewFilter.Reset(); // starts from the current direction: no lag from an earlier raise
                        VRLog.Info("binoculars: raised (" + distance.ToString("0.00") + " m from the head, view follows the head, smoothing "
                            + minCutoff.ToString("0.##") + " Hz / beta " + beta.ToString("0.###") + ").");
                    }
                }
                else
                {
                    bool outside = distance > ExitDistance;
                    if (!outside)
                    {
                        outSince = -1f;
                    }
                    else if (outSince < 0f)
                    {
                        outSince = now;
                    }
                    else if (now - outSince >= ExitDwell && now - activeSince >= MinOnSeconds)
                    {
                        isActive = false;
                        enterSince = -1f;
                        VRLog.Info("binoculars: lowered (" + distance.ToString("0.00") + " m from the head).");
                    }
                }

                EnsureLens(eye);
            }
            else
            {
                isActive = false;
                enterSince = -1f;
                outSince = -1f;
            }

            if (isActive && eye != null)
            {
                // Once per frame (this runs from LateUpdate only): the head direction, calmed. The hand only holds the
                // binoculars. PlaceCamera and the marker aim read the result, also in the before-render pass.
                ItemOpticAsset optic = player.equipment.asset as ItemOpticAsset;
                float zoom = optic != null ? Mathf.Max(1f, optic.zoom) : 1f;
                viewForward = viewFilter.Step(eye.transform.forward, Mathf.Min(Time.unscaledDeltaTime, 0.1f), 1f / Mathf.Sqrt(zoom));
            }

            active = isActive;
            if (isActive != wasActive)
            {
                changed = true;
                if (hand >= 0)
                {
                    VRHaptics.Pulse(hand, 0.25f, 0.03f, 200f);
                }
            }
        }

        /// <summary>
        /// Where the binoculars look: the eye position with the (calmed) head direction, rolled with the head so the
        /// horizon of the picture stays level with the lens window. False unless the binoculars are raised.
        /// </summary>
        public static bool TryGetView(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            Camera eye = VRRig.ActiveCamera;
            if (!Active || eye == null)
            {
                return false;
            }

            Vector3 forward = viewForward; // calmed in Update
            Vector3 up = eye.transform.up;
            if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.99f)
            {
                up = eye.transform.forward;
            }
            position = eye.transform.position;
            rotation = Quaternion.LookRotation(forward, up);
            return true;
        }

        // Distance from a point to the nearest mesh bounds of the held model. Renderers are cached per model.
        private static float DistanceToModel(Transform model, Vector3 point)
        {
            if (model != cachedModel)
            {
                cachedModel = model;
                cachedRenderers = model.GetComponentsInChildren<Renderer>(true);
            }

            float best = float.MaxValue;
            foreach (Renderer renderer in cachedRenderers)
            {
                if (renderer == null || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
                {
                    continue;
                }
                float distance = (renderer.bounds.ClosestPoint(point) - point).magnitude;
                if (distance < best)
                {
                    best = distance;
                }
            }
            return best < float.MaxValue ? best : (model.position - point).magnitude;
        }

        /// <summary>Puts the zoom camera on the view. After the item is placed, in LateUpdate and again before render.</summary>
        public static void PlaceCamera()
        {
            if (zoomCamera == null || !TryGetView(out Vector3 position, out Quaternion rotation))
            {
                return;
            }
            zoomCamera.transform.SetPositionAndRotation(position, rotation);
        }

        /// <summary>
        /// Shows or hides the lens: raised and the game really zoomed (its mask is on, so equip animation, third person
        /// and dequip cancels are respected). Runs after VRArms in LateUpdate (see <see cref="VRBinocularsLens"/>), also
        /// when VRArms is not running, so the lens cannot stay up.
        /// </summary>
        internal static void RefreshLens()
        {
            Camera eye = VRRig.ActiveCamera;
            bool gameZoomed = PlayerLifeUI.binocularsOverlay != null && PlayerLifeUI.binocularsOverlay.IsVisible;
            bool show = lensRoot != null && eye != null && Active && gameZoomed && VRGui.PanelLayer >= 0;
            if (show && !EnsureZoomCamera(eye))
            {
                show = false;
            }

            // Raised, but the game never zoomed: the secondary action did not arrive (say so once per raise).
            if (Active && !gameZoomed)
            {
                float now = Time.unscaledTime;
                if (raisedSince < 0f)
                {
                    raisedSince = now;
                }
                else if (!warnedNoZoom && now - raisedSince >= NoZoomWarnSeconds)
                {
                    warnedNoZoom = true;
                    VRLog.Warn("binoculars: raised for " + NoZoomWarnSeconds.ToString("0.0") + " s but the game did not zoom (no secondary action, or the item refused).");
                }
            }
            else
            {
                raisedSince = -1f;
                warnedNoZoom = false;
            }

            if (show)
            {
                Player player = Player.LocalPlayer;
                ItemOpticAsset asset = player != null && player.equipment != null ? player.equipment.asset as ItemOpticAsset : null;
                float zoom = asset != null ? Mathf.Max(1f, asset.zoom) : 1f;
                // Same angular scale as the window: a target looks "zoom" times larger than with the naked eye.
                float half = Mathf.Atan(Mathf.Tan(LensFov * 0.5f * Mathf.Deg2Rad) / zoom);
                zoomCamera.fieldOfView = half * 2f * Mathf.Rad2Deg;
                lensLodBoost = Mathf.Clamp(zoom, 1f, LensLodBoostMax);

                if (!lensVisible || Time.unscaledTime - lastMirror >= MirrorInterval)
                {
                    lastMirror = Time.unscaledTime;
                    int mask = eye.cullingMask & ~(1 << LayerMasks.VIEWMODEL) & ~(1 << VRGui.PanelLayer);
                    VRRig.MirrorSettings(eye, zoomCamera, mask);
                }
                if (!lensVisible)
                {
                    VRLog.Info("binoculars: lens on, zoom " + zoom.ToString("0.#") + "x, camera field of view " + zoomCamera.fieldOfView.ToString("0.0")
                        + " degrees, far clip " + zoomCamera.farClipPlane.ToString("0") + " m, lod bias " + QualitySettings.lodBias.ToString("0.#")
                        + " x " + lensLodBoost.ToString("0.#") + ".");
                }
            }
            else
            {
                RestoreLodBias(); // the render pass can never leave the raised bias behind
            }

            if (show != lensVisible)
            {
                lensVisible = show;
                if (!show)
                {
                    VRLog.Info("binoculars: lens off.");
                }
            }
            if (discRenderer != null && discRenderer.enabled != show)
            {
                discRenderer.enabled = show;
                surroundRenderer.enabled = show;
            }
            if (zoomCamera != null && zoomCamera.enabled != show)
            {
                zoomCamera.enabled = show;
            }
        }

        // The head-locked lens objects hang on the VR camera (they follow the head without any code) and live on the
        // GUI panel layer, which only VRGui's overlay camera draws.
        private static void EnsureLens(Camera eye)
        {
            if (VRGui.PanelLayer < 0)
            {
                return; // the GUI is not up yet
            }
            if (lensRoot != null && lensRoot.transform.parent == eye.transform)
            {
                return;
            }
            if (lensRoot != null)
            {
                UnityEngine.Object.Destroy(lensRoot);
            }

            lensRoot = new GameObject("UnturnedVR Binocular Lens");
            lensRoot.layer = VRGui.PanelLayer;
            lensRoot.transform.SetParent(eye.transform, worldPositionStays: false);
            lensRoot.AddComponent<VRBinocularsLens>();

            // Radius of a circle that spans LensFov at LensDistance.
            float radius = LensDistance * Mathf.Tan(LensFov * 0.5f * Mathf.Deg2Rad);

            if (zoomTexture == null)
            {
                zoomTexture = new RenderTexture(TextureSize, TextureSize, 24, RenderTextureFormat.ARGB32);
                zoomTexture.name = "UnturnedVR Binoculars";
                zoomTexture.useMipMap = false;
                zoomTexture.filterMode = FilterMode.Bilinear;
                zoomTexture.wrapMode = TextureWrapMode.Clamp;
                zoomTexture.Create();
            }

            Material source = Canvas.GetDefaultCanvasMaterial();
            Material discMaterial = new Material(source);
            discMaterial.mainTexture = zoomTexture;
            discRenderer = CreatePiece("Disc", BuildDisc(radius), discMaterial, LensDistance);

            // A little nearer and with its inner edge just inside the disc, so no seam of the world shows between them.
            Material surroundMaterial = new Material(source);
            surroundMaterial.color = Color.black;
            surroundRenderer = CreatePiece("Surround", BuildRing(radius * 0.99f, SurroundRadius), surroundMaterial, LensDistance - 0.01f);

            discRenderer.enabled = false;
            surroundRenderer.enabled = false;
            VRLog.Info("binoculars: lens created (window " + LensFov.ToString("0") + " degrees, " + TextureSize + " px).");
        }

        private static Renderer CreatePiece(string name, Mesh mesh, Material material, float distance)
        {
            GameObject go = new GameObject("UnturnedVR Binocular " + name);
            go.layer = VRGui.PanelLayer;
            go.transform.SetParent(lensRoot.transform, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 0f, distance);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }

        private static bool EnsureZoomCamera(Camera eye)
        {
            if (zoomCamera != null)
            {
                return true;
            }
            if (zoomTexture == null)
            {
                return false;
            }

            GameObject go = new GameObject("UnturnedVR Zoom Camera");
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.targetTexture = zoomTexture;
            camera.aspect = 1f;
            camera.depth = -50f; // before the eye camera, which reads the texture through the lens
            camera.allowMSAA = false;
            VRRig.CopyPostProcessLayer(eye, camera); // fog and the base look, like the VR camera
            VRRig.MirrorSettings(eye, camera, eye.cullingMask & ~(1 << LayerMasks.VIEWMODEL) & ~(1 << VRGui.PanelLayer));
            zoomCamera = camera;
            if (!lodHooked)
            {
                lodHooked = true;
                Camera.onPreCull += OnCameraPreCull;
                Camera.onPostRender += OnCameraPostRender;
            }
            return true;
        }

        // LOD levels are chosen while a camera culls, from QualitySettings.lodBias at that moment: raise it for the lens
        // camera only and put it back right after, so the headset camera and the game's own LOD handling never see it.
        private static void OnCameraPreCull(Camera camera)
        {
            if (camera != null && camera == zoomCamera)
            {
                if (!lodBiasRaised)
                {
                    savedLodBias = QualitySettings.lodBias;
                    lodBiasRaised = true;
                }
                QualitySettings.lodBias = savedLodBias * lensLodBoost;
            }
            else
            {
                RestoreLodBias();
            }
        }

        private static void OnCameraPostRender(Camera camera)
        {
            if (camera != null && camera == zoomCamera)
            {
                RestoreLodBias();
            }
        }

        private static void RestoreLodBias()
        {
            if (lodBiasRaised)
            {
                QualitySettings.lodBias = savedLodBias;
                lodBiasRaised = false;
            }
        }

        // Triangle fan, unit UVs over the inscribed square, so the (square) render texture shows through the circle.
        private static Mesh BuildDisc(float radius)
        {
            var vertices = new Vector3[Segments + 1];
            var uvs = new Vector2[Segments + 1];
            var triangles = new int[Segments * 3];
            vertices[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < Segments; i++)
            {
                float a = i * Mathf.PI * 2f / Segments;
                float x = Mathf.Cos(a);
                float y = Mathf.Sin(a);
                vertices[i + 1] = new Vector3(x * radius, y * radius, 0f);
                uvs[i + 1] = new Vector2(0.5f + 0.5f * x, 0.5f + 0.5f * y);
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = (i + 1) % Segments + 1;
            }

            Mesh mesh = new Mesh { name = "UnturnedVR Binocular Disc" };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildRing(float inner, float outer)
        {
            var vertices = new Vector3[Segments * 2];
            var triangles = new int[Segments * 6];
            for (int i = 0; i < Segments; i++)
            {
                float a = i * Mathf.PI * 2f / Segments;
                float x = Mathf.Cos(a);
                float y = Mathf.Sin(a);
                vertices[i * 2] = new Vector3(x * inner, y * inner, 0f);
                vertices[i * 2 + 1] = new Vector3(x * outer, y * outer, 0f);

                int next = (i + 1) % Segments;
                triangles[i * 6] = i * 2;
                triangles[i * 6 + 1] = i * 2 + 1;
                triangles[i * 6 + 2] = next * 2;
                triangles[i * 6 + 3] = next * 2;
                triangles[i * 6 + 4] = i * 2 + 1;
                triangles[i * 6 + 5] = next * 2 + 1;
            }

            Mesh mesh = new Mesh { name = "UnturnedVR Binocular Surround" };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>Drives <see cref="VRBinoculars.RefreshLens"/> every frame, after VRArms (execution order 500).</summary>
    [DefaultExecutionOrder(600)]
    internal sealed class VRBinocularsLens : MonoBehaviour
    {
        private void LateUpdate()
        {
            VRBinoculars.RefreshLens();
        }
    }
}
