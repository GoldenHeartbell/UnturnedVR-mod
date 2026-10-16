using System.Reflection;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The beam of a switched-on hand-held flashlight comes from the flashlight instead of from the eyes.
    /// The game lights the world with one spot light on the player (Player.firstSpot, a child of the game camera, so in VR
    /// at the head); the item only shows a small viewmodel light. While a flashlight (a melee item with a light) is held and
    /// switched on, that spot light is moved to the item's light (the "Light" child of the light hook the game switches on and
    /// off) and turned the way it points. The game keeps deciding everything else: when the light is on (its hook is active),
    /// range, angle, colour, the perspective. When the light goes off, the item is put away or the view is not first person,
    /// the spot light gets its own pose back.
    /// The head-mounted light (glasses with a lamp) shares that one spot light and belongs on the head: while it is on, the
    /// spot light is left alone, so it never moves to the hand. Gun tactical lights are not changed.
    /// Called from VRArms.Apply right after the item is placed (LateUpdate and before-render).
    /// Cost: a few cached reflection reads and one transform write per call; nothing without a flashlight.
    /// </summary>
    internal static class VRFlashlight
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly FieldInfo spotField = typeof(Player).GetField("firstSpot", AnyInstance);
        private static readonly FieldInfo headlampField = typeof(Player).GetField("headlampOn", AnyInstance);
        private static readonly FieldInfo meleeHookField = typeof(UseableMelee).GetField("firstLightHook", AnyInstance);

        private static Transform spot;
        private static Vector3 restPosition;
        private static Quaternion restRotation;
        private static bool moved;
        private static bool loggedMissing;
        private static bool loggedTakeover;

        public static void Update()
        {
            Player player = Player.LocalPlayer;
            Transform beam = VRState.IsActive && player != null ? FindBeam(player) : null;
            Transform spotTransform = beam != null ? FindSpot(player) : null;
            if (beam == null || spotTransform == null)
            {
                Restore();
                return;
            }

            if (spotTransform != spot)
            {
                spot = spotTransform;
                moved = false;
            }
            if (!moved)
            {
                restPosition = spot.localPosition;
                restRotation = spot.localRotation;
                moved = true;
            }

            spot.SetPositionAndRotation(beam.position, beam.rotation);

            if (!loggedTakeover)
            {
                loggedTakeover = true;
                VRLog.Info("flashlight: the player spot light now sits at the light of the held flashlight and points where it points.");
            }
        }

        private static Transform FindSpot(Player player)
        {
            if (spotField == null || headlampField == null)
            {
                if (!loggedMissing)
                {
                    loggedMissing = true;
                    VRLog.Warn("flashlight: Player.firstSpot / headlampOn not found, the light stays at the head.");
                }
                return null;
            }
            return spotField.GetValue(player) as Transform;
        }

        // The light of the held flashlight, when it is switched on and the view is first person: the "Light" child of the hook
        // (that child is the actual light, so its forward is the way the beam goes), or the hook itself without such a child.
        private static Transform FindBeam(Player player)
        {
            if (player.look == null || player.look.perspective != EPlayerPerspective.FIRST || player.look.IsLocallyUsingFreecam)
            {
                return null;
            }

            PlayerEquipment equipment = player.equipment;
            if (equipment == null || !(equipment.useable is UseableMelee melee) || !(equipment.asset is ItemMeleeAsset asset) || !asset.isLight)
            {
                return null;
            }

            // The head-mounted light owns the spot light (the game blends both into it at the head).
            if (headlampField != null && headlampField.GetValue(player) is bool headlamp && headlamp)
            {
                return null;
            }

            Transform hook = meleeHookField != null ? meleeHookField.GetValue(melee) as Transform : null;
            if (hook == null || !hook.gameObject.activeInHierarchy)
            {
                return null; // switched off: the game hides the hook
            }

            Transform light = hook.Find("Light");
            return light != null ? light : hook;
        }

        private static void Restore()
        {
            if (moved && spot != null)
            {
                spot.SetLocalPositionAndRotation(restPosition, restRotation);
            }
            moved = false;
        }
    }
}
