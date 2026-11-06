using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>Raccourci clavier global (fonctionne même quand Mimi n'a pas le focus).</summary>
    sealed class GlobalHotkey : NativeWindow, IDisposable
    {
        public const int MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8;
        const int Id = 0x4D31;
        bool registered;

        public event Action Pressed;

        public GlobalHotkey()
        {
            // fenêtre "message-only", invisible
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
            if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & MOD_SHIFT) != 0) parts.Add("Maj");
            if ((mods & MOD_WIN) != 0) parts.Add("Win");
            if (key != Keys.None) parts.Add(KeyName(key));
            return string.Join(" + ", parts);
        }

        static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Pavé " + (k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Space: return "Espace";
                case Keys.Return: return "Entrée";
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                case Keys.Oemplus: return "+";
                case Keys.OemMinus: return "-";
                case Keys.Multiply: return "Pavé *";
                case Keys.Add: return "Pavé +";
                case Keys.Subtract: return "Pavé -";
                case Keys.Divide: return "Pavé /";
                case Keys.Scroll: return "Arrêt défil";
                case Keys.Insert: return "Inser";
                case Keys.Home: return "Début";
                case Keys.End: return "Fin";
                case Keys.PageUp: return "Pg préc";
                case Keys.PageDown: return "Pg suiv";
            }
            return k.ToString();
        }
    }
}
