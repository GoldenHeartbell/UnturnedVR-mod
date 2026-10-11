using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The game window's Windows focus. In VR the player often works on the desktop (SteamVR dashboard, another
    /// program), which takes the focus from the game; the UI then ignores every event (UGUI's
    /// EventSystem.isFocused, GUESS) so the laser cannot click until the PC mouse clicks in the window. VRPointer
    /// uses this to take the focus back on the first menu click. Opening the pause menu on focus loss is left to
    /// the game (OptionsSettings.pauseWhenUnfocused). Not in the editor: its Game view focus is not a window focus.
    /// </summary>
    internal static class VRFocus
    {
        private const string UnityWindowClass = "UnityWndClass";
        private const int SwRestore = 9;
        private const byte VkMenu = 0x12;
        private const uint KeyEventKeyUp = 0x0002;
        private const float LogInterval = 1f;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static readonly EnumWindowsProc findWindowCallback = FindWindowCallback;
        private static readonly uint ownProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        private static IntPtr window = IntPtr.Zero;
        private static IntPtr found = IntPtr.Zero;
        private static bool broken;
        private static float nextLog;

        /// <summary>True while the game window is not the foreground window (Windows focus lost).</summary>
        public static bool Unfocused
        {
            get
            {
#if UNITY_EDITOR
                return false;
#else
                if (!Application.isFocused)
                {
                    return true;
                }
                if (broken)
                {
                    return false;
                }

                // Unity may not flip isFocused under XR, so also ask Windows who has the foreground.
                try
                {
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
                    return processId != ownProcessId;
                }
                catch (Exception e)
                {
                    Disable(e);
                    return false;
                }
#endif
            }
        }

        /// <summary>Brings the game window to the foreground. Returns true when it ended up there.</summary>
        public static bool RequestRefocus()
        {
#if UNITY_EDITOR
            return false;
#else
            if (broken)
            {
                return false;
            }

            try
            {
                IntPtr hwnd = FindGameWindow();
                if (hwnd == IntPtr.Zero)
                {
                    Log("focus request: game window not found.");
                    return false;
                }

                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SwRestore);
                }

                SetForegroundWindow(hwnd);
                string how = "SetForegroundWindow";

                // Windows only lets the process that got the last input steal the foreground. A synthetic Alt tap counts
                // as input from us; GUESS: this and the thread attach below are what works when the plain call is refused.
                if (GetForegroundWindow() != hwnd)
                {
                    keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
                    keybd_event(VkMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
                    SetForegroundWindow(hwnd);
                    how = "Alt tap";
                }

                if (GetForegroundWindow() != hwnd)
                {
                    uint ownThread = GetCurrentThreadId();
                    uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out uint _);
                    bool attached = foregroundThread != 0 && foregroundThread != ownThread
                        && AttachThreadInput(ownThread, foregroundThread, true);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    SetFocus(hwnd);
                    if (attached)
                    {
                        AttachThreadInput(ownThread, foregroundThread, false);
                    }
                    how = "thread attach";
                }

                bool ours = GetForegroundWindow() == hwnd;
                Log("focus request (" + how + "): foreground=" + (ours ? "ours" : "other") + ".");
                return ours;
            }
            catch (Exception e)
            {
                Disable(e);
                return false;
            }
#endif
        }

        private static IntPtr FindGameWindow()
        {
            if (window != IntPtr.Zero && IsWindow(window))
            {
                return window;
            }

            found = IntPtr.Zero;
            EnumWindows(findWindowCallback, IntPtr.Zero);
            window = found;
            return window;
        }

        private static bool FindWindowCallback(IntPtr hWnd, IntPtr lParam)
        {
            GetWindowThreadProcessId(hWnd, out uint processId);
            if (processId != ownProcessId || !IsWindowVisible(hWnd))
            {
                return true;
            }

            var className = new StringBuilder(64);
            GetClassName(hWnd, className, className.Capacity);
            if (className.ToString() == UnityWindowClass)
            {
                found = hWnd;
                return false;
            }
            return true;
        }

        // Rate-limited: a player can hammer the trigger while the request keeps failing.
        private static void Log(string message)
        {
            if (Time.unscaledTime < nextLog)
            {
                return;
            }
            nextLog = Time.unscaledTime + LogInterval;
            VRLog.Info(message);
        }

        private static void Disable(Exception e)
        {
            broken = true;
            VRLog.Warn("window focus handling disabled: " + e.GetType().Name + ": " + e.Message);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
