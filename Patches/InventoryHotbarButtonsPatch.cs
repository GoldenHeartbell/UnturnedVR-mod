using System;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;
using UnturnedVR.VR;

namespace UnturnedVR.Patches
{
    /// <summary>
    /// Two more buttons in the item panel of the inventory (the one that opens with a right press on an item, with Equip /
    /// Drop / Store): "Hotbar 3" and "Hotbar 4", one row at the bottom of the list. They put the selected item on those hotbar
    /// slots, which are the left and right chest holsters (VRHolsters). On a keyboard the same thing is done by pressing 3 or 4
    /// with the item selected; there is no such key press in VR, so the buttons call the game's own routine for it
    /// (PlayerEquipment.bindHotkey: same checks, same duplicate clearing, closes the panel). The buttons only exist where the
    /// game would accept the key: items in the inventory pages that can be equipped there.
    /// The game rebuilds the panel's extra-actions box on every selection (PlayerDashboardInventoryUI.openSelection), so the
    /// buttons are added to it right after, and the scroll content grows by one row.
    /// Cost: one reflection read pair and two buttons per selection; nothing while the inventory is closed.
    /// </summary>
    internal static class InventoryHotbarButtonsPatch
    {
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int RowHeight = 40;     // as the game's rows: a 30 px button and 10 px gap
        private const byte FirstButton = 3;   // hotbar key 3 = hotkeys[0] = left chest holster
        private const byte SecondButton = 4;  // hotbar key 4 = hotkeys[1] = right chest holster

        private static readonly FieldInfo extraBoxField = typeof(PlayerDashboardInventoryUI).GetField("selectionExtraActionsBox", AnyStatic);
        private static readonly FieldInfo actionsBoxField = typeof(PlayerDashboardInventoryUI).GetField("selectionActionsBox", AnyStatic);
        private static readonly MethodInfo bindHotkey = typeof(PlayerEquipment).GetMethod("bindHotkey", AnyInstance);
        private static bool failed;
        private static bool loggedAdded;

        [HarmonyPatch(typeof(PlayerDashboardInventoryUI), "openSelection")]
        internal static class OpenSelection
        {
            private static void Postfix()
            {
                if (failed || !VRState.IsActive)
                {
                    return;
                }

                try
                {
                    AddButtons();
                }
                catch (Exception e)
                {
                    failed = true;
                    VRLog.Warn("inventory: could not add the hotbar buttons: " + e.Message);
                }
            }
        }

        private static void AddButtons()
        {
            byte page = PlayerDashboardInventoryUI.selectedPage;
            ItemJar jar = PlayerDashboardInventoryUI.selectedJar;
            Player player = Player.LocalPlayer;
            // The same condition the game's bindHotkey has for binding an item.
            if (jar == null || player == null || page < PlayerInventory.SLOTS || page >= PlayerInventory.STORAGE
                || !ItemTool.checkUseable(page, jar.item.id))
            {
                return;
            }

            ISleekElement extraBox = extraBoxField != null ? extraBoxField.GetValue(null) as ISleekElement : null;
            ISleekScrollView actionsBox = actionsBoxField != null ? actionsBoxField.GetValue(null) as ISleekScrollView : null;
            if (extraBox == null || actionsBox == null || bindHotkey == null)
            {
                failed = true;
                VRLog.Warn("inventory: item panel widgets or PlayerEquipment.bindHotkey not found, no hotbar buttons.");
                return;
            }

            // The extras box is as tall as its rows minus the last gap (-10 when it is empty).
            int rows = Mathf.RoundToInt(extraBox.SizeOffset_Y) + 10;

            AddButton(extraBox, rows, 0f, FirstButton, player);
            AddButton(extraBox, rows, 0.5f, SecondButton, player);

            extraBox.SizeOffset_Y = rows + RowHeight - 10;
            Vector2 content = actionsBox.ContentSizeOffset;
            actionsBox.ContentSizeOffset = new Vector2(content.x, content.y + RowHeight);

            if (!loggedAdded)
            {
                loggedAdded = true;
                VRLog.Info("inventory: hotbar 3 / 4 buttons added to the item panel.");
            }
        }

        private static void AddButton(ISleekElement parent, int y, float x, byte button, Player player)
        {
            ISleekButton element = Glazier.Get().CreateButton();
            element.PositionScale_X = x;
            element.PositionOffset_X = x > 0f ? 5 : 0;
            element.PositionOffset_Y = y;
            element.SizeScale_X = 0.5f;
            element.SizeOffset_X = -5;
            element.SizeOffset_Y = 30;
            element.Text = "Hotbar " + button + (IsBound(player, button) ? " (set)" : string.Empty);
            element.TooltipText = "Put this item on hotbar " + button + ", the " + (button == FirstButton ? "left" : "right") + " chest holster.";
            element.OnClicked += _ => Bind(button);
            parent.AddChild(element);
        }

        // The selected item is already on this hotkey (hotkeys[0] is button 3).
        private static bool IsBound(Player player, byte button)
        {
            HotkeyInfo[] hotkeys = player.equipment.hotkeys;
            int index = button - 1 - PlayerInventory.SLOTS;
            if (hotkeys == null || index < 0 || index >= hotkeys.Length || hotkeys[index] == null)
            {
                return false;
            }
            HotkeyInfo info = hotkeys[index];
            return info.id != 0 && info.page == PlayerDashboardInventoryUI.selectedPage
                && info.x == PlayerDashboardInventoryUI.selected_x && info.y == PlayerDashboardInventoryUI.selected_y;
        }

        // bindHotkey takes the game's button number: 2 = key 3, 3 = key 4 (the keys are shown as 1-based).
        private static void Bind(byte keyNumber)
        {
            Player player = Player.LocalPlayer;
            if (player == null || bindHotkey == null)
            {
                return;
            }

            try
            {
                bindHotkey.Invoke(player.equipment, new object[] { (byte)(keyNumber - 1) });
            }
            catch (Exception e)
            {
                VRLog.Warn("inventory: binding hotbar " + keyNumber + " failed: " + (e.InnerException ?? e).Message);
            }
        }
    }
}
