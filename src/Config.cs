using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>User settings, stored in %APPDATA%\Mimi\&lt;profile&gt;\config.ini</summary>
    sealed class Config
    {
        public const int DefaultPort = 47777;

        public string Name = Environment.UserName;
        public string Peer = "";          // partner address, "ip" or "ip:port" (filled in by pairing)
        public string PeerName = "";
        public int Port = DefaultPort;    // local UDP port
        public byte[] Key;                // couple's 32-byte master key (stored encrypted with DPAPI)
        public string PairedSince = "";   // yyyy-MM-dd
        public int HotMods = GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT;
        public int HotKey = (int)Keys.H;  // send a heart (hold = big heart)
        public int MsgMods = GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT;
        public int MsgKey = (int)Keys.M;  // open the message bar
        public int Volume = 70;
        public bool Sound = true;
        public bool Echo = true;          // also show my own hearts when I send them
        public int Corner = 0;            // 0 bottom right, 1 bottom left, 2 bottom center
        public bool AutoUpdate = true;    // automatic updates from GitHub Releases
        public string Lang = L.Detect();  // follows Windows on first launch (see lang/*.ini)
        public bool Exists;               // false on first launch
        /// <summary>
        /// Pairing code of a couple paired with version 0.1/0.2. It is kept so that those pairings keep
        /// working: the key is derived from it, and the old protocol is still spoken until the partner
        /// has updated too. Cleared as soon as the partner speaks the new protocol.
        /// </summary>
        public string LegacyCode = "";
        /// <summary>True when the key was derived from a 0.1/0.2 pairing code (works, but pairing again is stronger).</summary>
        public bool WeakKey;
        /// <summary>"Do not disturb" until this UTC time (DateTime.MaxValue = until turned off).</summary>
        public DateTime DndUntil = DateTime.MinValue;
        /// <summary>Optional GIPHY API key (official releases already embed one).</summary>
        public string GiphyKey = "";
        /// <summary>My hearts' look: "preset|style" or "custom-RRGGBB|style" (see Art.Presets / Art.Styles).</summary>
        public string Theme = "classic|glossy";
        /// <summary>Show my partner's hearts in their own colours.</summary>
        public bool PartnerColors = true;

        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mimi", Program.Profile);
        static string FilePath => Path.Combine(Dir, "config.ini");

        public bool IsPaired => Key != null && !string.IsNullOrWhiteSpace(Peer);

        public Config Clone() => (Config)MemberwiseClone();

        /// <summary>Key used for couples paired with 0.1/0.2: derived from their shared pairing code.</summary>
        public static byte[] LegacyKey(string code) =>
            Crypto.Hmac(Encoding.UTF8.GetBytes("mimi legacy v2 upgrade"), Encoding.UTF8.GetBytes(code.Trim()));

        public static Config Load()
        {
            var c = new Config();
            if (Program.Profile != "default")
            {
                // Test profile on a single PC: other port, other shortcuts.
                c.Port = DefaultPort + 1;
                c.HotMods = c.MsgMods = GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT | GlobalHotkey.MOD_SHIFT;
                c.Name += " (" + Program.Profile + ")";
            }
            try
            {
                if (!File.Exists(FilePath)) return c;
                c.Exists = true;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0 && !line.StartsWith("#")) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                string s; int n; bool b;
                if (d.TryGetValue("Name", out s)) c.Name = s;
                if (d.TryGetValue("Peer", out s)) c.Peer = s;
                if (d.TryGetValue("PeerName", out s)) c.PeerName = s;
                if (d.TryGetValue("Key", out s)) c.Key = Crypto.Unprotect(s);
                if (d.TryGetValue("PairedSince", out s)) c.PairedSince = s;
                if (d.TryGetValue("Port", out s) && int.TryParse(s, out n) && n > 0 && n < 65536) c.Port = n;
                if (d.TryGetValue("HotMods", out s) && int.TryParse(s, out n)) c.HotMods = n;
                if (d.TryGetValue("HotKey", out s) && int.TryParse(s, out n)) c.HotKey = n;
                if (d.TryGetValue("MsgMods", out s) && int.TryParse(s, out n)) c.MsgMods = n;
                if (d.TryGetValue("MsgKey", out s) && int.TryParse(s, out n)) c.MsgKey = n;
                if (d.TryGetValue("Volume", out s) && int.TryParse(s, out n)) c.Volume = Math.Max(0, Math.Min(100, n));
                if (d.TryGetValue("Sound", out s) && bool.TryParse(s, out b)) c.Sound = b;
                if (d.TryGetValue("Echo", out s) && bool.TryParse(s, out b)) c.Echo = b;
                if (d.TryGetValue("Corner", out s) && int.TryParse(s, out n) && n >= 0 && n <= 2) c.Corner = n;
                if (d.TryGetValue("AutoUpdate", out s) && bool.TryParse(s, out b)) c.AutoUpdate = b;
                if (d.TryGetValue("Lang", out s) && L.Has(s)) c.Lang = s;
                if (d.TryGetValue("WeakKey", out s) && bool.TryParse(s, out b)) c.WeakKey = b;
                if (d.TryGetValue("GiphyKey", out s)) c.GiphyKey = s;
                if (d.TryGetValue("Theme", out s)) c.Theme = Art.NormalizeTheme(s);
                if (d.TryGetValue("PartnerColors", out s) && bool.TryParse(s, out b)) c.PartnerColors = b;
                if (d.TryGetValue("DndUntil", out s) && long.TryParse(s, out long ticks) && ticks >= 0 && ticks <= DateTime.MaxValue.Ticks)
                    c.DndUntil = new DateTime(ticks, DateTimeKind.Utc);

                if (c.Key != null && c.Key.Length != 32) c.Key = null;

                // Paired with 0.1/0.2 (the code itself was the secret): keep that pairing working.
                if (d.TryGetValue("PairCode", out s) && s.Length > 0)
                {
                    c.LegacyCode = s;
                    if (c.Key == null) { c.Key = LegacyKey(s); c.WeakKey = true; }
                }
            }
            catch { /* fall back to defaults */ }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("# Mimi settings");
                sb.AppendLine("Name=" + Name);
                sb.AppendLine("Peer=" + Peer);
                sb.AppendLine("PeerName=" + PeerName);
                sb.AppendLine("Key=" + (Key != null ? Crypto.Protect(Key) : ""));
                sb.AppendLine("PairedSince=" + PairedSince);
                if (LegacyCode.Length > 0) sb.AppendLine("PairCode=" + LegacyCode);
                sb.AppendLine("Port=" + Port);
                sb.AppendLine("HotMods=" + HotMods);
                sb.AppendLine("HotKey=" + HotKey);
                sb.AppendLine("MsgMods=" + MsgMods);
                sb.AppendLine("MsgKey=" + MsgKey);
                sb.AppendLine("Volume=" + Volume);
                sb.AppendLine("Sound=" + Sound);
                sb.AppendLine("Echo=" + Echo);
                sb.AppendLine("Corner=" + Corner);
                sb.AppendLine("AutoUpdate=" + AutoUpdate);
                sb.AppendLine("Lang=" + Lang);
                sb.AppendLine("WeakKey=" + WeakKey);
                sb.AppendLine("GiphyKey=" + GiphyKey);
                sb.AppendLine("Theme=" + Theme);
                sb.AppendLine("PartnerColors=" + PartnerColors);
                sb.AppendLine("DndUntil=" + DndUntil.Ticks);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                Exists = true;
            }
            catch { }
        }
    }
}
