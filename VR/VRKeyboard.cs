using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using Valve.VR;

namespace UnturnedVR.VR
{
    /// <summary>
    /// The SteamVR keyboard for the game's text fields. Glazier's string and number fields are TMP_InputField
    /// components; while one of them is focused (clicked with the laser, or focused by the game, e.g. chat) the SteamVR
    /// overlay keyboard is shown with the field's text, and what is typed on it is copied back into the field, so the
    /// game sees ordinary typing (its own value-changed and submit handlers run). Flow as in Valve's unity_keyboard_sample
    /// (IVROverlay.ShowKeyboard, the KeyboardCharInput / KeyboardDone / KeyboardClosed events and GetKeyboardText).
    ///
    /// What a keystroke reports differs between SteamVR versions: the event payload (cNewInput) is empty, and
    /// GetKeyboardText returns only the newest keystroke (seen here), not the whole text. The char events reach a poller
    /// only when it asks before the XR plugin does. So, while the keyboard is up, a worker thread asks about every few
    /// milliseconds (frame hitches and the plugin's own polling no longer lose keys) and keeps the text itself:
    /// a change of the value of GetKeyboardText is a keystroke, a char event that has no change is the same key typed
    /// again, and the event that belongs to a change seen first is swallowed (credit). The thread only touches OpenVR
    /// and strings; the main thread writes the finished text into the field. Cost while no field is focused: one
    /// EventSystem lookup per frame, no OpenVR calls. If the thread fails, the same step runs on the main thread.
    /// There is no placement or dragging of the keyboard: SteamVR places it, and its own bar moves it.
    ///
    /// Number fields (port, ...): the game parses every change and puts the last valid number back when the text is empty
    /// or invalid, so the last digit could never be deleted and the next digit was appended to it. An emptied number
    /// field is shown empty without telling the game (SetTextWithoutNotify) until a digit is typed; when the keyboard
    /// closes empty the last valid text is put back.
    /// </summary>
    internal sealed class VRKeyboard : MonoBehaviour
    {
        private const uint DefaultCharMax = 256;
        private const int TextCapacity = 1024;
        private const int PollSleepMs = 4;
        private const int CreditMs = 400;     // a change seen by the poll is matched with its event within this time
        private const int MaxDiagLines = 200;

        private enum End { None, Done, Closed }

        private Session session;            // the open keyboard, null when none
        private TMP_InputField active;      // the field the keyboard is open for
        private bool numeric;               // the field is a number field (see above)
        private string lastNonEmpty = string.Empty;
        private bool drainEvents;           // the keyboard was hidden by us: its late closed event must not close the next one
        private int diagLines;
        private bool loggedShown;
        private bool loggedEvent;
        private bool loggedUnavailable;
        private VREvent_t drainEvent;

        public static void Create()
        {
            GameObject go = new GameObject("UnturnedVR Keyboard");
            DontDestroyOnLoad(go);
            go.AddComponent<VRKeyboard>();
        }

        private void OnDestroy()
        {
            if (session != null)
            {
                session.Running = false;
            }
        }

        private void Update()
        {
            if (!VRState.IsActive)
            {
                if (session != null)
                {
                    Hide();
                }
                return;
            }

            if (drainEvents && session == null)
            {
                drainEvents = false;
                Drain();
            }

            TMP_InputField selected = FocusedField();

            if (session != null)
            {
                Apply();
                if (session != null && selected != active)
                {
                    Diag("hide: the focused field is not the keyboard's field any more (" + (selected == null ? "nothing focused" : "another field") + ")");
                    Hide(); // clicked somewhere else, or the field went away
                }
            }

            if (session == null && selected != null)
            {
                Show(selected);
            }
        }

        // The focused text field of the game's UI, or null. A button or slider being selected is not one.
        private static TMP_InputField FocusedField()
        {
            EventSystem system = EventSystem.current;
            GameObject selected = system != null ? system.currentSelectedGameObject : null;
            if (selected == null)
            {
                return null;
            }

            TMP_InputField field = selected.GetComponent<TMP_InputField>();
            return field != null && field.isActiveAndEnabled && field.isFocused && field.interactable ? field : null;
        }

        private void Show(TMP_InputField field)
        {
            CVROverlay overlay = OpenVR.Overlay;
            if (overlay == null)
            {
                if (!loggedUnavailable)
                {
                    loggedUnavailable = true;
                    VRLog.Warn("keyboard: OpenVR.Overlay is not available, the SteamVR keyboard cannot be shown.");
                }
                return;
            }

            bool password = field.contentType == TMP_InputField.ContentType.Password;
            bool multiline = field.lineType != TMP_InputField.LineType.SingleLine;
            uint charMax = field.characterLimit > 0 ? (uint)field.characterLimit : DefaultCharMax;
            string description = "Text";
            TMP_Text placeholder = field.placeholder as TMP_Text;
            if (placeholder != null && !string.IsNullOrEmpty(placeholder.text))
            {
                description = placeholder.text;
            }

            string existing = field.text ?? string.Empty;
            EVROverlayError error = overlay.ShowKeyboard(
                (int)(password ? EGamepadTextInputMode.k_EGamepadTextInputModePassword : EGamepadTextInputMode.k_EGamepadTextInputModeNormal),
                (int)(multiline ? EGamepadTextInputLineMode.k_EGamepadTextInputLineModeMultipleLines : EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine),
                0u, description, charMax, existing, 0UL); // flags 0: the normal keyboard with its own text box (not minimal)
            if (error != EVROverlayError.None)
            {
                if (!loggedUnavailable)
                {
                    loggedUnavailable = true;
                    VRLog.Warn("keyboard: ShowKeyboard failed: " + error);
                }
                // Not retried every frame: the field has to be re-selected.
                EventSystem.current.SetSelectedGameObject(null);
                return;
            }

            active = field;
            numeric = field.contentType == TMP_InputField.ContentType.IntegerNumber
                || field.contentType == TMP_InputField.ContentType.DecimalNumber;
            lastNonEmpty = existing;

            // What is still readable from an earlier keyboard is not a keystroke: the first read is the starting point.
            session = new Session(existing, Session.ReadText(overlay, new StringBuilder(TextCapacity)));
            Diag("shown (" + (numeric ? "number field" : "text field") + "), field text=\"" + existing + "\", first read=\"" + session.StartRead + "\"");
            StartWorker(session);

            if (!loggedShown)
            {
                loggedShown = true;
                VRLog.Info("keyboard: SteamVR keyboard shown for a text field (\"" + description + "\", max " + charMax + ").");
            }
        }

        private void StartWorker(Session s)
        {
            try
            {
                Thread thread = new Thread(() => Worker(s))
                {
                    IsBackground = true,
                    Name = "UnturnedVR keyboard",
                };
                thread.Start();
            }
            catch (Exception e)
            {
                s.WorkerDead = true;
                VRLog.Warn("keyboard: worker thread could not start (" + e.Message + "), polling on the main thread.");
            }
        }

        private static void Worker(Session s)
        {
            try
            {
                while (s.Running && !s.Ended)
                {
                    s.Step();
                    Thread.Sleep(PollSleepMs);
                }
            }
            catch (Exception e)
            {
                lock (s.Lock)
                {
                    s.Failure = e.GetType().Name + ": " + e.Message;
                }
                s.WorkerDead = true;
            }
        }

        // Main thread, every frame while the keyboard is up: take what the worker has finished.
        private void Apply()
        {
            Session s = session;
            if (s.WorkerDead)
            {
                string failure;
                lock (s.Lock)
                {
                    failure = s.Failure;
                    s.Failure = null;
                }
                if (failure != null)
                {
                    VRLog.Warn("keyboard: worker thread failed (" + failure + "), polling on the main thread.");
                }
                s.Step();
            }

            string text;
            End end;
            bool quit;
            bool sawChar;
            List<string> lines = null;
            lock (s.Lock)
            {
                text = s.PendingText;
                s.PendingText = null;
                end = s.EndKind;
                quit = s.QuitRequested;
                sawChar = s.SawChar;
                if (s.DiagLines.Count > 0)
                {
                    lines = new List<string>(s.DiagLines);
                    s.DiagLines.Clear();
                }
            }

            if (lines != null)
            {
                foreach (string line in lines)
                {
                    Diag(line);
                }
            }
            if (sawChar && !loggedEvent)
            {
                loggedEvent = true;
                VRLog.Info("keyboard: first SteamVR keyboard event received (char input).");
            }

            if (text != null)
            {
                WriteField(s, text);
            }

            if (quit)
            {
                // The event was taken from the queue, so the plugin never saw it: quit the way it would have.
                CVRSystem system = OpenVR.System;
                if (system != null)
                {
                    system.AcknowledgeQuit_Exiting();
                }
                Application.Quit();
            }

            if (end != End.None)
            {
                Diag((end == End.Done ? "done" : "closed") + ", field text=\"" + (active != null ? active.text : string.Empty) + "\"");
                Finish(end == End.Done);
            }
        }

        private void WriteField(Session s, string text)
        {
            if (active == null)
            {
                return;
            }

            if (text.Length == 0 && numeric)
            {
                active.SetTextWithoutNotify(string.Empty); // the game would put the last number back (see the class doc)
            }
            else
            {
                active.text = text; // raises the field's value-changed handlers, like typing (the field applies its length limit)
            }
            active.MoveTextEnd(false);

            string after = active.text ?? string.Empty;
            if (after.Length > 0)
            {
                lastNonEmpty = after;
            }
            if (after != text)
            {
                s.Adopt(text, after); // limited or put back by the game: that is the text now
            }
        }

        // A number field that is shown empty (see the class doc) gets its last valid text back.
        private void RestoreNumber(TMP_InputField field)
        {
            if (numeric && field != null && string.IsNullOrEmpty(field.text) && lastNonEmpty.Length > 0)
            {
                field.SetTextWithoutNotify(lastNonEmpty);
            }
        }

        private void Hide()
        {
            RestoreNumber(active);
            CVROverlay overlay = OpenVR.Overlay;
            if (overlay != null)
            {
                overlay.HideKeyboard();
            }
            if (session != null)
            {
                session.Running = false;
            }
            session = null;
            active = null;
            drainEvents = true;
        }

        // The user closed the keyboard (Done, or the close button): take the focus off the field so a click focuses it
        // again and brings the keyboard back.
        private void Finish(bool submitted)
        {
            TMP_InputField field = active;
            if (session != null)
            {
                session.Running = false;
            }
            session = null;
            active = null;
            if (field == null)
            {
                return;
            }

            RestoreNumber(field);
            if (submitted)
            {
                field.onSubmit.Invoke(field.text);
            }
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == field.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
        }

        // Throws away the events queued while no keyboard was up (our own Hide produces a closed event).
        private void Drain()
        {
            CVRSystem system = OpenVR.System;
            if (system == null)
            {
                return;
            }

            uint size = (uint)Marshal.SizeOf(typeof(VREvent_t));
            int guard = 64;
            while (guard-- > 0 && system.PollNextEvent(ref drainEvent, size))
            {
            }
        }

        // Diagnostics for the keyboard text (VRDebugFlags.LogKeyboard), limited so a long session cannot flood the log.
        private void Diag(string message)
        {
            if (!VRDebugFlags.LogKeyboard || diagLines >= MaxDiagLines)
            {
                return;
            }
            diagLines++;
            VRLog.Info("keyboard: " + message);
        }

        // Applies keystrokes to the text the way the field would: backspace removes the last character (a whole emoji, which is
        // two UTF-16 characters), other control characters (enter, escape; done / closed come as their own events) and the
        // escape sequences of the arrow, home, end and delete keys do nothing (the field's caret is not moved from here).
        private static string Merge(string current, string keystrokes)
        {
            if (IsEscapeSequence(keystrokes))
            {
                return current;
            }

            StringBuilder text = new StringBuilder(current);
            foreach (char c in keystrokes)
            {
                if (c == '\b')
                {
                    if (text.Length > 1 && char.IsLowSurrogate(text[text.Length - 1]) && char.IsHighSurrogate(text[text.Length - 2]))
                    {
                        text.Length -= 2;
                    }
                    else if (text.Length > 0)
                    {
                        text.Length--;
                    }
                }
                else if (c >= ' ' && c != '\u007f')
                {
                    text.Append(c);
                }
            }
            return text.ToString();
        }

        // ESC [ D (left arrow), ESC [ H, ESC [ 3 ~ ... as a keyboard that sends terminal style sequences reports them. The ESC
        // itself may be missing from what is read, so "[" followed by up to three digits / ';' and a letter or '~' counts too.
        private static bool IsEscapeSequence(string keystrokes)
        {
            int start = keystrokes.Length > 0 && keystrokes[0] == '\u001b' ? 1 : 0;
            if (start == 1 && keystrokes.Length == 1)
            {
                return true; // a lone escape
            }
            if (keystrokes.Length - start < 2 || keystrokes.Length - start > 6 || keystrokes[start] != '[')
            {
                return start == 1; // ESC followed by anything: a sequence
            }

            for (int i = start + 1; i < keystrokes.Length - 1; i++)
            {
                if (!char.IsDigit(keystrokes[i]) && keystrokes[i] != ';')
                {
                    return start == 1;
                }
            }
            char last = keystrokes[keystrokes.Length - 1];
            return last == '~' || (last >= 'A' && last <= 'Z');
        }

        // Is a read the keyboard's whole text? Returns it, or null when it is a keystroke to merge into the current text.
        // Judged per read against what the field has (a keyboard that reports whole text continues or shortens it by
        // one; one that reports keystrokes gives a single new character or a backspace).
        private static string AsWholeText(string read, string current)
        {
            if (read.IndexOf('\b') >= 0)
            {
                return null; // a whole text never holds a raw backspace
            }
            if (read == current)
            {
                // Unchanged: a repeated whole text, or the same character typed again. With a single character the second
                // is far more likely.
                return current.Length > 1 ? current : null;
            }
            if (read.Length > current.Length && read.StartsWith(current, StringComparison.Ordinal))
            {
                return read;
            }
            if (read.Length >= 2 && read.Length == current.Length - 1 && current.StartsWith(read, StringComparison.Ordinal))
            {
                return read;
            }
            return null;
        }

        /// <summary>
        /// One open keyboard: the polling and the text rules. Step runs on the worker thread (or on the main thread if the
        /// worker is gone), never on both; everything the main thread reads is behind Lock. No Unity API in here.
        /// </summary>
        private sealed class Session
        {
            public readonly object Lock = new object();
            public volatile bool Running = true;
            public volatile bool Ended;
            public volatile bool WorkerDead;
            public string StartRead;

            // Under Lock: what the main thread takes.
            public string PendingText;      // the newest text to write into the field, null = nothing new
            public End EndKind;
            public bool QuitRequested;
            public bool SawChar;
            public string Failure;
            public readonly List<string> DiagLines = new List<string>();

            // Under Lock too (Adopt is called from the main thread).
            private string shadow;          // what the field holds, edited per keystroke
            private string lastRead;        // the value of GetKeyboardText that has been dealt with
            private bool wholeMode;         // GetKeyboardText has been seen to return the keyboard's whole text
            private int credit;             // char events still expected for keystrokes already seen as a change
            private long creditUntil;
            private int diagCount;

            private readonly StringBuilder buffer = new StringBuilder(TextCapacity);
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly uint eventSize = (uint)Marshal.SizeOf(typeof(VREvent_t));
            private VREvent_t vrEvent;

            public Session(string fieldText, string firstRead)
            {
                shadow = fieldText;
                lastRead = firstRead;
                StartRead = firstRead;
            }

            public static string ReadText(CVROverlay overlay, StringBuilder buffer)
            {
                buffer.Length = 0;
                overlay.GetKeyboardText(buffer, TextCapacity);
                string text = buffer.ToString();
                return text.IndexOf('\0') >= 0 ? text.Replace("\0", string.Empty) : text;
            }

            /// <summary>The field did not keep the text that was written (length limit, number put back): that is the text now.</summary>
            public void Adopt(string written, string actual)
            {
                lock (Lock)
                {
                    if (PendingText == null && shadow == written)
                    {
                        shadow = actual;
                    }
                }
            }

            public void Step()
            {
                if (Ended)
                {
                    return;
                }

                CVRSystem system = OpenVR.System;
                CVROverlay overlay = OpenVR.Overlay;
                if (system == null || overlay == null)
                {
                    return;
                }

                // Events first, then the text: the text of a keystroke is updated before its event is posted, so a text read after
                // the events already shows every keystroke whose event was just taken.
                int chars = 0;
                End end = End.None;
                bool quit = false;
                while (system.PollNextEvent(ref vrEvent, eventSize))
                {
                    switch ((EVREventType)vrEvent.eventType)
                    {
                        case EVREventType.VREvent_KeyboardCharInput:
                            chars++;
                            break;
                        case EVREventType.VREvent_KeyboardDone:
                            end = End.Done;
                            break;
                        case EVREventType.VREvent_KeyboardClosed:
                            if (end == End.None)
                            {
                                end = End.Closed;
                            }
                            break;
                        case EVREventType.VREvent_Quit:
                        case EVREventType.VREvent_DriverRequestedQuit:
                            quit = true;
                            break;
                    }
                }

                string read = ReadText(overlay, buffer);
                long now = clock.ElapsedMilliseconds;

                lock (Lock)
                {
                    if (credit > 0 && now > creditUntil)
                    {
                        credit = 0; // that event was not delivered to us
                    }

                    string current = shadow;
                    string next = current;
                    bool changed = read != lastRead;
                    int extra = chars;

                    if (changed)
                    {
                        lastRead = read;
                        if (read.Length == 0)
                        {
                            // Reset by the keyboard. A keyboard that reports its whole text does that when its box was emptied.
                            if (wholeMode && current.Length > 0)
                            {
                                next = string.Empty;
                            }
                        }
                        else
                        {
                            string whole = AsWholeText(read, current);
                            if (whole != null)
                            {
                                next = whole;
                                if (read.Length >= 2)
                                {
                                    wholeMode = true;
                                }
                            }
                            else
                            {
                                next = Merge(current, read);
                            }

                            // This change has its own event, now or in a moment.
                            extra = Math.Max(0, chars - 1);
                            credit = chars == 0 ? 1 : 0;
                            creditUntil = now + CreditMs;
                        }
                    }
                    else if (chars > 0)
                    {
                        int used = Math.Min(credit, chars);
                        credit -= used;
                        extra = chars - used;
                    }

                    // The same character again: the read did not change, but the keyboard says a key was pressed.
                    if (extra > 0 && !wholeMode && read.Length == 1 && (read[0] >= ' ' || read[0] == '\b'))
                    {
                        for (int i = 0; i < extra; i++)
                        {
                            next = Merge(next, read);
                        }
                    }

                    if ((changed || chars > 0) && diagCount < MaxDiagLines)
                    {
                        diagCount++;
                        DiagLines.Add("read=\"" + read.Replace("\b", "<BS>") + "\" changed=" + changed + " events=" + chars
                            + " credit=" + credit + " whole=" + wholeMode + " text=\"" + current + "\" -> \"" + next + "\"");
                    }

                    if (next != current)
                    {
                        shadow = next;
                        PendingText = next;
                    }
                    if (chars > 0)
                    {
                        SawChar = true;
                    }
                    if (quit)
                    {
                        QuitRequested = true;
                    }
                    if (end != End.None)
                    {
                        EndKind = end;
                        Ended = true;
                    }
                }
            }
        }
    }
}
