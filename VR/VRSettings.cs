using SDG.Unturned;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The mod's player settings that are edited on the VR settings page (VRSettingsPage). Stored in the game's own
    /// ConvenientSavedata (Cloud/ConvenientSavedata.json, keys "UnturnedVR.*"): the game serializes it, ignores keys it
    /// does not know and keeps them, so removing the mod leaves a few unused entries and nothing else. There is no
    /// polling: values are read once and written when the page changes them.
    /// A setting that was never changed on the page has no saved value, so the older source of the value (a line in
    /// holsters.txt) still decides until the page is used.
    /// </summary>
    internal static class VRSettings
    {
        private const string HolsterMarkersKey = "UnturnedVR.holsters.markers";

        private const string AttachMenuHoldKey = "UnturnedVR.controls.attachMenuHold";
        private const string BuildingHandKey = "UnturnedVR.controls.buildingHand";

        private const string TurretControllerOnlyKey = "UnturnedVR.controls.turretControllerOnly";
        private static bool turretControllerOnlyLoaded;
        private static bool turretControllerOnlyValue;

        private const string BuildingRotateSpeedKey = "UnturnedVR.controls.buildingRotateSpeed";

        private const string HallucinationPitchFlipKey = "UnturnedVR.comfort.hallucinationPitchFlip";
        private static bool pitchFlipLoaded;
        private static bool pitchFlipValue;

        /// <summary>Slowest and fastest barricade turn speed on the settings page, in percent of the original speed.</summary>
        public const int BuildingRotateSpeedMinPercent = 100;
        public const int BuildingRotateSpeedMaxPercent = 300;

        private const string WristStatusKey = "UnturnedVR.hud.wristStatus";
        private const string VehicleDashKey = "UnturnedVR.hud.vehicleDash";
        private const string WristCompassKey = "UnturnedVR.hud.wristCompass";

        private const string MenuCameraFollowKey = "UnturnedVR.comfort.menuCameraFollow";
        private static bool menuCameraFollowLoaded;
        private static bool menuCameraFollowValue = true;

        private const string WaterReflectionsKey = "UnturnedVR.graphics.waterReflections"; // first version: on / off
        private const string WaterReflectionLevelKey = "UnturnedVR.graphics.waterReflectionLevel";
        private static bool waterReflectionsLoaded;
        private static int waterReflectionLevelValue = WaterReflectionLevelMax;

        private const string GunDisplayKey = "UnturnedVR.hud.gunDisplay";

        private const string GunDisplayModeKey = "UnturnedVR.hud.gunDisplayMode";

        private static bool gunDisplayLoaded;
        private static bool gunDisplayValue = true;
        private static bool dashLoaded;
        private static bool dashValue = true;
        private static bool wristLoaded;
        private static bool wristValue = true;
        private static bool compassLoaded;
        private static bool compassValue = true;
        private static bool markersLoaded;
        private static bool markersSaved;
        private static bool markersValue;
        private static bool attachHoldLoaded;
        private static bool attachHoldValue;
        private static bool buildHandLoaded;
        private static bool buildHandValue;
        private static bool rotateSpeedLoaded;
        private static int rotateSpeedPercent = BuildingRotateSpeedMinPercent;

        /// <summary>The holster / ear zone rings are drawn in the world.</summary>
        /// <param name="fallback">What holsters.txt says, used until the page has saved a value.</param>
        public static bool HolsterMarkers(bool fallback)
        {
            if (!markersLoaded)
            {
                markersLoaded = true;
                markersSaved = ConvenientSavedata.get().read(HolsterMarkersKey, out markersValue);
            }
            return markersSaved ? markersValue : fallback;
        }

        public static void SetHolsterMarkers(bool on)
        {
            markersLoaded = true;
            markersSaved = true;
            markersValue = on;
            ConvenientSavedata.get().write(HolsterMarkersKey, on);
        }

        /// <summary>
        /// Attachment menu (long press B on the gun hand): false = it stays open until B is pressed again (the original
        /// behaviour, the default), true = it is open only while B is held and closes on release.
        /// </summary>
        public static bool AttachMenuHold
        {
            get
            {
                if (!attachHoldLoaded)
                {
                    attachHoldLoaded = true;
                    ConvenientSavedata.get().read(AttachMenuHoldKey, out attachHoldValue); // missing = false
                }
                return attachHoldValue;
            }
        }

        public static void SetAttachMenuHold(bool hold)
        {
            attachHoldLoaded = true;
            attachHoldValue = hold;
            ConvenientSavedata.get().write(AttachMenuHoldKey, hold);
        }

        /// <summary>
        /// Building (barricades, structures, housing planner): false = placement aims from the head (the default, as it
        /// always was), true = from the hand that holds the item.
        /// </summary>
        public static bool BuildingHandBased
        {
            get
            {
                if (!buildHandLoaded)
                {
                    buildHandLoaded = true;
                    ConvenientSavedata.get().read(BuildingHandKey, out buildHandValue); // missing = false
                }
                return buildHandValue;
            }
        }

        public static void SetBuildingHandBased(bool hand)
        {
            buildHandLoaded = true;
            buildHandValue = hand;
            ConvenientSavedata.get().write(BuildingHandKey, hand);
        }

        /// <summary>
        /// Vehicle turrets (the tank's gunner seat...): false (the default) = the turret turns left / right with the head and
        /// the right stick, true = only the right stick turns it left / right. Up / down follows the head either way.
        /// </summary>
        public static bool TurretControllerOnly
        {
            get
            {
                if (!turretControllerOnlyLoaded)
                {
                    turretControllerOnlyLoaded = true;
                    ConvenientSavedata.get().read(TurretControllerOnlyKey, out turretControllerOnlyValue); // missing = false
                }
                return turretControllerOnlyValue;
            }
        }

        public static void SetTurretControllerOnly(bool controllerOnly)
        {
            turretControllerOnlyLoaded = true;
            turretControllerOnlyValue = controllerOnly;
            ConvenientSavedata.get().write(TurretControllerOnlyKey, controllerOnly);
        }

        /// <summary>
        /// How fast the right stick turns a barricade that is being placed, in percent of the original speed:
        /// 100 (the default and the minimum) up to 300 (three times as fast).
        /// </summary>
        public static int BuildingRotateSpeedPercent
        {
            get
            {
                if (!rotateSpeedLoaded)
                {
                    rotateSpeedLoaded = true;
                    long saved;
                    if (ConvenientSavedata.get().read(BuildingRotateSpeedKey, out saved)) // missing = 100
                    {
                        rotateSpeedPercent = ClampPercent(saved);
                    }
                }
                return rotateSpeedPercent;
            }
        }

        public static void SetBuildingRotateSpeedPercent(int percent)
        {
            rotateSpeedLoaded = true;
            rotateSpeedPercent = ClampPercent(percent);
            ConvenientSavedata.get().write(BuildingRotateSpeedKey, (long)rotateSpeedPercent);
        }

        /// <summary>
        /// While a hallucination flips the game's look input, also mirror the VR view's up/down (looking up shows the view
        /// going down), as the game does for the mouse. Off by default: it is the most nauseating of the effects. The turn
        /// (left/right) flip is not a setting, it is always applied.
        /// </summary>
        public static bool HallucinationPitchFlip
        {
            get
            {
                if (!pitchFlipLoaded)
                {
                    pitchFlipLoaded = true;
                    ConvenientSavedata.get().read(HallucinationPitchFlipKey, out pitchFlipValue); // missing = false
                }
                return pitchFlipValue;
            }
        }

        public static void SetHallucinationPitchFlip(bool on)
        {
            pitchFlipLoaded = true;
            pitchFlipValue = on;
            ConvenientSavedata.get().write(HallucinationPitchFlipKey, on);
        }

        /// <summary>
        /// The main menu's scripted camera motion (the fly-in when the menu opens, the pans between pages): true (the
        /// default) = the headset view rides along with it as the game plays it (position and heading), false = the view
        /// holds still and only adopts the camera's pose once it rests (the earlier comfort behaviour).
        /// </summary>
        public static bool MenuCameraFollow
        {
            get
            {
                if (!menuCameraFollowLoaded)
                {
                    menuCameraFollowLoaded = true;
                    bool saved;
                    if (ConvenientSavedata.get().read(MenuCameraFollowKey, out saved)) // missing = follow
                    {
                        menuCameraFollowValue = saved;
                    }
                }
                return menuCameraFollowValue;
            }
        }

        public static void SetMenuCameraFollow(bool follow)
        {
            menuCameraFollowLoaded = true;
            menuCameraFollowValue = follow;
            ConvenientSavedata.get().write(MenuCameraFollowKey, follow);
        }

        private const string WaterSkyReflectionKey = "UnturnedVR.graphics.waterSkyReflection";
        private static bool waterSkyReflectionLoaded;
        private static bool waterSkyReflectionValue = true;

        /// <summary>
        /// The sky (skybox and clouds) in the water reflection: true (the default) = drawn at the Ultra step, false = never
        /// drawn, the reflection then shows a flat sky colour as the lower steps do. The lower steps never draw it.
        /// </summary>
        public static bool WaterSkyReflection
        {
            get
            {
                if (!waterSkyReflectionLoaded)
                {
                    waterSkyReflectionLoaded = true;
                    bool saved;
                    if (ConvenientSavedata.get().read(WaterSkyReflectionKey, out saved)) // missing = on
                    {
                        waterSkyReflectionValue = saved;
                    }
                }
                return waterSkyReflectionValue;
            }
        }

        public static void SetWaterSkyReflection(bool on)
        {
            waterSkyReflectionLoaded = true;
            waterSkyReflectionValue = on;
            ConvenientSavedata.get().write(WaterSkyReflectionKey, on);
        }

        /// <summary>Highest water reflection level on the settings page: 0 Off, 1 Low, 2 Medium, 3 High, 4 Ultra.</summary>
        public const int WaterReflectionLevelMax = 4;

        /// <summary>
        /// The planar (mirrored) reflection of the world on water, in the game's own quality steps (0 Off, 1 Low,
        /// 2 Medium, 3 High, 4 Ultra, as EGraphicQuality). 4 (the default) = the game's own reflection exactly as its
        /// Planar Reflections option sets it, drawn once per eye in VR (StereoReflectionPatches). Lower = at most that
        /// quality (the game's culling mask for it), 0 = water without reflection and without the extra render. It is a
        /// cap: the game's own Planar Reflections and Water quality options still apply first and this can only lower
        /// what they give. A value saved by the first version (an on / off switch) still reads: off = 0.
        /// </summary>
        public static int WaterReflectionLevel
        {
            get
            {
                if (!waterReflectionsLoaded)
                {
                    waterReflectionsLoaded = true;
                    long level;
                    bool oldOn;
                    if (ConvenientSavedata.get().read(WaterReflectionLevelKey, out level))
                    {
                        waterReflectionLevelValue = level < 0 ? 0 : level > WaterReflectionLevelMax ? WaterReflectionLevelMax : (int)level;
                    }
                    else if (ConvenientSavedata.get().read(WaterReflectionsKey, out oldOn) && !oldOn)
                    {
                        waterReflectionLevelValue = 0;
                    }
                }
                return waterReflectionLevelValue;
            }
        }

        /// <summary>The reflection is drawn at all (level above Off).</summary>
        public static bool WaterReflections => WaterReflectionLevel > 0;

        public static void SetWaterReflectionLevel(int level)
        {
            waterReflectionsLoaded = true;
            waterReflectionLevelValue = level < 0 ? 0 : level > WaterReflectionLevelMax ? WaterReflectionLevelMax : level;
            ConvenientSavedata.get().write(WaterReflectionLevelKey, (long)waterReflectionLevelValue);
        }

        private static int ClampPercent(long percent)
        {
            if (percent < BuildingRotateSpeedMinPercent)
            {
                return BuildingRotateSpeedMinPercent;
            }
            return percent > BuildingRotateSpeedMaxPercent ? BuildingRotateSpeedMaxPercent : (int)percent;
        }

        /// <summary>
        /// Health, food, water... and the status icons: true (the default) = on the left wrist plus a glance reveal at the
        /// edge of the view (VRWristStatus), false = they stay on the flat HUD panel like the rest of the HUD.
        /// </summary>
        public static bool WristStatus
        {
            get
            {
                if (!wristLoaded)
                {
                    wristLoaded = true;
                    bool saved;
                    if (ConvenientSavedata.get().read(WristStatusKey, out saved)) // missing = on
                    {
                        wristValue = saved;
                    }
                }
                return wristValue;
            }
        }

        public static void SetWristStatus(bool on)
        {
            wristLoaded = true;
            wristValue = on;
            ConvenientSavedata.get().write(WristStatusKey, on);
        }

        /// <summary>
        /// The compass tape: true (the default) = on the left wrist panel above the status (VRWristStatus), false = it stays
        /// on the flat HUD panel. Independent of <see cref="WristStatus"/>.
        /// </summary>
        public static bool WristCompass
        {
            get
            {
                if (!compassLoaded)
                {
                    compassLoaded = true;
                    bool saved;
                    if (ConvenientSavedata.get().read(WristCompassKey, out saved)) // missing = on
                    {
                        compassValue = saved;
                    }
                }
                return compassValue;
            }
        }

        public static void SetWristCompass(bool on)
        {
            compassLoaded = true;
            compassValue = on;
            ConvenientSavedata.get().write(WristCompassKey, on);
        }

        /// <summary>
        /// The vehicle box (fuel, speed, health...): true (the default) = a dashboard in the vehicle to the right of the
        /// wheel (VRWristStatus), and in a turret seat the turret gun's ammo box on the turret; false = they stay on the flat HUD panel.
        /// </summary>
        public static bool VehicleDash
        {
            get
            {
                if (!dashLoaded)
                {
                    dashLoaded = true;
                    bool saved;
                    if (ConvenientSavedata.get().read(VehicleDashKey, out saved)) // missing = on
                    {
                        dashValue = saved;
                    }
                }
                return dashValue;
            }
        }

        public static void SetVehicleDash(bool on)
        {
            dashLoaded = true;
            dashValue = on;
            ConvenientSavedata.get().write(VehicleDashKey, on);
        }

        // The saved value is the old three-state mode number (0 gun side, 1 behind grip, 2 HUD panel), kept so saved settings
        // still read: 2 means the box stays on the HUD panel, anything else means it is on the gun's side.
        private const long GunDisplayModeSide = 0;
        private const long GunDisplayModeHud = 2;

        /// <summary>
        /// The gun's ammo / fire mode / magazine box is shown on the inner side of the gun, seen when the gun is twisted
        /// inwards (VRWristStatus), instead of staying on the flat HUD panel. The default.
        /// </summary>
        public static bool GunDisplay
        {
            get
            {
                if (!gunDisplayLoaded)
                {
                    gunDisplayLoaded = true;
                    long mode;
                    bool old;
                    if (ConvenientSavedata.get().read(GunDisplayModeKey, out mode))
                    {
                        gunDisplayValue = mode != GunDisplayModeHud;
                    }
                    else if (ConvenientSavedata.get().read(GunDisplayKey, out old))
                    {
                        gunDisplayValue = old; // the first version's on / off setting
                    }
                }
                return gunDisplayValue;
            }
        }

        public static void SetGunDisplay(bool on)
        {
            gunDisplayLoaded = true;
            gunDisplayValue = on;
            ConvenientSavedata.get().write(GunDisplayModeKey, on ? GunDisplayModeSide : GunDisplayModeHud);
        }

        /// <summary>Writes the game's savedata file now if something changed (the game also does it on its own schedule).</summary>
        public static void Flush()
        {
            ConvenientSavedata.SaveIfDirty();
        }
    }
}
