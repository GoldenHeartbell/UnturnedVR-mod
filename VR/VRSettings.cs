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

        private const string BuildingRotateSpeedKey = "UnturnedVR.controls.buildingRotateSpeed";

        /// <summary>Slowest and fastest barricade turn speed on the settings page, in percent of the original speed.</summary>
        public const int BuildingRotateSpeedMinPercent = 100;
        public const int BuildingRotateSpeedMaxPercent = 300;

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

        private static int ClampPercent(long percent)
        {
            if (percent < BuildingRotateSpeedMinPercent)
            {
                return BuildingRotateSpeedMinPercent;
            }
            return percent > BuildingRotateSpeedMaxPercent ? BuildingRotateSpeedMaxPercent : (int)percent;
        }

        /// <summary>Writes the game's savedata file now if something changed (the game also does it on its own schedule).</summary>
        public static void Flush()
        {
            ConvenientSavedata.SaveIfDirty();
        }
    }
}
