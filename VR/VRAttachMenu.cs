using System.Reflection;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The gun's attachment menu ("attach mode": the hook buttons and the rings of matching inventory items) in VR.
    /// Opened by a long press of B on the controller that holds the gun, closed by a tap of that B (or the other B).
    /// A short tap of B stays what it was (quick reload / pause): it is delivered on release, as a one-frame press.
    /// The game's attach mode lasts as long as the attach key is held, so the long press latches a virtual
    /// ControlsSettings.attach key (VRKeyMap serves it through GetKey/GetKeyDown/GetKeyUp) until the menu is closed.
    /// Once per frame from VRKeyMap.Refresh; no cost while no gun is held.
    /// </summary>
    internal static class VRAttachMenu
    {
        private const float LongPressSeconds = 0.4f;
        // The game refuses to enter attach mode while shooting/aiming/reloading: give up on the virtual key after this.
        private const int OpenTimeoutFrames = 5;

        private static readonly FieldInfo isAttachingField = AccessTools_Field("isAttaching");
        private static readonly FieldInfo sightField = AccessTools_Field("sightButton");
        private static readonly FieldInfo tacticalField = AccessTools_Field("tacticalButton");
        private static readonly FieldInfo gripField = AccessTools_Field("gripButton");
        private static readonly FieldInfo barrelField = AccessTools_Field("barrelButton");
        private static readonly FieldInfo magazineField = AccessTools_Field("magazineButton");

        private static FieldInfo AccessTools_Field(string name)
        {
            return typeof(UseableGun).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        }

        /// <summary>The virtual attach key is held (the menu was asked for).</summary>
        public static bool Wanted { get; private set; }
        public static bool KeyDown { get; private set; }
        public static bool KeyUp { get; private set; }

        /// <summary>The game is in attach mode right now (the local player's equipped gun reports isAttaching).</summary>
        public static bool Open { get; private set; }

        // B of each hand (index VRHands.Left / VRHands.Right).
        private static readonly bool[] previousB = new bool[2];
        private static readonly bool[] consumedB = new bool[2];
        private static readonly bool[] bypass = new bool[2];
        private static bool tapPulse;
        private static bool holdActive; // opened in hold mode: closes when that B is released
        private static bool pending;
        private static float pressTime;
        private static int openFrame;
        private static bool loggedMissing;
        private static bool seenOpen;

        /// <summary>True while this hand's B is under the attach menu's control: its raw value is replaced by Value.</summary>
        public static bool ControlsB(int hand)
        {
            return consumedB[hand] || (!bypass[hand] && hand == controlledHand);
        }

        /// <summary>The value to use for a controlled B: a one-frame press on a short tap, else nothing.</summary>
        public static bool ControlledBValue(int hand)
        {
            return !consumedB[hand] && hand == controlledHand && tapPulse;
        }

        private static int controlledHand = -1;

        public static UseableGun EquippedGun()
        {
            Player player = Player.LocalPlayer;
            return player != null && player.equipment != null ? player.equipment.useable as UseableGun : null;
        }

        /// <summary>Once per frame with the raw B state of both controllers and whether a cursor UI is open.</summary>
        public static void Update(bool leftB, bool rightB, bool uiOpen)
        {
            KeyDown = false;
            KeyUp = false;
            tapPulse = false;
            controlledHand = -1;

            bool[] raw = rawBuffer; // reused: this runs every frame
            raw[0] = leftB;
            raw[1] = rightB;
            for (int h = 0; h < 2; h++)
            {
                if (!raw[h])
                {
                    consumedB[h] = false;
                }
            }

            UseableGun gun = EquippedGun();
            int hand = VRArms.ItemHand;
            bool gunHeld = gun != null && hand >= 0 && hand <= 1;

            Open = gun != null && ReadAttaching(gun);
            if (Open)
            {
                seenOpen = true;
            }
            else
            {
                slotsReady = false; // the slots are chosen again at the next open
            }

            if (!gunHeld)
            {
                Release();
                pending = false;
                bypass[0] = bypass[1] = false;
                previousB[0] = leftB;
                previousB[1] = rightB;
                return;
            }

            // The game left attach mode by itself (dequipped, died, ...): stop holding the key.
            if (Wanted && seenOpen && !Open)
            {
                Release();
            }
            // The game never entered it (busy shooting / aiming / reloading): give up.
            else if (Wanted && !seenOpen && Time.frameCount - openFrame > OpenTimeoutFrames)
            {
                Release();
            }

            controlledHand = hand;
            // With a cursor UI open (inventory, pause) B is not ours: pass it through as it always was.
            bypass[hand] = uiOpen && !Wanted;
            bypass[1 - hand] = false;

            int other = 1 - hand;
            bool otherDown = raw[other] && !previousB[other];
            if (Wanted && otherDown)
            {
                // The other B (pause on the left) closes the menu instead of also opening the pause menu.
                Release();
                consumedB[other] = true;
            }

            bool held = raw[hand] && !consumedB[hand];
            bool down = held && !previousB[hand];
            bool up = !held && previousB[hand];

            if (bypass[hand])
            {
                pending = false;
            }
            else
            {
                if (down)
                {
                    pending = true;
                    pressTime = Time.unscaledTime;
                }

                if (pending && held && Time.unscaledTime - pressTime >= LongPressSeconds)
                {
                    pending = false;
                    if (Wanted)
                    {
                        Release();
                    }
                    else if (!uiOpen && !gun.player.equipment.isBusy)
                    {
                        Request();
                        // Hold mode (VR settings page): this press keeps the menu open only while it is held.
                        holdActive = Wanted && VRSettings.AttachMenuHold;
                    }
                }

                if (up && pending)
                {
                    pending = false;
                    if (Wanted)
                    {
                        Release();
                    }
                    else
                    {
                        tapPulse = true;
                    }
                }
                else if (up && holdActive)
                {
                    // The long press that opened it ends: close, and do not deliver B as a tap (no reload / pause).
                    Release();
                }
            }

            previousB[0] = leftB;
            previousB[1] = rightB;
        }

        private static void Request()
        {
            if (isAttachingField == null)
            {
                if (!loggedMissing)
                {
                    loggedMissing = true;
                    VRLog.Warn("attach menu: UseableGun.isAttaching not found, long-press B does nothing.");
                }
                return;
            }

            Wanted = true;
            KeyDown = true;
            seenOpen = false;
            openFrame = Time.frameCount;
            VRLog.Info("attach menu: opened (long press B, " + (VRSettings.AttachMenuHold ? "hold mode: closes when B is released" : "toggle mode: closes with the next B press") + ").");
        }

        private static void Release()
        {
            holdActive = false;
            if (!Wanted)
            {
                return;
            }
            Wanted = false;
            KeyUp = true;
            seenOpen = false;
            VRLog.Info("attach menu: closed.");
        }

        // ---- Panel on the gun -------------------------------------------------------------------------------------
        // Tuned in the headset.
        /// <summary>World width (m) of the panel showing CanvasRect.</summary>
        public const float PanelWidth = 0.7f;
        /// <summary>How far above the gun's hooks (m, world up) the panel floats, so it does not cover the gun.</summary>
        private const float PanelRise = 0.32f;

        /// <summary>Canvas rectangle (0..1 of the texture, origin top left) that holds the whole menu with its rings.</summary>
        public static readonly Rect CanvasRect = Rect.MinMaxRect(0.2f, 0.14f, 0.8f, 0.86f);

        private static readonly ISleekElement[] buttonBuffer = new ISleekElement[5];
        private static readonly Transform[] hookBuffer = new Transform[5];
        private static readonly Vector2[] slotPosition = new Vector2[5];
        private static readonly float[] sortX = new float[5];
        private static readonly float[] sortY = new float[5];
        private static readonly int[] order = new int[5];
        private static Transform hookModel;
        private static Attachments hookAttachments;

        private static void ResolveHooks(UseableGun gun)
        {
            Transform model = gun != null && gun.player != null && gun.player.equipment != null ? gun.player.equipment.firstModel : null;
            if (model != hookModel)
            {
                hookModel = model;
                hookAttachments = model != null ? model.GetComponent<Attachments>() : null;
            }

            Attachments a = hookAttachments;
            hookBuffer[0] = a != null ? a.sightHook : null;
            hookBuffer[1] = a != null ? a.tacticalHook : null;
            hookBuffer[2] = a != null ? a.gripHook : null;
            hookBuffer[3] = a != null ? a.barrelHook : null;
            hookBuffer[4] = a != null ? a.magazineHook : null;
        }

        // The middle of this gun's hooks (so a pistol, a rifle and a launcher each get the panel over their own
        // attachment points), else the model origin.
        private static bool TryGetGunCenter(UseableGun gun, out Vector3 center)
        {
            ResolveHooks(gun);
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int i = 0; i < hookBuffer.Length; i++)
            {
                if (hookBuffer[i] != null)
                {
                    sum += hookBuffer[i].position;
                    count++;
                }
            }

            if (count > 0)
            {
                center = sum / count;
                return true;
            }
            if (hookModel != null)
            {
                center = hookModel.position;
                return true;
            }
            center = default;
            return false;
        }

        /// <summary>
        /// Pose of the menu panel: floating above the equipped gun's hooks, turned to face the eye (visible from -Z,
        /// like every VRGui panel, and upright). False when no gun model exists.
        /// </summary>
        public static bool TryGetPanelPose(Vector3 eyePosition, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (!TryGetGunCenter(EquippedGun(), out Vector3 center))
            {
                return false;
            }

            position = center + Vector3.up * PanelRise;
            Vector3 toPanel = position - eyePosition;
            if (toPanel.sqrMagnitude < 0.0001f)
            {
                return false;
            }
            rotation = Quaternion.LookRotation(toPanel, Vector3.up);
            return true;
        }

        /// <summary>The last panel pose computed with the gun in its final place (see <see cref="CachePanelPose"/>).</summary>
        public static bool CachedValid { get; private set; }
        public static Vector3 CachedPosition { get; private set; }
        public static Quaternion CachedRotation { get; private set; }

        /// <summary>
        /// Called by VRArms right after it placed the item on the controller (LateUpdate and before render). The hooks
        /// must be read then: earlier in the frame the model is still in the game's animated, head-relative hold pose,
        /// which made the panel follow the head instead of the gun.
        /// </summary>
        public static void CachePanelPose(Vector3 eyePosition)
        {
            if (!Open)
            {
                CachedValid = false;
                return;
            }

            CachedValid = TryGetPanelPose(eyePosition, out Vector3 position, out Quaternion rotation);
            CachedPosition = position;
            CachedRotation = rotation;

            // The slots are chosen once per open, here, from the hooks in their final place.
            if (CachedValid && !slotsReady)
            {
                UseableGun gun = EquippedGun();
                if (IsAttaching(gun))
                {
                    GetButtons(gun, buttonBuffer);
                    AssignSlots(gun);
                    slotsReady = true;
                }
            }
        }

        private static bool slotsReady;

        /// <summary>
        /// Puts each hook button in a slot of the compact layout (two rows, the top row holding the higher hooks) in the
        /// order its hook has on this gun as seen from the eye: sight above, magazine below, barrel in front of the grip.
        /// The slots are chosen once when the menu opens so the buttons never swap places while the gun moves.
        /// Call every frame while the game is in attach mode (after UseableGun.tick has placed them its own way).
        /// </summary>
        public static void ApplyLayout(UseableGun gun)
        {
            if (!slotsReady)
            {
                return; // chosen in CachePanelPose once the gun is in place; the game's own spots for a frame or two
            }

            GetButtons(gun, buttonBuffer);
            for (int i = 0; i < buttonBuffer.Length; i++)
            {
                ISleekElement button = buttonBuffer[i];
                if (button == null)
                {
                    continue;
                }
                button.PositionScale_X = slotPosition[i].x;
                button.PositionScale_Y = slotPosition[i].y;
            }
        }

        private static void AssignSlots(UseableGun gun)
        {
            ResolveHooks(gun);
            Camera eye = VRRig.ActiveCamera;
            Vector3 right = Vector3.right;
            Vector3 up = Vector3.up;
            Vector3 center = Vector3.zero;
            if (eye != null && TryGetPanelPose(eye.transform.position, out _, out Quaternion rotation) && TryGetGunCenter(gun, out center))
            {
                right = rotation * Vector3.right;
                up = rotation * Vector3.up;
            }

            int count = 0;
            for (int i = 0; i < buttonBuffer.Length; i++)
            {
                if (buttonBuffer[i] == null)
                {
                    continue;
                }
                Vector3 offset = hookBuffer[i] != null ? hookBuffer[i].position - center : Vector3.zero;
                sortX[i] = Vector3.Dot(offset, right);
                sortY[i] = hookBuffer[i] != null ? Vector3.Dot(offset, up) : float.MinValue;
                order[count++] = i;
            }

            // Highest hook first; the top row takes the first half (rounded up), each row is then ordered left to right.
            SortBy(order, count, sortY, descending: true);
            int top = count <= 1 ? count : (count + 1) / 2;
            SortBy(order, top, sortX, descending: false, start: 0);
            SortRange(order, top, count, sortX);

            PlaceRow(0, top, count == 1 ? 0.5f : 0.33f);
            PlaceRow(top, count, 0.67f);
        }

        private static void SortBy(int[] items, int count, float[] key, bool descending, int start = 0)
        {
            for (int i = start + 1; i < start + count; i++)
            {
                int item = items[i];
                int j = i - 1;
                while (j >= start && (descending ? key[items[j]] < key[item] : key[items[j]] > key[item]))
                {
                    items[j + 1] = items[j];
                    j--;
                }
                items[j + 1] = item;
            }
        }

        private static void SortRange(int[] items, int from, int to, float[] key)
        {
            SortBy(items, to - from, key, descending: false, start: from);
        }

        private static void PlaceRow(int from, int to, float y)
        {
            int n = to - from;
            for (int k = 0; k < n; k++)
            {
                float x = n == 1 ? 0.5f : n == 2 ? 0.4f + 0.2f * k : 0.3f + 0.2f * k;
                slotPosition[order[from + k]] = new Vector2(x, y);
            }
        }

        /// <summary>The five hook buttons of a gun (null where the gun has no such hook), in game order.</summary>
        public static void GetButtons(UseableGun gun, ISleekElement[] buttons)
        {
            buttons[0] = sightField != null ? sightField.GetValue(gun) as ISleekElement : null;
            buttons[1] = tacticalField != null ? tacticalField.GetValue(gun) as ISleekElement : null;
            buttons[2] = gripField != null ? gripField.GetValue(gun) as ISleekElement : null;
            buttons[3] = barrelField != null ? barrelField.GetValue(gun) as ISleekElement : null;
            buttons[4] = magazineField != null ? magazineField.GetValue(gun) as ISleekElement : null;
        }

        // Typed field reference: reading isAttaching through FieldInfo.GetValue boxes a bool every call, and this is read
        // every frame. Declared after isAttachingField (static initializers run in order); null when the field is missing
        // or not a bool, then the reflection read below is used.
        private static readonly bool[] rawBuffer = new bool[2];
        private static readonly HarmonyLib.AccessTools.FieldRef<UseableGun, bool> isAttachingRef = CreateIsAttachingRef();

        private static HarmonyLib.AccessTools.FieldRef<UseableGun, bool> CreateIsAttachingRef()
        {
            try
            {
                return isAttachingField != null ? HarmonyLib.AccessTools.FieldRefAccess<UseableGun, bool>(isAttachingField) : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static bool ReadAttaching(UseableGun gun)
        {
            if (isAttachingRef != null)
            {
                return isAttachingRef(gun);
            }
            return isAttachingField != null && (bool)isAttachingField.GetValue(gun);
        }

        public static bool IsAttaching(UseableGun gun)
        {
            return gun != null && ReadAttaching(gun);
        }
    }
}
