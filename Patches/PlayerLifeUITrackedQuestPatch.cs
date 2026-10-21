using System;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Takes the tracked quest (title and conditions, top right) off the HUD in VR. PlayerLifeUI.UpdateTrackedQuest
    /// (internal; runs when the tracked quest or its progress changes and when the HUD opens) sets both widgets visible
    /// again on every call, so the postfix hides them again after each one. Tracking itself is unchanged: the quest list
    /// on the Information page still shows it.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerLifeUITrackedQuestPatch
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo titleField = typeof(PlayerLifeUI).GetField("trackedQuestTitle", Any);
        private static readonly FieldInfo barField = typeof(PlayerLifeUI).GetField("trackedQuestBar", Any);
        private static bool logged;
        private static bool failed;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PlayerLifeUI), "UpdateTrackedQuest");
        }

        private static bool Prepare()
        {
            if (TargetMethod() != null)
            {
                return true;
            }

            VRLog.Warn("tracked quest: PlayerLifeUI.UpdateTrackedQuest not found, the tracked quest stays on the HUD.");
            return false;
        }

        private static void Postfix()
        {
            if (failed || !VR.VRState.IsActive)
            {
                return;
            }

            try
            {
                ISleekElement title = titleField != null ? titleField.GetValue(null) as ISleekElement : null;
                ISleekElement bar = barField != null ? barField.GetValue(null) as ISleekElement : null;
                if (title == null || bar == null)
                {
                    failed = true;
                    VRLog.Warn("tracked quest: PlayerLifeUI.trackedQuestTitle / trackedQuestBar not found, the tracked quest stays on the HUD.");
                    return;
                }

                if (title.IsVisible || bar.IsVisible)
                {
                    title.IsVisible = false;
                    bar.IsVisible = false;
                    if (!logged)
                    {
                        logged = true;
                        VRLog.Info("tracked quest: hidden on the HUD (still listed on the Information page).");
                    }
                }
            }
            catch (Exception e)
            {
                failed = true;
                VRLog.Warn("tracked quest: could not hide the tracked quest: " + e.Message);
            }
        }
    }
}
