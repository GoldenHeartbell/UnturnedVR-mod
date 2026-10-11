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
        // The laser uses the same controller tilt as the hands (VRHands.GripOffsetEuler: the raw controller
        // forward points above where the hand points).
        private const float ScrollPerSecond = 15f;
        private const float OffPanel = -10000f;

        /// <summary>True while the pointer owns the mouse (a UI wants a cursor and the right controller is tracked).</summary>
        public static bool Active { get; private set; }
        public static Vector3 MousePosition { get; private set; } = new Vector3(OffPanel, OffPanel, 0f);
        public static bool ButtonHeld { get; private set; }
        public static bool ButtonDown { get; private set; }
        public static bool ButtonUp { get; private set; }
        /// <summary>Right mouse button = right controller A.</summary>
        public static bool RightButtonHeld { get; private set; }
        public static bool RightButtonDown { get; private set; }
        public static bool RightButtonUp { get; private set; }
        public static Vector2 ScrollDelta { get; private set; }

        /// <summary>
        /// A physical button turned into a mouse click: down on the press frame, up on the next, both at the
        /// position where the press started. Unity only clicks when press and release hit the same element,
        /// and pulling a trigger or pressing A moves the ray.
        /// </summary>
        private struct Tap
        {
            private bool wasHeld;
            private int phase; // 0 idle, 1 = press this frame, 2 = release this frame
            private Vector3 position;

            public bool Down => phase == 1;
            public bool Up => phase == 2;

            /// <summary>Returns the mouse position to report: the latched one while a click is being delivered.</summary>
            public Vector3 Update(bool held, Vector3 live)
            {
                if (held && !wasHeld)
                {
                    phase = 1;
                    position = live;
                }
                wasHeld = held;
                Vector3 result = phase != 0 ? position : live;
                return result;
            }

            /// <summary>Call after the frame's values were read.</summary>
            public void Advance()
            {
                phase = phase == 1 ? 2 : 0;
            }

            public void Reset(bool held)
            {
                wasHeld = held; // a button already held when a UI opens must not count as a click
                phase = 0;
            }
        }

        private LineRenderer laser;
        private bool loggedActive;
        private bool loggedHit;
        private float nextPointerLog;
        private Tap leftTap;
        private Tap rightTap;
        private bool swallowLeftUp;
        private bool swallowRightUp;

        public static void Create()
        {
            GameObject go = new GameObject("UnturnedVR Pointer");
            DontDestroyOnLoad(go);
            go.AddComponent<VRPointer>();
        }

        private void Update()
        {
            long t = VRPerf.Begin();
            UpdateInner();
            VRPerf.End(VRPerf.Pointer, t);
        }

        private void UpdateInner()
        {
            if (!VRState.IsActive)
            {
                Deactivate(false, false);
                return;
            }

            VRControllerInput.Refresh();
            // Gun menu open: the gun's hand holds the gun, so the other hand points. Otherwise always the right one.
            bool useLeft = VRAttachMenu.Open && VRArms.ItemHand == VRHands.Right;
            bool held = useLeft ? (VRControllerInput.LeftButtons & VRButton.Trigger) != 0 : VRControllerInput.RightTrigger;
            bool rightHeld = ((useLeft ? VRControllerInput.LeftButtons : VRControllerInput.RightButtons) & VRButton.A) != 0;
            bool poseValid = useLeft ? VRControllerInput.LeftPoseValid : VRControllerInput.RightPoseValid;
            Vector3 handPosition = useLeft ? VRControllerInput.LeftPosition : VRControllerInput.RightPosition;
            Quaternion handRotation = useLeft ? VRControllerInput.LeftRotation : VRControllerInput.RightRotation;
            Vector2 stick = useLeft ? VRControllerInput.LeftStick : VRControllerInput.RightStick;

            Transform rig = VRRig.RigTransform;
            Transform panel = VRGui.PointerPanel;
            if (!UiWantsCursor() || !poseValid || rig == null
                || panel == null || !panel.gameObject.activeInHierarchy)
            {
                Deactivate(held, rightHeld);
                return;
            }

            Vector3 origin = rig.TransformPoint(handPosition);
            Quaternion aim = rig.rotation * handRotation * Quaternion.Euler(VRHands.GripOffsetEuler);
            Vector3 direction = aim * Vector3.forward;

            bool hit = TryHitPanel(panel, VRGui.PointerUv, origin, direction, out Vector3 hitPoint, out Vector2 pixel);

            Active = true;
            Vector3 livePosition = hit ? new Vector3(pixel.x, pixel.y, 0f) : new Vector3(OffPanel, OffPanel, 0f);
            // Trigger = left click, A = right click (item context actions: equip, drop, salvage, ...).
            // The left tap owns the reported position, the right tap only overrides it during its own click.
            MousePosition = leftTap.Update(held, livePosition);
            Vector3 rightPosition = rightTap.Update(rightHeld, livePosition);
            if (rightTap.Down || rightTap.Up)
            {
                MousePosition = rightPosition;
            }
            ButtonHeld = ButtonDown = leftTap.Down;
            ButtonUp = leftTap.Up;
            RightButtonHeld = RightButtonDown = rightTap.Down;
            RightButtonUp = rightTap.Up;
            SwallowRefocusClick();
            leftTap.Advance();
            rightTap.Advance();
            ScrollDelta = new Vector2(0f, stick.y * ScrollPerSecond * Time.unscaledDeltaTime);

            UpdateLaser(origin, hit ? hitPoint : origin + direction * LaserLength, hit);

            if (VRDebugFlags.LogPointer && Time.unscaledTime >= nextPointerLog)
            {
                nextPointerLog = Time.unscaledTime + 1f;
                VRLog.Info("pointer: hand=" + (useLeft ? "left" : "right") + " hit=" + hit + " mouse=" + MousePosition + " trigger=" + held + " A=" + rightHeld
                    + " ctrlPos=" + handPosition.ToString("0.00") + " panel=" + panel.position.ToString("0.00"));
            }

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
        }

        // While the game window has no Windows focus (the player used the desktop) the UI ignores every event, so the first
        // click on the menu takes the focus back instead and is not delivered: it cannot hit a button, and the release
        // that follows is dropped too. If Windows refuses the focus the click goes through as before.
        private void SwallowRefocusClick()
        {
            if (swallowLeftUp && leftTap.Up)
            {
                ButtonUp = false;
                swallowLeftUp = false;
            }
            if (swallowRightUp && rightTap.Up)
            {
                RightButtonUp = false;
                swallowRightUp = false;
            }

            if ((leftTap.Down || rightTap.Down) && VRFocus.Unfocused && VRFocus.RequestRefocus())
            {
                swallowLeftUp = leftTap.Down;
                swallowRightUp = rightTap.Down;
                ButtonHeld = ButtonDown = false;
                RightButtonHeld = RightButtonDown = false;
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
        // uv is the part of the UI texture the panel shows (the whole texture for the main panel).
        private static bool TryHitPanel(Transform panel, Rect uv, Vector3 origin, Vector3 direction, out Vector3 point, out Vector2 pixel)
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

            pixel = new Vector2((uv.xMin + (local.x + 0.5f) * uv.width) * Screen.width,
                (uv.yMin + (local.y + 0.5f) * uv.height) * Screen.height);
            return true;
        }

        private void Deactivate(bool triggerHeld, bool rightHeld)
        {
            leftTap.Reset(triggerHeld);
            rightTap.Reset(rightHeld);
            swallowLeftUp = false;
            swallowRightUp = false;
            HideLaser(); // every inactive frame, not just on the transition, so it can never stay behind in the world
            if (!Active)
            {
                return;
            }

            Active = false;
            ButtonHeld = false;
            ButtonDown = false;
            ButtonUp = false;
            RightButtonHeld = false;
            RightButtonDown = false;
            RightButtonUp = false;
            ScrollDelta = Vector2.zero;
            MousePosition = new Vector3(OffPanel, OffPanel, 0f);
        }

        private void HideLaser()
        {
            if (laser != null && laser.enabled)
            {
                laser.enabled = false;
            }
        }

        private void OnDisable()
        {
            HideLaser();
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
