using System;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnturnedVR.VR;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Takes the crosshair's centre dot off the HUD. In VR the flat HUD's centre is not where anything is aimed (the guns
    /// aim with the hand, the head looks anywhere), so the dot only floats in the middle of the view. The game shows and
    /// hides it only through Crosshair.SetGameWantsCenterDotVisible / SetPluginAllowsCenterDotVisible, so a postfix on both
    /// hides the image again after each call. The game's own wants / allows flags are left as they are.
    /// The gun crosshair (the four spread lines around the centre, shown while a gun is equipped) goes the same way: the
    /// game shows it only through Crosshair.SetDirectionalArrowsVisible, and the postfix hides the four images again. The
    /// game's isGunCrosshairVisible flag stays, so its spread update keeps running on the hidden images.
    /// </summary>
    internal static class CrosshairCenterDotPatch
    {
        private static readonly FieldInfo dotField = typeof(Crosshair).GetField("centerDotImage", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo[] arrowFields =
        {
            typeof(Crosshair).GetField("crosshairLeftImage", BindingFlags.Instance | BindingFlags.NonPublic),
            typeof(Crosshair).GetField("crosshairRightImage", BindingFlags.Instance | BindingFlags.NonPublic),
            typeof(Crosshair).GetField("crosshairDownImage", BindingFlags.Instance | BindingFlags.NonPublic),
            typeof(Crosshair).GetField("crosshairUpImage", BindingFlags.Instance | BindingFlags.NonPublic),
        };
        private static bool logged;
        private static bool failed;
        private static bool arrowsLogged;
        private static bool arrowsFailed;

        [HarmonyPatch(typeof(Crosshair), nameof(Crosshair.SetDirectionalArrowsVisible))]
        internal static class DirectionalArrows
        {
            private static void Postfix(Crosshair __instance, bool isVisible)
            {
                if (isVisible)
                {
                    HideArrows(__instance);
                }
            }
        }

        private static void HideArrows(Crosshair crosshair)
        {
            if (arrowsFailed)
            {
                return;
            }

            try
            {
                foreach (FieldInfo field in arrowFields)
                {
                    ISleekImage arrow = field != null ? field.GetValue(crosshair) as ISleekImage : null;
                    if (arrow == null)
                    {
                        arrowsFailed = true;
                        VRLog.Warn("crosshair: a Crosshair spread line image not found, the gun crosshair stays on the HUD.");
                        return;
                    }

                    arrow.IsVisible = false;
                }

                if (!arrowsLogged)
                {
                    arrowsLogged = true;
                    VRLog.Info("crosshair: gun crosshair hidden on the HUD.");
                }
            }
            catch (Exception e)
            {
                arrowsFailed = true;
                VRLog.Warn("crosshair: could not hide the gun crosshair: " + e.Message);
            }
        }

        [HarmonyPatch(typeof(Crosshair), nameof(Crosshair.SetGameWantsCenterDotVisible))]
        internal static class GameWants
        {
            private static void Postfix(Crosshair __instance)
            {
                Hide(__instance);
            }
        }

        [HarmonyPatch(typeof(Crosshair), nameof(Crosshair.SetPluginAllowsCenterDotVisible))]
        internal static class PluginAllows
        {
            private static void Postfix(Crosshair __instance)
            {
                Hide(__instance);
            }
        }

        private static void Hide(Crosshair crosshair)
        {
            if (failed)
            {
                return;
            }

            try
            {
                ISleekImage dot = dotField != null ? dotField.GetValue(crosshair) as ISleekImage : null;
                if (dot == null)
                {
                    failed = true;
                    VRLog.Warn("crosshair: Crosshair.centerDotImage not found, the centre dot stays on the HUD.");
                    return;
                }

                if (dot.IsVisible)
                {
                    dot.IsVisible = false;
                    if (!logged)
                    {
                        logged = true;
                        VRLog.Info("crosshair: centre dot hidden on the HUD.");
                    }
                }
            }
            catch (Exception e)
            {
                failed = true;
                VRLog.Warn("crosshair: could not hide the centre dot: " + e.Message);
            }
        }
    }
}
