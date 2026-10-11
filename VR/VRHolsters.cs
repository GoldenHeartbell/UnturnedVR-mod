using System;
using System.Globalization;
using System.IO;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Body holsters: the game's hotbar as zones on your body. Primary over either shoulder (two zones, one slot, so
    /// either hand can cross over to the far shoulder), secondary at the right hip, hotbar slots 3 and 4 on the left and
    /// right of the chest. Squeezing a grip with a hand inside a zone:
    ///   - the item of that zone is held right now (or an item that belongs to no zone is held): it is holstered,
    ///   - the zone holds an item: it is drawn into the hand that squeezed (replaces what is held),
    ///   - the zone is empty: nothing (short rumble).
    /// It works by pressing the game's own keys for a frame (hotbar key of the slot, or the dequip key), served through
    /// VRKeyMap like the attach key, so equip rules, animations and the network all stay the game's. The grip press is
    /// consumed so it does not also interact / change fire mode / grab the item (VRHold).
    /// Zones are in a body frame: the neck pivot under the eye (the same as VRArms) turned to the head's heading, so
    /// they follow where you face and ignore head pitch. Positions and radius can be overridden in holsters.txt.
    /// Cost: four distance checks per hand per frame (plus a file timestamp check every 2 s).
    /// </summary>
    internal static class VRHolsters
    {
        private const int ZoneCount = 5;
        private const string FileName = "holsters.txt";

        // Hotbar button of each zone: 0 primary, 1 secondary, 2 and 3 = keys 3 and 4 (hotkeys[0], hotkeys[1]).
        // The left shoulder is a second primary zone, so either hand can reach either shoulder (cross draw).
        private static readonly int[] zoneSlot = { 0, 1, 2, 3, 0 };
        private const int PrimaryLeftZone = 4;
        // Offsets in the body frame (x right, y up, z forward, metres, from the neck pivot). GUESSES, tune in headset.
        private static readonly Vector3[] defaultOffsets =
        {
            new Vector3(0.20f, 0.16f, -0.15f),  // primary: over the right shoulder (raised 6 cm)
            new Vector3(0.30f, -0.30f, 0.00f),  // secondary: right hip (moved up 15 cm, then 5 cm to the right)
            new Vector3(-0.12f, -0.07f, 0.12f), // slot 3: left chest (moved up 15 cm)
            new Vector3(0.12f, -0.07f, 0.12f),  // slot 4: right chest
            new Vector3(-0.20f, 0.16f, -0.15f), // primary again: over the left shoulder (mirror of the right one)
        };
        private static readonly string[] names = { "primary", "secondary", "slot3", "slot4", "primaryleft" };
        private static bool fileHasPrimaryLeft; // holsters.txt sets the left shoulder itself; otherwise it mirrors "primary"
        private const float DefaultRadius = 0.14f;

        private static readonly Vector3[] offsets = (Vector3[]) defaultOffsets.Clone();
        private static float radius = DefaultRadius;

        // Ear zones beside the head for the night vision gesture (VRGestures): left and right, same body frame.
        // Ellipsoids: EarRadius sideways and forward/back, EarHeight up/down (was a 0.12 m sphere: half as wide, 15% lower).
        private const float DefaultEarRadius = 0.06f;
        private const float DefaultEarHeight = 0.102f;
        private static readonly Vector3[] defaultEarOffsets =
        {
            new Vector3(-0.12f, 0.26f, 0.02f), // left of the head, ear height (closer to the head than 0.17)
            new Vector3(0.12f, 0.26f, 0.02f),  // right of the head
        };
        private static readonly Vector3[] earOffsets = (Vector3[]) defaultEarOffsets.Clone();
        private static float earRadius = DefaultEarRadius;
        private static float earHeight = DefaultEarHeight;

        /// <summary>The body frame of this frame: set while the local player is in play (no cursor UI, alive, on foot).</summary>
        public static bool FrameValid { get; private set; }
        private static Vector3 frameHead;
        private static Quaternion frameYaw = Quaternion.identity;

        public static float EarRadius => earRadius;
        public static float EarHeight => earHeight;

        /// <summary>
        /// True when the point is inside the ear zone (0 left, 1 right); score = how deep (0 at the centre, 1 at the edge),
        /// to pick the nearer of the two.
        /// </summary>
        public static bool InEar(int side, Vector3 point, out float score)
        {
            Vector3 local = Quaternion.Inverse(frameYaw) * (point - EarCenter(side));
            float horizontal = (local.x * local.x + local.z * local.z) / (earRadius * earRadius);
            float vertical = local.y * local.y / (earHeight * earHeight);
            score = horizontal + vertical;
            return score <= 1f;
        }

        /// <summary>World centre of an ear zone (0 left, 1 right); only meaningful while <see cref="FrameValid"/>.</summary>
        public static Vector3 EarCenter(int side)
        {
            return frameHead + frameYaw * VRArms.NeckOffset + frameYaw * earOffsets[side];
        }
        // Switched in holsters.txt ("markers on", "log on"), so they work without a rebuild while wearing the headset.
        private static bool fileMarkers;
        private static bool fileLog;

        private const float TickAmplitude = 0.25f; // entering a zone: a light tick (was 0.5, too strong)
        private const float ActionAmplitude = 0.8f;
        private const float EmptyAmplitude = 0.3f;

        private static readonly bool[] previousGrip = new bool[2];
        private static readonly bool[] consumed = new bool[2];
        private static readonly int[] previousZone = { -1, -1 };
        private static KeyCode downKey = KeyCode.None;
        private static Vector3 lastFlat = Vector3.forward;
        private static DateTime loadedStamp = DateTime.MinValue;
        private static float nextFileCheck;
        private static string path;
        private static float nextLog;

        // Debug markers (VRDebugFlags.HolsterMarkers).
        private static LineRenderer[] markers;

        /// <summary>A zone used this hand's grip press; it must not reach the game or VRHold until released.</summary>
        public static bool Consumed(bool right)
        {
            return right ? consumed[VRHands.Right] : consumed[VRHands.Left];
        }

        /// <summary>The key the zones press this frame (one frame), or KeyCode.None.</summary>
        public static bool KeyDown(KeyCode key)
        {
            return downKey != KeyCode.None && key == downKey;
        }

        /// <summary>Once per frame from VRKeyMap with the raw grip states, before VRHold.</summary>
        public static void Update(bool leftGrip, bool rightGrip, bool uiOpen)
        {
            downKey = KeyCode.None;
            CheckFile();

            Camera eye = VRRig.ActiveCamera;
            Player player = Player.LocalPlayer;
            PlayerEquipment equipment = player != null ? player.equipment : null;
            bool usable = VRState.IsActive && eye != null && equipment != null && !uiOpen
                && !player.life.isDead && player.movement.getVehicle() == null;

            Vector3 head = default;
            Quaternion yaw = Quaternion.identity;
            if (usable)
            {
                head = eye.transform.position;
                Vector3 flat = eye.transform.forward;
                flat.y = 0f;
                if (flat.sqrMagnitude < 0.01f)
                {
                    flat = lastFlat;
                }
                flat.Normalize();
                lastFlat = flat;
                yaw = Quaternion.LookRotation(flat, Vector3.up);
            }

            FrameValid = usable;
            frameHead = head;
            frameYaw = yaw;

            int equippedSlot = usable ? EquippedSlot(equipment) : -1;
            UpdateMarkers(usable, head, yaw, equippedSlot, equipment);

            for (int hand = 0; hand < 2; hand++)
            {
                bool grip = hand == VRHands.Left ? leftGrip : rightGrip;
                bool down = grip && !previousGrip[hand];
                previousGrip[hand] = grip;
                if (!grip)
                {
                    consumed[hand] = false;
                }

                int zone = -1;
                Transform anchor = VRHands.Anchor(hand);
                if (usable && anchor != null && VRHands.IsTracked(hand))
                {
                    zone = FindZone(anchor.position, head, yaw);
                }

                int slot = zone >= 0 ? zoneSlot[zone] : -1; // the hotbar button of the zone (both shoulders are slot 0)
                bool actionable = zone >= 0 && Actionable(slot, equippedSlot, equipment);
                if (zone != previousZone[hand] && actionable)
                {
                    VRHaptics.Pulse(hand, TickAmplitude, 0.05f, 160f);
                }
                previousZone[hand] = zone;

                if ((VRDebugFlags.LogHolsters || fileLog) && usable && Time.unscaledTime >= nextLog && down)
                {
                    nextLog = Time.unscaledTime + 0.25f;
                    VRLog.Info("holsters: grip down, hand " + (hand == VRHands.Left ? "left" : "right") + " zone " + (zone >= 0 ? names[zone] : "none")
                        + ", equipped slot " + equippedSlot + ", busy=" + equipment.isBusy + ".");
                }

                if (!down || zone < 0 || equipment == null || equipment.isBusy || !equipment.canEquip)
                {
                    continue;
                }

                if (equipment.HasValidUseable && !equipment.IsEquipAnimationFinished)
                {
                    continue; // the game would ignore the key; leave the grip to its normal job
                }

                // Holster: this zone's item (or an item that has no zone of its own) is in a hand.
                bool holdsHere = equippedSlot == slot || equippedSlot == UnzonedItem;
                if (holdsHere)
                {
                    downKey = ControlsSettings.dequip;
                    consumed[hand] = true;
                    VRHaptics.Pulse(hand, ActionAmplitude, 0.15f, 120f);
                    VRLog.Info("holsters: " + names[zone] + " zone, holstered.");
                    continue;
                }

                if (SlotHasItem(slot, equipment))
                {
                    downKey = ControlsSettings.getEquipmentHotbarKeyCode(slot);
                    consumed[hand] = true;
                    VRHold.RequestHand(hand); // the drawing hand ends up holding it
                    VRHaptics.Pulse(hand, ActionAmplitude, 0.15f, 120f);
                    VRLog.Info("holsters: " + names[zone] + " zone, drawn with the " + (hand == VRHands.Left ? "left" : "right") + " hand.");
                    continue;
                }

                // An empty slot (nothing bound to this hotbar key): consume the press so it does not do something else.
                consumed[hand] = true;
                VRHaptics.Pulse(hand, EmptyAmplitude, 0.1f, 90f);
                VRLog.Info("holsters: " + names[zone] + " zone is empty" + (slot >= 2 ? " (bind an item to hotbar key " + (slot + 1) + " in the inventory)." : "."));
            }
        }

        // Equipped item that belongs to no zone (equipped from the inventory, or bound to hotbar 5-0).
        private const int UnzonedItem = -2;

        // -1 nothing held, 0..3 the zone slot of the held item, UnzonedItem when it has no zone.
        private static int EquippedSlot(PlayerEquipment equipment)
        {
            if (!equipment.HasValidUseable)
            {
                return -1;
            }

            byte page = equipment.equippedPage;
            if (page < PlayerInventory.SLOTS)
            {
                return page; // 0 primary, 1 secondary
            }

            HotkeyInfo[] hotkeys = equipment.hotkeys;
            if (hotkeys != null)
            {
                for (int i = 0; i < hotkeys.Length; i++)
                {
                    HotkeyInfo info = hotkeys[i];
                    if (info != null && info.id != 0 && info.page == page
                        && info.x == equipment.equipped_x && info.y == equipment.equipped_y)
                    {
                        int button = i + 2;
                        return button < ZoneCount ? button : UnzonedItem;
                    }
                }
            }
            return UnzonedItem;
        }

        private static bool SlotHasItem(int button, PlayerEquipment equipment)
        {
            if (button < PlayerInventory.SLOTS)
            {
                return equipment.player.inventory.getItem((byte) button, 0) != null;
            }

            HotkeyInfo[] hotkeys = equipment.hotkeys;
            int index = button - 2;
            return hotkeys != null && index >= 0 && index < hotkeys.Length && hotkeys[index] != null && hotkeys[index].id != 0;
        }

        // A zone is worth a rumble when a grip there would do something.
        private static bool Actionable(int slot, int equippedSlot, PlayerEquipment equipment)
        {
            return equippedSlot == slot || equippedSlot == UnzonedItem || SlotHasItem(slot, equipment);
        }

        private static Vector3 ZoneCenter(int zone, Vector3 head, Quaternion yaw)
        {
            Vector3 neck = head + yaw * VRArms.NeckOffset;
            return neck + yaw * offsets[zone];
        }

        private static int FindZone(Vector3 handPosition, Vector3 head, Quaternion yaw)
        {
            int best = -1;
            float bestDistance = radius * radius;
            for (int zone = 0; zone < ZoneCount; zone++)
            {
                float distance = (ZoneCenter(zone, head, yaw) - handPosition).sqrMagnitude;
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = zone;
                }
            }
            return best;
        }

        private static void UpdateMarkers(bool usable, Vector3 head, Quaternion yaw, int equippedSlot, PlayerEquipment equipment)
        {
            if (!(VRDebugFlags.HolsterMarkers || fileMarkers))
            {
                if (markers != null)
                {
                    foreach (LineRenderer marker in markers)
                    {
                        if (marker != null) marker.gameObject.SetActive(false);
                    }
                }
                return;
            }

            if (markers == null)
            {
                markers = new LineRenderer[ZoneCount + 2];
            }
            // Holster zones, then the two ear zones (blue) of the night vision gesture.
            for (int zone = 0; zone < ZoneCount + 2; zone++)
            {
                bool ear = zone >= ZoneCount;
                if (markers[zone] == null)
                {
                    markers[zone] = CreateMarker(ear ? (zone == ZoneCount ? "ear left" : "ear right") : names[zone]);
                }
                LineRenderer line = markers[zone];
                // On the mod's own layer: the overlay camera draws it (on top of the world), as it does for the laser.
                // The game's eye camera may not draw the default layer at all.
                if (VRGui.PanelLayer >= 0 && line.gameObject.layer != VRGui.PanelLayer)
                {
                    line.gameObject.layer = VRGui.PanelLayer;
                }
                line.gameObject.SetActive(usable);
                if (!usable)
                {
                    continue;
                }

                Color color = ear ? new Color(0.3f, 0.6f, 1f)
                    : equippedSlot == zoneSlot[zone] ? new Color(0.2f, 1f, 0.3f)
                    : SlotHasItem(zoneSlot[zone], equipment) ? new Color(1f, 0.8f, 0.2f) : new Color(0.6f, 0.6f, 0.6f);
                line.startColor = color;
                line.endColor = color;
                Vector3 center = ear ? head + yaw * VRArms.NeckOffset + yaw * earOffsets[zone - ZoneCount] : ZoneCenter(zone, head, yaw);
                float ringRadius = ear ? earRadius : radius;
                float ringHeight = ear ? earHeight : radius;
                const int steps = 24;
                for (int i = 0; i <= steps; i++)
                {
                    float angle = i * Mathf.PI * 2f / steps;
                    // Horizontal ring (sideways x forward) and an upright ring (sideways x up).
                    line.SetPosition(i, center + yaw * new Vector3(Mathf.Cos(angle) * ringRadius, 0f, Mathf.Sin(angle) * ringRadius));
                    line.SetPosition(steps + 1 + i, center + yaw * new Vector3(Mathf.Cos(angle) * ringRadius, Mathf.Sin(angle) * ringHeight, 0f));
                }
            }
        }

        private static LineRenderer CreateMarker(string name)
        {
            GameObject go = new GameObject("UnturnedVR Holster " + name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.material = new Material(Canvas.GetDefaultCanvasMaterial());
            line.startWidth = 0.008f;
            line.endWidth = 0.008f;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.positionCount = 2 * 25;
            return line;
        }

        // ---- holsters.txt ---------------------------------------------------------------------------------------
        private static void CheckFile()
        {
            if (Time.unscaledTime < nextFileCheck)
            {
                return;
            }
            nextFileCheck = Time.unscaledTime + 2f;

            try
            {
                if (path == null)
                {
                    path = Path.Combine(Path.GetDirectoryName(typeof(VRHolsters).Assembly.Location), FileName);
                }
                if (!File.Exists(path))
                {
                    File.WriteAllText(path,
                        "# Your own holster zone overrides. Empty = the mod's built-in values. Saved changes apply within a couple of seconds.\n" +
                        "# <zone> x y z   zone = primary | primaryleft | secondary | slot3 | slot4; x right, y up, z forward in metres\n" +
                        "# from the neck (about 28 cm under the eye), turned with your head's heading. primary and primaryleft are the\n" +
                        "# two shoulders (same hotbar slot, either hand uses either); primaryleft mirrors primary unless you set it.\n" +
                        "# radius <metres>   how close the hand must be to a zone (default 0.14)\n" +
                        "# Examples (remove the # to use):\n" +
                        "# primary 0.22 0.12 -0.18\n" +
                        "# secondary -0.25 -0.55 0\n" +
                        "# radius 0.16\n");
                }

                // Unchanged since the last read: nothing more to do (the usual case; two cheap calls every 2 s).
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == loadedStamp)
                {
                    return;
                }
                loadedStamp = stamp;

                string[] lines = File.ReadAllLines(path);
                bool hasSwitches = false;
                foreach (string line in lines)
                {
                    if (line.IndexOf("markers", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hasSwitches = true;
                        break;
                    }
                }
                if (!hasSwitches)
                {
                    // A new file, or one made by an earlier version, lacks the switches: add them (markers off, a player
                    // switches them on while tuning the zones) and read it again on the next check.
                    File.AppendAllText(path,
                        "\n# markers on|off    draw the zones as rings in the game (yellow = has an item, green = held, grey = empty)\n" +
                        "markers off\n" +
                        "# log on|off        log every grip press near the body with the zone it hit\n" +
                        "# log on\n");
                    loadedStamp = DateTime.MinValue;
                    return;
                }

                Array.Copy(defaultOffsets, offsets, ZoneCount);
                radius = DefaultRadius;
                Array.Copy(defaultEarOffsets, earOffsets, 2);
                earRadius = DefaultEarRadius;
                earHeight = DefaultEarHeight;
                fileMarkers = false;
                fileLog = false;
                fileHasPrimaryLeft = false;
                foreach (string raw in lines)
                {
                    ParseLine(raw);
                }
                if (!fileHasPrimaryLeft)
                {
                    // The left shoulder mirrors a tuned right shoulder unless the file places it itself.
                    Vector3 right = offsets[0];
                    offsets[PrimaryLeftZone] = new Vector3(-right.x, right.y, right.z);
                }
                VRLog.Info("holsters: read " + FileName + " (radius " + radius.ToString("0.00", CultureInfo.InvariantCulture) + " m).");
            }
            catch (Exception exception)
            {
                VRLog.Warn("holsters: cannot read " + FileName + ": " + exception.Message);
            }
        }

        private static void ParseLine(string raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                return;
            }

            string[] parts = line.Split((char[]) null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Equals("radius", StringComparison.OrdinalIgnoreCase)
                && TryFloat(parts[1], out float value) && value > 0.02f && value < 0.6f)
            {
                radius = value;
                return;
            }

            if (parts.Length == 2 && parts[0].Equals("earradius", StringComparison.OrdinalIgnoreCase)
                && TryFloat(parts[1], out float earValue) && earValue > 0.02f && earValue < 0.4f)
            {
                earRadius = earValue;
                return;
            }

            if (parts.Length == 2 && parts[0].Equals("earheight", StringComparison.OrdinalIgnoreCase)
                && TryFloat(parts[1], out float earHeightValue) && earHeightValue > 0.02f && earHeightValue < 0.4f)
            {
                earHeight = earHeightValue;
                return;
            }

            if (parts.Length == 4 && (parts[0].Equals("earleft", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("earright", StringComparison.OrdinalIgnoreCase)))
            {
                if (TryFloat(parts[1], out float ex) && TryFloat(parts[2], out float ey) && TryFloat(parts[3], out float ez))
                {
                    earOffsets[parts[0].Equals("earleft", StringComparison.OrdinalIgnoreCase) ? 0 : 1] = new Vector3(ex, ey, ez);
                    return;
                }
            }

            if (parts.Length == 2 && (parts[0].Equals("markers", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("log", StringComparison.OrdinalIgnoreCase)))
            {
                bool on = parts[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                if (parts[0].Equals("markers", StringComparison.OrdinalIgnoreCase))
                {
                    fileMarkers = on;
                }
                else
                {
                    fileLog = on;
                }
                return;
            }

            if (parts.Length == 4)
            {
                for (int zone = 0; zone < ZoneCount; zone++)
                {
                    if (parts[0].Equals(names[zone], StringComparison.OrdinalIgnoreCase)
                        && TryFloat(parts[1], out float x) && TryFloat(parts[2], out float y) && TryFloat(parts[3], out float z))
                    {
                        offsets[zone] = new Vector3(x, y, z);
                        if (zone == PrimaryLeftZone)
                        {
                            fileHasPrimaryLeft = true;
                        }
                        return;
                    }
                }
            }
            VRLog.Warn("holsters: ignored line \"" + line + "\".");
        }

        private static bool TryFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
