using HarmonyLib;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// M5: Glazier's RectTransform.GetAbsoluteRect reads transform.position as screen pixels, which only
    /// holds for a ScreenSpaceOverlay canvas. VRGui moves the canvas to ScreenSpaceCamera, where positions
    /// are world units, so everything derived from the rect (ViewportToNormalizedPosition, used by the
    /// inventory drag visual that follows the cursor) landed far off screen. This computes the same rect
    /// (top-left origin, pixels) by projecting the corners through the UI camera, which renders into a
    /// texture of Screen size.
    /// </summary>
    [HarmonyPatch(typeof(RectTransformExtension), nameof(RectTransformExtension.GetAbsoluteRect))]
    internal static class AbsoluteRectPatch
    {
        private static readonly Vector3[] Corners = new Vector3[4];

        private static bool Prefix(RectTransform transform, ref Rect __result)
        {
            Camera camera = VR.VRGui.UiCamera;
            if (!VR.VRState.IsActive || camera == null || transform == null)
            {
                return true;
            }

            // 0 = bottom left, 2 = top right.
            transform.GetWorldCorners(Corners);
            Vector3 bottomLeft = camera.WorldToScreenPoint(Corners[0]);
            Vector3 topRight = camera.WorldToScreenPoint(Corners[2]);
            __result = new Rect(bottomLeft.x, Screen.height - topRight.y, topRight.x - bottomLeft.x, topRight.y - bottomLeft.y);
            return false;
        }
    }
}
