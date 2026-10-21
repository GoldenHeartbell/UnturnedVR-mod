using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Building (barricades, structures, the housing planner) and the fertilizer (UseableGrower) aim with player.look.aim, which in VR follows the
    /// head. With the "Building: Hand based" control setting (VRSettings.BuildingHandBased) the placement checks run
    /// with that aim, and for barricades the main camera's forward too (it orients wall mounted items), moved to
    /// the hand that holds the item (the same hand as the interact ray), for the duration of the call only; both
    /// are put back afterwards, like the muzzle and interact patches. Head based (the default) changes nothing.
    /// The check methods run every frame while a building item is equipped (the preview) and on the click that places,
    /// so the preview and the placement agree. Cost: two transform writes per frame while a building item is in hand.
    /// </summary>
    internal static class BuildingHandAim
    {
        internal struct Saved
        {
            public bool Swapped;
            public Vector3 AimPosition;
            public Quaternion AimRotation;
            public bool CameraSwapped;
            public Vector3 CameraPosition;
            public Quaternion CameraRotation;
        }

        private static bool logged;

        public static Saved Begin(Player player, SteamChannel channel, bool alsoMainCamera)
        {
            var saved = new Saved();
            if (!VR.VRState.IsActive || !VR.VRSettings.BuildingHandBased || channel == null || !channel.IsLocalPlayer)
            {
                return saved;
            }

            Transform aim = player != null && player.look != null ? player.look.aim : null;
            int handIndex = VR.VRKeyMap.InteractHand; // the holding hand
            Transform hand = VR.VRHands.Anchor(handIndex);
            if (aim == null || hand == null || !VR.VRHands.IsTracked(handIndex))
            {
                return saved; // no usable hand pose: stay head based for this call
            }

            saved.Swapped = true;
            saved.AimPosition = aim.position;
            saved.AimRotation = aim.rotation;
            aim.SetPositionAndRotation(hand.position, hand.rotation);

            Camera main = MainCamera.instance;
            if (alsoMainCamera && main != null)
            {
                Transform t = main.transform;
                saved.CameraSwapped = true;
                saved.CameraPosition = t.position;
                saved.CameraRotation = t.rotation;
                t.SetPositionAndRotation(hand.position, hand.rotation);
            }

            if (!logged)
            {
                logged = true;
                VRLog.Info("building: placement aims from the hand (Hand based control setting).");
            }
            return saved;
        }

        public static void End(Player player, Saved saved)
        {
            if (!saved.Swapped)
            {
                return;
            }

            if (player != null && player.look != null && player.look.aim != null)
            {
                player.look.aim.SetPositionAndRotation(saved.AimPosition, saved.AimRotation);
            }
            if (saved.CameraSwapped && MainCamera.instance != null)
            {
                MainCamera.instance.transform.SetPositionAndRotation(saved.CameraPosition, saved.CameraRotation);
            }
        }
    }

    // Barricades: UseableBarricade.check() (private) is the one entry of every placement test (preview and click).
    [HarmonyPatch(typeof(UseableBarricade), "check")]
    internal static class BuildingHandAimBarricadePatch
    {
        private static void Prefix(UseableBarricade __instance, out BuildingHandAim.Saved __state)
        {
            __state = BuildingHandAim.Begin(__instance.player, __instance.channel, alsoMainCamera: true);
        }

        // Finalizer: the check can throw, and the aim must be restored anyway.
        private static void Finalizer(UseableBarricade __instance, BuildingHandAim.Saved __state)
        {
            BuildingHandAim.End(__instance.player, __state);
        }
    }

    // Structures (walls, floors, roofs...): UseableStructure.UpdatePendingPlacement() (private).
    [HarmonyPatch(typeof(UseableStructure), "UpdatePendingPlacement")]
    internal static class BuildingHandAimStructurePatch
    {
        private static void Prefix(UseableStructure __instance, out BuildingHandAim.Saved __state)
        {
            __state = BuildingHandAim.Begin(__instance.player, __instance.channel, alsoMainCamera: false);
        }

        private static void Finalizer(UseableStructure __instance, BuildingHandAim.Saved __state)
        {
            BuildingHandAim.End(__instance.player, __state);
        }
    }

    // Fertilizer (UseableGrower): fire() (private) raycasts 3 m from player.look.aim at the planted crop, on the click
    // only (no preview), and on the server checks the hit point against the aim position (7 m).
    [HarmonyPatch(typeof(UseableGrower), "fire")]
    internal static class BuildingHandAimGrowerPatch
    {
        private static void Prefix(UseableGrower __instance, out BuildingHandAim.Saved __state)
        {
            __state = BuildingHandAim.Begin(__instance.player, __instance.channel, alsoMainCamera: false);
        }

        private static void Finalizer(UseableGrower __instance, BuildingHandAim.Saved __state)
        {
            BuildingHandAim.End(__instance.player, __state);
        }
    }

    // The housing planner: UseableHousingPlanner.UpdatePendingPlacement() (private).
    [HarmonyPatch(typeof(UseableHousingPlanner), "UpdatePendingPlacement")]
    internal static class BuildingHandAimHousingPatch
    {
        private static void Prefix(UseableHousingPlanner __instance, out BuildingHandAim.Saved __state)
        {
            __state = BuildingHandAim.Begin(__instance.player, __instance.channel, alsoMainCamera: false);
        }

        private static void Finalizer(UseableHousingPlanner __instance, BuildingHandAim.Saved __state)
        {
            BuildingHandAim.End(__instance.player, __state);
        }
    }
}
