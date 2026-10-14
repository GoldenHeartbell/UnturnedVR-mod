using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The "VR" page of the escape menu: the mod's own settings, built from the game's Sleek widgets like the game's
    /// own option pages (MenuConfigurationAudioUI is the model), so the laser clicks it like any other page and it
    /// is drawn on the same panel. Reached from a button that PlayerPauseUIPatch adds to the pause menu.
    /// While the page is open the game's PlayerPauseUI.active stays true (the pause container is only moved out of
    /// view): the game's cursor, time pause and blur rules key on that flag and do not know about this page.
    /// </summary>
    internal sealed class VRSettingsPage : SleekFullscreenBox
    {
        private const float ToggleLeft = 0f;

        private readonly SleekFullscreenBox pauseContainer;
        private ISleekToggle markersToggle;
        private SleekButtonState attachModeButton;
        private SleekButtonState buildAimButton;
        private SleekButtonState wristStatusButton;
        private SleekButtonState vehicleDashButton;
        private SleekButtonState gunDisplayButton;
        private ISleekSlider rotateSpeedSlider;

        /// <summary>The page is on screen (the pause menu is hidden behind it).</summary>
        public bool Active { get; private set; }

        public VRSettingsPage(SleekFullscreenBox pauseContainer)
        {
            this.pauseContainer = pauseContainer;

            // Same geometry as the game's option pages: fills the window, starts below the screen.
            PositionOffset_X = 10;
            PositionOffset_Y = 10;
            PositionScale_Y = 1;
            SizeOffset_X = -20;
            SizeOffset_Y = -20;
            SizeScale_X = 1;
            SizeScale_Y = 1;

            ISleekScrollView box = Glazier.Get().CreateScrollView();
            box.PositionOffset_X = -200;
            box.PositionOffset_Y = 100;
            box.PositionScale_X = 0.5f;
            box.SizeOffset_X = 430;
            box.SizeOffset_Y = -200;
            box.SizeScale_Y = 1;
            box.ScaleContentToWidth = true;
            AddChild(box);

            int verticalOffset = 0;

            ISleekBox title = Glazier.Get().CreateBox();
            title.PositionOffset_Y = verticalOffset;
            title.SizeOffset_X = 400;
            title.SizeOffset_Y = 30;
            title.Text = "UnturnedVR";
            box.AddChild(title);
            verticalOffset += 40;

            ISleekBox holsters = Glazier.Get().CreateBox();
            holsters.PositionOffset_Y = verticalOffset;
            holsters.SizeOffset_X = 400;
            holsters.SizeOffset_Y = 30;
            holsters.Text = "Holsters";
            box.AddChild(holsters);
            verticalOffset += 40;

            markersToggle = Glazier.Get().CreateToggle();
            markersToggle.PositionOffset_X = ToggleLeft;
            markersToggle.PositionOffset_Y = verticalOffset;
            markersToggle.SizeOffset_X = 40;
            markersToggle.SizeOffset_Y = 40;
            markersToggle.AddLabel("Show holster zones", ESleekSide.RIGHT);
            markersToggle.TooltipText = "Draw the holster, shoulder and ear zones as rings around your body.";
            markersToggle.Value = VRHolsters.MarkersEnabled;
            markersToggle.OnValueChanged += OnMarkersToggled;
            box.AddChild(markersToggle);
            verticalOffset += 50;

            ISleekBox controls = Glazier.Get().CreateBox();
            controls.PositionOffset_Y = verticalOffset;
            controls.SizeOffset_X = 400;
            controls.SizeOffset_Y = 30;
            controls.Text = "Controls";
            box.AddChild(controls);
            verticalOffset += 40;

            // Same kind of control the game uses for aiming, crouching, sprinting...: click it to switch between Hold and
            // Toggle (right click goes back). Order and labels follow the game's own (Hold, then Toggle).
            attachModeButton = new SleekButtonState(new GUIContent("Hold"), new GUIContent("Toggle"));
            attachModeButton.PositionOffset_Y = verticalOffset;
            attachModeButton.SizeOffset_X = 200;
            attachModeButton.SizeOffset_Y = 30;
            attachModeButton.AddLabel("Attachment menu", ESleekSide.RIGHT);
            attachModeButton.tooltip = "Toggle: a long press of B opens the attachment menu and the next press closes it. Hold: it is open only while B is held.";
            attachModeButton.state = AttachModeState(VRSettings.AttachMenuHold);
            attachModeButton.onSwappedState = OnAttachModeSwapped;
            box.AddChild(attachModeButton);
            verticalOffset += 40;

            // Building (barricades, structures, housing planner): what the placement aims with. Head based is what it
            // always was and the default; states: 0 = Head based, 1 = Hand based.
            buildAimButton = new SleekButtonState(new GUIContent("Head based"), new GUIContent("Hand based"));
            buildAimButton.PositionOffset_Y = verticalOffset;
            buildAimButton.SizeOffset_X = 200;
            buildAimButton.SizeOffset_Y = 30;
            buildAimButton.AddLabel("Building aim", ESleekSide.RIGHT);
            buildAimButton.tooltip = "Head based: placement follows where you look. Hand based: placement follows where the hand holding the item points.";
            buildAimButton.state = VRSettings.BuildingHandBased ? 1 : 0;
            buildAimButton.onSwappedState = OnBuildAimSwapped;
            box.AddChild(buildAimButton);
            verticalOffset += 40;

            // How fast the right stick turns a barricade that is being placed. The slider is 0..1; its left end is the
            // original speed (100%) and its right end three times as fast (300%).
            rotateSpeedSlider = Glazier.Get().CreateSlider();
            rotateSpeedSlider.PositionOffset_Y = verticalOffset;
            rotateSpeedSlider.SizeOffset_X = 200;
            rotateSpeedSlider.SizeOffset_Y = 20;
            rotateSpeedSlider.Orientation = ESleekOrientation.HORIZONTAL;
            rotateSpeedSlider.Value = RotateSpeedToSlider(VRSettings.BuildingRotateSpeedPercent);
            rotateSpeedSlider.AddLabel(RotateSpeedLabel(VRSettings.BuildingRotateSpeedPercent), ESleekSide.RIGHT);
            rotateSpeedSlider.OnValueChanged += OnRotateSpeedDragged;
            box.AddChild(rotateSpeedSlider);
            verticalOffset += 40;

            ISleekBox hud = Glazier.Get().CreateBox();
            hud.PositionOffset_Y = verticalOffset;
            hud.SizeOffset_X = 400;
            hud.SizeOffset_Y = 30;
            hud.Text = "HUD";
            box.AddChild(hud);
            verticalOffset += 40;

            // Health, food, water and the status icons: on the left wrist (look at it) plus a short reveal at the edge of
            // the view when something needs attention, or left on the flat HUD panel. States: 0 = Wrist, 1 = HUD panel.
            wristStatusButton = new SleekButtonState(new GUIContent("Wrist"), new GUIContent("HUD panel"));
            wristStatusButton.PositionOffset_Y = verticalOffset;
            wristStatusButton.SizeOffset_X = 200;
            wristStatusButton.SizeOffset_Y = 30;
            wristStatusButton.AddLabel("Status display", ESleekSide.RIGHT);
            wristStatusButton.tooltip = "Wrist: health, food, water and status icons are on your left wrist (look at it with the hand free) and fade in at the edge of the view when something needs attention. HUD panel: they stay on the flat HUD.";
            wristStatusButton.state = VRSettings.WristStatus ? 0 : 1;
            wristStatusButton.onSwappedState = OnWristStatusSwapped;
            box.AddChild(wristStatusButton);
            verticalOffset += 40;

            // The vehicle box (fuel, speed, health): a dashboard to the right of the wheel, or left on the flat HUD panel.
            vehicleDashButton = new SleekButtonState(new GUIContent("Dashboard"), new GUIContent("HUD panel"));
            vehicleDashButton.PositionOffset_Y = verticalOffset;
            vehicleDashButton.SizeOffset_X = 200;
            vehicleDashButton.SizeOffset_Y = 30;
            vehicleDashButton.AddLabel("Vehicle display", ESleekSide.RIGHT);
            vehicleDashButton.tooltip = "Dashboard: fuel, speed and vehicle health sit in the vehicle to the right of the steering wheel, fixed in place. HUD panel: they stay on the flat HUD.";
            vehicleDashButton.state = VRSettings.VehicleDash ? 0 : 1;
            vehicleDashButton.onSwappedState = OnVehicleDashSwapped;
            box.AddChild(vehicleDashButton);
            verticalOffset += 40;

            // The gun's box (ammo, fire mode, magazine): on the inside of the gun (twist it to look), or left on the flat HUD
            // panel. States: 0 = Gun side, 1 = HUD panel.
            gunDisplayButton = new SleekButtonState(new GUIContent("Gun side"), new GUIContent("HUD panel"));
            gunDisplayButton.PositionOffset_Y = verticalOffset;
            gunDisplayButton.SizeOffset_X = 200;
            gunDisplayButton.SizeOffset_Y = 30;
            gunDisplayButton.AddLabel("Gun display", ESleekSide.RIGHT);
            gunDisplayButton.tooltip = "Gun side: ammo, fire mode and magazine are on the inside of the gun, seen when you twist the gun inwards. HUD panel: they stay on the flat HUD.";
            gunDisplayButton.state = VRSettings.GunDisplay ? 0 : 1;
            gunDisplayButton.onSwappedState = OnGunDisplaySwapped;
            box.AddChild(gunDisplayButton);
            verticalOffset += 40;

            box.ContentSizeOffset = new Vector2(0f, verticalOffset - 10);

            SleekButtonIcon backButton = new SleekButtonIcon(null);
            backButton.PositionOffset_Y = -50;
            backButton.PositionScale_Y = 1f;
            backButton.SizeOffset_X = 200;
            backButton.SizeOffset_Y = 50;
            backButton.text = "Back";
            backButton.tooltip = "Back to the pause menu";
            backButton.onClickedButton += OnBackClicked;
            backButton.fontSize = ESleekFontSize.Medium;
            backButton.iconColor = ESleekTint.FOREGROUND;
            AddChild(backButton);
        }

        public void Open()
        {
            if (Active)
            {
                return;
            }

            Active = true;
            markersToggle.Value = VRHolsters.MarkersEnabled;
            attachModeButton.state = AttachModeState(VRSettings.AttachMenuHold);
            buildAimButton.state = VRSettings.BuildingHandBased ? 1 : 0;
            wristStatusButton.state = VRSettings.WristStatus ? 0 : 1;
            vehicleDashButton.state = VRSettings.VehicleDash ? 0 : 1;
            gunDisplayButton.state = VRSettings.GunDisplay ? 0 : 1;
            rotateSpeedSlider.Value = RotateSpeedToSlider(VRSettings.BuildingRotateSpeedPercent);
            rotateSpeedSlider.UpdateLabel(RotateSpeedLabel(VRSettings.BuildingRotateSpeedPercent));
            pauseContainer.AnimateOutOfView(0, 1);
            AnimateIntoView();
            VRLog.Info("settings page: opened.");
        }

        /// <summary>Closes the page. The pause menu comes back unless the pause menu itself is closing (death, other UI).</summary>
        public void Close(bool showPauseMenu)
        {
            if (!Active)
            {
                return;
            }

            Active = false;
            VRSettings.Flush();
            AnimateOutOfView(0, 1);
            if (showPauseMenu)
            {
                pauseContainer.AnimateIntoView();
            }
            VRLog.Info("settings page: closed.");
        }

        private void OnBackClicked(ISleekElement button)
        {
            Close(true);
        }

        private void OnBuildAimSwapped(SleekButtonState button, int index)
        {
            bool hand = index == 1;
            VRSettings.SetBuildingHandBased(hand);
            VRLog.Info("settings page: building aim " + (hand ? "hand based" : "head based") + ".");
        }

        private void OnWristStatusSwapped(SleekButtonState button, int index)
        {
            bool wrist = index == 0;
            VRSettings.SetWristStatus(wrist);
            VRLog.Info("settings page: status display " + (wrist ? "wrist" : "HUD panel") + ".");
        }

        private void OnVehicleDashSwapped(SleekButtonState button, int index)
        {
            bool dash = index == 0;
            VRSettings.SetVehicleDash(dash);
            VRLog.Info("settings page: vehicle display " + (dash ? "dashboard" : "HUD panel") + ".");
        }

        private void OnGunDisplaySwapped(SleekButtonState button, int index)
        {
            bool side = index == 0;
            VRSettings.SetGunDisplay(side);
            VRLog.Info("settings page: gun display " + (side ? "gun side" : "HUD panel") + ".");
        }

        private static float RotateSpeedToSlider(int percent)
        {
            return (float)(percent - VRSettings.BuildingRotateSpeedMinPercent)
                / (VRSettings.BuildingRotateSpeedMaxPercent - VRSettings.BuildingRotateSpeedMinPercent);
        }

        private static string RotateSpeedLabel(int percent)
        {
            return "Building rotate speed: " + percent + "%";
        }

        private void OnRotateSpeedDragged(ISleekSlider slider, float state)
        {
            int range = VRSettings.BuildingRotateSpeedMaxPercent - VRSettings.BuildingRotateSpeedMinPercent;
            int percent = VRSettings.BuildingRotateSpeedMinPercent + Mathf.RoundToInt(state * range);
            VRSettings.SetBuildingRotateSpeedPercent(percent);
            rotateSpeedSlider.UpdateLabel(RotateSpeedLabel(VRSettings.BuildingRotateSpeedPercent));
        }

        // Button states: 0 = Hold, 1 = Toggle (the game's order). Toggle is the mod's original behaviour and the default.
        private static int AttachModeState(bool hold)
        {
            return hold ? 0 : 1;
        }

        private void OnAttachModeSwapped(SleekButtonState button, int index)
        {
            bool hold = index == 0;
            VRSettings.SetAttachMenuHold(hold);
            VRLog.Info("settings page: attachment menu " + (hold ? "hold" : "toggle") + " mode.");
        }

        private void OnMarkersToggled(ISleekToggle toggle, bool state)
        {
            VRHolsters.SetMarkersEnabled(state);
            VRLog.Info("settings page: holster zone rings " + (state ? "on" : "off") + ".");
        }
    }
}
