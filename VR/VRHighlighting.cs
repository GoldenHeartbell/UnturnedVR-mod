using SDG.Unturned;
using UnityEngine;
#if !UNITY_EDITOR
using HighlightingSystem;
#endif

namespace UnturnedVR.VR
{
    /// <summary>
    /// M4: the outline around the item/object you can interact with. The shipped game draws it with the
    /// Highlighting System asset: a HighlightingRenderer component on MainCamera (camera command buffer
    /// plus OnRenderImage) and a Highlighter on each outlined object. The renderer is per camera, so
    /// the VR camera needs its own copy, with MainCamera's settings kept in sync. The SDK/editor build
    /// has no HighlightingSystem (closed-source, stripped from the SDK), so this is a no-op there.
    /// </summary>
    internal static class VRHighlighting
    {
#if UNITY_EDITOR
        public static void Sync(Camera from, Camera to)
        {
        }
#else
        private static bool loggedCreated;

        // Sync runs every frame: the two components are looked up again only when the camera changes or the cached
        // one is gone (a missing source is looked for every call, as before, since it may be added later).
        private static Camera cachedFrom;
        private static Camera cachedTo;
        private static HighlightingRenderer cachedSource;
        private static HighlightingRenderer cachedTarget;

        public static void Sync(Camera from, Camera to)
        {
            if (cachedFrom != from || cachedSource == null)
            {
                cachedFrom = from;
                cachedSource = from.GetComponent<HighlightingRenderer>();
            }
            HighlightingRenderer source = cachedSource;
            if (source == null)
            {
                return;
            }

            if (cachedTo != to || cachedTarget == null)
            {
                cachedTo = to;
                cachedTarget = to.GetComponent<HighlightingRenderer>();
                if (cachedTarget == null)
                {
                    cachedTarget = to.gameObject.AddComponent<HighlightingRenderer>();
                    if (!loggedCreated)
                    {
                        loggedCreated = true;
                        VRLog.Info("added HighlightingRenderer to the VR camera (outline for interactable objects).");
                    }
                }
            }
            HighlightingRenderer target = cachedTarget;

            if (target.enabled != source.enabled) target.enabled = source.enabled;
            if (target.fillAlpha != source.fillAlpha) target.fillAlpha = source.fillAlpha;
            if (target.downsampleFactor != source.downsampleFactor) target.downsampleFactor = source.downsampleFactor;
            if (target.iterations != source.iterations) target.iterations = source.iterations;
            if (target.blurMinSpread != source.blurMinSpread) target.blurMinSpread = source.blurMinSpread;
            if (target.blurSpread != source.blurSpread) target.blurSpread = source.blurSpread;
            if (target.blurIntensity != source.blurIntensity) target.blurIntensity = source.blurIntensity;
            if (target.blurDirections != source.blurDirections) target.blurDirections = source.blurDirections;
            if (target.antiAliasing != source.antiAliasing) target.antiAliasing = source.antiAliasing;
        }
#endif

        private static bool loggedComponents;

        /// <summary>One-time log of what MainCamera carries, to see which effects the VR camera lacks.</summary>
        public static void LogCameraComponents(Camera camera)
        {
            if (loggedComponents || camera == null)
            {
                return;
            }
            loggedComponents = true;

            var sb = new System.Text.StringBuilder("MainCamera components:");
            foreach (Component component in camera.GetComponents<Component>())
            {
                sb.Append(' ').Append(component == null ? "<missing>" : component.GetType().Name);
            }
            VRLog.Info(sb.ToString());
        }
    }
}
