using SDG.Unturned;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Whole-view screen effects on the VR camera. The game draws them for the flat MainCamera (and its own UI canvas), so
    /// the headset never showed them:
    ///   - the red pain flash, the flashbang flash and the blindfold are PlayerUI.colorOverlayImage, a full-window image on
    ///     the 2D HUD canvas, which in VR only covered the HUD panel. PlayerUIOverlayColorPatch hands the colour over
    ///     (<see cref="SetTint"/>) and blanks the canvas image; it is drawn here as a head-locked quad in front of both eyes.
    ///   - the grey screen at low health (and under night vision) is a legacy GrayscaleEffect image effect on MainCamera, the
    ///     viewmodel camera and the character camera; the VR camera gets its own copy, driven by the game's values.
    ///   - hallucination is post-processing (colour grading hue shift + vignette) in global volumes, which the VR camera's
    ///     PostProcessLayer picks up; <see cref="SyncPostProcessLayer"/> keeps that layer matching MainCamera's.
    /// </summary>
    internal static class VRScreenEffects
    {
        // The quad hangs this far in front of the eye (the overlay camera's near plane is 0.1) and is large enough to
        // cover the widest headset FOV with margin: half-size 2 m at 0.3 m is about +-81 degrees.
        private const float TintDistance = 0.3f;
        private const float TintHalfSize = 2f;
        private const float MinVisibleAlpha = 0.004f;

        // The pain/stun colour is handed over from PlayerUI.Update every frame; a few frames without it (player gone,
        // main menu) mean there is no effect any more.
        private static Color tint;
        private static int tintFrame = -10;

        private static GameObject tintObject;
        private static Material tintMaterial;
        private static MeshRenderer tintRenderer;

        /// <summary>Called by PlayerUIOverlayColorPatch with the colour the game wanted on its full-window overlay image.</summary>
        public static void SetTint(Color color)
        {
            tint = color;
            tintFrame = Time.frameCount;
        }

        /// <summary>Per frame, from VRRig.SyncGraphicsSettings: draws the tint and mirrors the grayscale effect.</summary>
        public static void Update(Camera from, Camera to)
        {
            UpdateTint(to);
            SyncGrayscale(from, to);
        }

        private static void UpdateTint(Camera eye)
        {
            bool wanted = Time.frameCount - tintFrame <= 2 && tint.a > MinVisibleAlpha && VRGui.PanelLayer >= 0 && eye != null;
            if (!wanted)
            {
                if (tintRenderer != null && tintRenderer.enabled)
                {
                    tintRenderer.enabled = false;
                }
                return;
            }

            if (tintObject == null)
            {
                CreateTint();
            }
            if (tintObject.transform.parent != eye.transform)
            {
                tintObject.transform.SetParent(eye.transform, worldPositionStays: false);
                tintObject.transform.localPosition = new Vector3(0f, 0f, TintDistance);
                tintObject.transform.localRotation = Quaternion.identity;
            }
            if (tintObject.layer != VRGui.PanelLayer)
            {
                tintObject.layer = VRGui.PanelLayer;
            }

            tintMaterial.color = tint;
            tintRenderer.enabled = true;
        }

        // On the GUI panel layer, which only VRGui's overlay camera draws (on top of the world and of the HUD panel,
        // which sits further away). Same material family as the binoculars' lens pieces.
        private static void CreateTint()
        {
            tintObject = new GameObject("UnturnedVR Screen Tint");
            tintObject.layer = VRGui.PanelLayer;

            tintMaterial = new Material(Canvas.GetDefaultCanvasMaterial());
            tintMaterial.renderQueue = 3900; // after the HUD panel and the lens pieces

            float h = TintHalfSize;
            var mesh = new Mesh { name = "UnturnedVR Screen Tint Quad" };
            mesh.vertices = new[] { new Vector3(-h, -h, 0f), new Vector3(h, -h, 0f), new Vector3(h, h, 0f), new Vector3(-h, h, 0f) };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();

            tintObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            tintRenderer = tintObject.AddComponent<MeshRenderer>();
            tintRenderer.sharedMaterial = tintMaterial;
            tintRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tintRenderer.receiveShadows = false;
            tintRenderer.enabled = false;
            VRLog.Info("screen effects: head-locked tint quad created (pain / flashbang / blindfold).");
        }

#if UNITY_EDITOR
        // The SDK has no legacy image effects (Assembly-CSharp's UpdateGrayscaleEffectPartial is stripped), so no grayscale.
        private static void SyncGrayscale(Camera from, Camera to)
        {
        }
#else
        // The game keeps one GrayscaleEffect per camera that can show the world (PlayerLifeUI.UpdateGrayscaleEffectPartial):
        // the viewmodel camera, MainCamera and the character camera, enabled depending on perspective, with `blend` from
        // the lighting vision (night vision) and the player's health. The VR camera shows all of it, so it follows
        // whichever of them is enabled with the strongest blend.
        private static Component mainOwner;
        private static GrayscaleEffect mainEffect;
        private static Component viewmodelOwner;
        private static GrayscaleEffect viewmodelEffect;
        private static Component characterOwner;
        private static GrayscaleEffect characterEffect;
        private static GrayscaleEffect vrEffect;
        private static bool loggedCreated;

        private static void SyncGrayscale(Camera from, Camera to)
        {
            Player player = Player.LocalPlayer;
            Component viewmodel = null;
            Component character = null;
            if (player != null)
            {
                if (player.animator != null)
                {
                    viewmodel = player.animator.viewmodelCameraTransform;
                }
                if (player.look != null)
                {
                    character = player.look.characterCamera;
                }
            }

            GrayscaleEffect strongest = null;
            Consider(ref mainOwner, ref mainEffect, from, ref strongest);
            Consider(ref viewmodelOwner, ref viewmodelEffect, viewmodel, ref strongest);
            Consider(ref characterOwner, ref characterEffect, character, ref strongest);

            bool wanted = strongest != null && strongest.blend > 0.001f && strongest.shader != null;
            if (!wanted)
            {
                if (vrEffect != null && vrEffect.enabled)
                {
                    vrEffect.enabled = false;
                }
                return;
            }

            if (vrEffect == null || vrEffect.gameObject != to.gameObject)
            {
                vrEffect = to.GetComponent<GrayscaleEffect>();
                if (vrEffect == null)
                {
                    vrEffect = to.gameObject.AddComponent<GrayscaleEffect>();
                    if (!loggedCreated)
                    {
                        loggedCreated = true;
                        VRLog.Info("screen effects: added GrayscaleEffect to the VR camera (low health / night vision).");
                    }
                }
            }

            // Shader before the component's Start (it disables itself without one).
            if (vrEffect.shader != strongest.shader) vrEffect.shader = strongest.shader;
            if (vrEffect.textureRamp != strongest.textureRamp) vrEffect.textureRamp = strongest.textureRamp;
            vrEffect.rampOffset = strongest.rampOffset;
            vrEffect.blend = strongest.blend;
            if (!vrEffect.enabled) vrEffect.enabled = true;
        }

        private static void Consider(ref Component cachedOwner, ref GrayscaleEffect cached, Component owner, ref GrayscaleEffect strongest)
        {
            if (owner == null)
            {
                cachedOwner = null;
                cached = null;
                return;
            }
            if (cachedOwner != owner || cached == null)
            {
                cachedOwner = owner;
                cached = owner.GetComponent<GrayscaleEffect>();
            }
            if (cached != null && cached.enabled && (strongest == null || cached.blend > strongest.blend))
            {
                strongest = cached;
            }
        }
#endif

        /// <summary>
        /// The VR camera's PostProcessLayer is a one-time copy of MainCamera's (VRRig.CopyPostProcessLayer). The volume mask
        /// and fog flags are what the global volumes (fog, hallucination colour grading, vignette) depend on; keep them
        /// matching MainCamera's in case the game sets them after the copy. The anti-aliasing mode is the same: the
        /// options menu changes it on MainCamera's layer after the copy was made.
        /// </summary>
        public static void SyncPostProcessLayer(Camera from, Camera to)
        {
            PostProcessLayer source = from.GetComponent<PostProcessLayer>();
            PostProcessLayer target = to.GetComponent<PostProcessLayer>();
            if (source == null || target == null)
            {
                return;
            }

            if (target.volumeLayer != source.volumeLayer)
            {
                target.volumeLayer = source.volumeLayer;
                VRLog.Info("screen effects: VR camera PostProcessLayer volume mask -> " + source.volumeLayer.value);
            }
            // The game's anti-aliasing option (off / FXAA / TAA / SMAA) is written to MainCamera's layer only, by
            // UnturnedPostProcess.applyUserSettings, so the VR layer has to follow it or the option does nothing in VR.
            PostProcessLayer.Antialiasing wantedAa = VRAntialiasing(source.antialiasingMode);
            if (target.antialiasingMode != wantedAa)
            {
                target.antialiasingMode = wantedAa;
                VRLog.Info("screen effects: VR camera anti-aliasing -> " + wantedAa
                    + (wantedAa != source.antialiasingMode ? " (game option is " + source.antialiasingMode + ")" : ""));
            }
            if (target.fog.enabled != source.fog.enabled)
            {
                target.fog.enabled = source.fog.enabled;
            }
            if (target.fog.excludeSkybox != source.fog.excludeSkybox)
            {
                target.fog.excludeSkybox = source.fog.excludeSkybox;
            }
        }

        /// <summary>
        /// The mode the VR camera uses for the game's chosen one. Temporal AA breaks the stereo image (it needs per-eye
        /// history and jitter, which the headset's eye textures do not give it), so the game's TAA option becomes SMAA.
        /// </summary>
        internal static PostProcessLayer.Antialiasing VRAntialiasing(PostProcessLayer.Antialiasing gameMode)
        {
            return gameMode == PostProcessLayer.Antialiasing.TemporalAntialiasing
                ? PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing
                : gameMode;
        }
    }
}
