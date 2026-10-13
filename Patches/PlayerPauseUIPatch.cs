using System;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnturnedVR.VR;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Adds a "VR" button to the in-game pause menu (PlayerPauseUI, built once per local player spawn) that opens the
    /// mod's settings page (VRSettingsPage). The button goes right after Audio; suicide, exit and quit move down one
    /// row. Everything in PlayerPauseUI is static and private, so its widgets are read by reflection. Escape and
    /// death are handled by PlayerUIEscapePatch and the PlayerPauseUI.close postfix below.
    /// </summary>
    internal static class PlayerPauseUIPatch
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const float RowHeight = 60f;
        private const float SpyShiftX = -435f; // the same shift the game gives its buttons while the spy picture is shown

        /// <summary>The page of the current local player, or null before the pause menu exists.</summary>
        internal static VRSettingsPage Page { get; private set; }

        private static SleekButtonIcon vrButton;

        private static T Get<T>(string name) where T : class
        {
            FieldInfo field = typeof(PlayerPauseUI).GetField(name, Any);
            return field != null ? field.GetValue(null) as T : null;
        }

        [HarmonyPatch(typeof(PlayerPauseUI), MethodType.Constructor)]
        internal static class Constructed
        {
            private static void Postfix()
            {
                try
                {
                    Build();
                }
                catch (Exception e)
                {
                    VRLog.Warn("settings page: could not be added to the pause menu: " + e.Message);
                }
            }
        }

        private static void Build()
        {
            SleekFullscreenBox container = Get<SleekFullscreenBox>("container");
            SleekButtonIcon audioButton = Get<SleekButtonIcon>("audioButton");
            SleekButtonIconConfirm suicideButton = Get<SleekButtonIconConfirm>("suicideButton");
            ISleekLabel suicideDisabledLabel = Get<ISleekLabel>("suicideDisabledLabel");
            if (container == null || audioButton == null || suicideButton == null || PlayerPauseUI.exitButton == null || PlayerPauseUI.quitButton == null)
            {
                VRLog.Warn("settings page: pause menu widgets not found, no VR button.");
                return;
            }

            // The page lives next to the pause menu, in the player UI container (like the game's audio page).
            Page = new VRSettingsPage(container);
            PlayerUI.container.AddChild(Page);

            // 40 px like the game's own icons next to a 50 px button; the headset glyph is drawn by the mod (VRIcons).
            vrButton = new SleekButtonIcon(VRIcons.Hmd, 40);
            vrButton.PositionOffset_X = -100;
            vrButton.PositionOffset_Y = audioButton.PositionOffset_Y + RowHeight;
            vrButton.PositionScale_X = 0.5f;
            vrButton.PositionScale_Y = 0.5f;
            vrButton.SizeOffset_X = 200;
            vrButton.SizeOffset_Y = 50;
            vrButton.text = "VR settings";
            vrButton.tooltip = "UnturnedVR settings";
            vrButton.iconColor = ESleekTint.FOREGROUND;
            vrButton.onClickedButton += OnVrClicked;
            vrButton.fontSize = ESleekFontSize.Medium;
            container.AddChild(vrButton);

            // Make room: the rows below Audio move down one row.
            suicideButton.PositionOffset_Y += RowHeight;
            if (suicideDisabledLabel != null)
            {
                suicideDisabledLabel.PositionOffset_Y += RowHeight;
            }
            PlayerPauseUI.exitButton.PositionOffset_Y += RowHeight;
            PlayerPauseUI.quitButton.PositionOffset_Y += RowHeight;

            VRLog.Info("settings page: VR button added to the pause menu.");
        }

        private static void OnVrClicked(ISleekElement button)
        {
            if (Page != null)
            {
                Page.Open();
            }
        }

        // The spy picture moves every button left; the new one has to follow.
        [HarmonyPatch(typeof(PlayerPauseUI), "onSpyReady")]
        internal static class SpyReady
        {
            private static void Postfix()
            {
                if (vrButton != null)
                {
                    vrButton.PositionOffset_X = SpyShiftX;
                }
            }
        }

        // Death and every other caller that closes the pause menu take the page down with it.
        [HarmonyPatch(typeof(PlayerPauseUI), nameof(PlayerPauseUI.close))]
        internal static class Closed
        {
            private static void Postfix()
            {
                if (Page != null && Page.Active)
                {
                    Page.Close(false);
                }
            }
        }
    }

    /// <summary>
    /// Escape / the controller's pause button: while the VR page is open it goes back to the pause menu (one step, like the
    /// game's own option pages), instead of the game opening the pause menu on top of it.
    /// </summary>
    [HarmonyPatch(typeof(PlayerUI), "escapeMenu")]
    internal static class PlayerUIEscapePatch
    {
        private static bool Prefix()
        {
            VRSettingsPage page = PlayerPauseUIPatch.Page;
            if (page != null && page.Active)
            {
                page.Close(true);
                return false;
            }
            return true;
        }
    }
}
