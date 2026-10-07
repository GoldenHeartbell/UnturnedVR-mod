using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// M5: the right controller as a laser pointer for menus and cursor UIs (inventory, pause, ...).
    /// The controller ray is intersected with the VRGui panel quad and the hit is turned into a pixel
    /// of the UI texture (same size as the window, see VRGui). Those pixels and the trigger are exposed
    /// here and substituted for the flat-window mouse by Patches/VRInputPatches, so Unity's
    /// StandaloneInputModule, Glazier's cursor image and Unturned's own mouse reads all follow the laser.
    /// Only active while a UI wants a cursor; in gameplay nothing here runs.
    /// </summary>
    [DefaultExecutionOrder(-32000)] // before EventSystem.Update reads the (patched) mouse
    internal class VRPointer : MonoBehaviour
    {
        // Guesses to tune in-headset.
        private const float LaserWidth = 0.004f;
        private const float LaserLength = 4f;
        // Tilt of the laser relative to the controller's forward, degrees (positive = down). Touch/Index
        // grips may need some; 0 = straight along the pose's forward.
        private const float PointerPitchDegrees = 0f;
        private const float ScrollPerSecond = 15f;
        private const float OffPanel = -10000f;

        /// <summary>True while the pointer owns the mouse (a UI wants a cursor and the right controller is tracked).</summary>
        public static bool Active { get; private set; }
        public static Vector3 MousePosition { get; private set; } = new Vector3(OffPanel, OffPanel, 0f);
        public static bool ButtonHeld { get; private set; }
        public static bool ButtonDown { get; private set; }
        public static bool ButtonUp { get; private set; }
        public static Vector2 ScrollDelta { get; private set; }

        private LineRenderer laser;
        private bool prevHeld;
        private bool loggedActive;
        private bool loggedHit;
        private bool loggedPress;

        public static void Create()
        {
            GameObject go = new GameObject("UnturnedVR Pointer");
            DontDestroyOnLoad(go);
            go.AddComponent<VRPointer>();
        }

        private void Update()
        {
            if (!VRState.IsActive)
            {
                Deactivate(false);
                return;
            }

            VRControllerInput.Refresh();
            bool held = VRControllerInput.RightTrigger;
            bool wasHeld = prevHeld;
            prevHeld = held;

            Transform rig = VRRig.RigTransform;
            Transform panel = VRGui.PanelTransform;
            if (!UiWantsCursor() || !VRControllerInput.RightPoseValid || rig == null
                || panel == null || !panel.gameObject.activeInHierarchy)
            {
                Deactivate(held);
                return;
            }

            Vector3 origin = rig.TransformPoint(VRControllerInput.RightPosition);
            Quaternion aim = rig.rotation * VRControllerInput.RightRotation * Quaternion.Euler(PointerPitchDegrees, 0f, 0f);
            Vector3 direction = aim * Vector3.forward;

            bool hit = TryHitPanel(panel, origin, direction, out Vector3 hitPoint, out Vector2 pixel);

            Active = true;
            MousePosition = hit ? new Vector3(pixel.x, pixel.y, 0f) : new Vector3(OffPanel, OffPanel, 0f);
            ButtonHeld = held;
            ButtonDown = held && !wasHeld;
            ButtonUp = !held && wasHeld;
            ScrollDelta = new Vector2(0f, VRControllerInput.RightStick.y * ScrollPerSecond * Time.unscaledDeltaTime);

            UpdateLaser(origin, hit ? hitPoint : origin + direction * LaserLength, hit);

            if (!loggedActive)
            {
                loggedActive = true;
                VRLog.Info("pointer active (menu/cursor UI open, right controller tracked).");
            }
            if (hit && !loggedHit)
            {
                loggedHit = true;
                VRLog.Info("first pointer hit at pixel " + pixel + " of " + Screen.width + "x" + Screen.height + ".");
            }
            if (ButtonDown && !loggedPress)
            {
                loggedPress = true;
                VRLog.Info("first trigger press (hit=" + hit + ", pixel=" + MousePosition + ").");
            }
        }

        private static bool UiWantsCursor()
        {
            bool cursorUiOpen = PlayerUI.window != null && PlayerUI.window.showCursor;
            return VRRig.InMenu || VRRig.ActiveCamera == null || cursorUiOpen;
        }

        // The panel is a unit quad (local -0.5..0.5) scaled to size and visible from -Z, so rays hit
        // it travelling along its +Z; local x/y + 0.5 are the texture u/v (origin bottom-left, like
        // the window's mouse coordinates).
        private static bool TryHitPanel(Transform panel, Vector3 origin, Vector3 direction, out Vector3 point, out Vector2 pixel)
        {
            point = origin;
            pixel = Vector2.zero;

            Vector3 normal = panel.forward;
            float denominator = Vector3.Dot(direction, normal);
            if (denominator < 0.0001f)
            {
                return false;
            }

            float distance = Vector3.Dot(panel.position - origin, normal) / denominator;
            if (distance <= 0f)
            {
                return false;
            }

            point = origin + direction * distance;
            Vector3 local = panel.InverseTransformPoint(point);
            if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f)
            {
                return false;
            }

            pixel = new Vector2((local.x + 0.5f) * Screen.width, (local.y + 0.5f) * Screen.height);
            return true;
        }

        private void Deactivate(bool triggerHeld)
        {
            prevHeld = triggerHeld; // a trigger already held when a UI opens must not count as a click
            if (!Active)
            {
                return;
            }

            Active = false;
            ButtonHeld = false;
            ButtonDown = false;
            ButtonUp = false;
            ScrollDelta = Vector2.zero;
            MousePosition = new Vector3(OffPanel, OffPanel, 0f);
            if (laser != null)
            {
                laser.enabled = false;
            }
        }

        private void UpdateLaser(Vector3 from, Vector3 to, bool hit)
        {
            if (laser == null && !TryCreateLaser())
            {
                return;
            }

            laser.enabled = true;
            laser.SetPosition(0, from);
            laser.SetPosition(1, to);
            Color color = hit ? new Color(0.3f, 0.9f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.35f);
            laser.startColor = color;
            laser.endColor = color;
        }

        // On the panel layer so only the GUI overlay camera draws it (the eye cameras strip that layer).
        private bool TryCreateLaser()
        {
            if (VRGui.PanelLayer < 0)
            {
                return false;
            }

            GameObject go = new GameObject("UnturnedVR Laser");
            go.layer = VRGui.PanelLayer;
            go.transform.SetParent(transform, worldPositionStays: false);
            laser = go.AddComponent<LineRenderer>();
            laser.useWorldSpace = true;
            laser.positionCount = 2;
            laser.startWidth = LaserWidth;
            laser.endWidth = LaserWidth;
            laser.material = new Material(Canvas.GetDefaultCanvasMaterial());
            laser.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            laser.receiveShadows = false;
            return true;
        }
    }
}
