using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>Global keyboard shortcut (works even when Mimi doesn't have focus).</summary>
    sealed class GlobalHotkey : NativeWindow, IDisposable
    {
        public const int MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8;
        const int Id = 0x4D31;
        bool registered;

        public event Action Pressed;

        public GlobalHotkey()
        {
            // invisible "message-only" window
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        public bool Set(int mods, Keys key)
        {
            Clear();
            if (key == Keys.None) return false;
            registered = Native.RegisterHotKey(Handle, Id, (uint)mods, (uint)key);
            return registered;
        }

        public void Clear()
        {
            if (registered) Native.UnregisterHotKey(Handle, Id);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == Id) Pressed?.Invoke();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Clear();
            DestroyHandle();
        }

        public static bool IsStandaloneKey(Keys k) =>
            (k >= Keys.F1 && k <= Keys.F24) || k == Keys.Pause || k == Keys.Scroll ||
            k == Keys.Insert || (k >= Keys.NumPad0 && k <= Keys.Divide);

        public static string Describe(int mods, Keys key)
        {
            var parts = new List<string>();
            if ((mods & MOD_CONTROL) != 0) parts.Add(L.T("key.ctrl"));
            if ((mods & MOD_ALT) != 0) parts.Add(L.T("key.alt"));
            if ((mods & MOD_SHIFT) != 0) parts.Add(L.T("key.shift"));
            if ((mods & MOD_WIN) != 0) parts.Add(L.T("key.win"));
            if (key != Keys.None) parts.Add(KeyName(key));
            return string.Join(" + ", parts);
        }

        static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return L.T("key.numpad") + " " + (k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Space: return L.T("key.space");
                case Keys.Return: return L.T("key.enter");
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                case Keys.Oemplus: return "+";
                case Keys.OemMinus: return "-";
                case Keys.Multiply: return L.T("key.numpad") + " *";
                case Keys.Add: return L.T("key.numpad") + " +";
                case Keys.Subtract: return L.T("key.numpad") + " -";
                case Keys.Divide: return L.T("key.numpad") + " /";
                case Keys.Scroll: return L.T("key.scroll");
                case Keys.Insert: return L.T("key.insert");
                case Keys.Home: return L.T("key.home");
                case Keys.End: return L.T("key.end");
                case Keys.PageUp: return L.T("key.pgup");
                case Keys.PageDown: return L.T("key.pgdn");
            }
            return k.ToString();
        }
    }
}
