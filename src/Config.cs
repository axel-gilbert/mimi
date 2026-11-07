using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>Réglages, stockés dans %APPDATA%\Mimi\&lt;profil&gt;\config.ini</summary>
    sealed class Config
    {
        public const int DefaultPort = 47777;

        public string Name = Environment.UserName;
        public string Peer = "";          // "ip" ou "ip:port" de ta moitié (rempli par l'appairage)
        public string PeerName = "";
        public int Port = DefaultPort;    // port UDP local
        public string PairCode = "";      // secret partagé (le code d'appairage)
        public int HotMods = GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT;
        public int HotKey = (int)Keys.H;
        public int Volume = 70;
        public bool Sound = true;
        public bool Echo = true;          // voir aussi ses propres cœurs envoyés
        public int Corner = 0;            // 0 bas droite, 1 bas gauche, 2 bas centre
        public bool AutoUpdate = true;    // mises à jour automatiques via GitHub Releases
        public string Lang = "en";        // "en" ou "fr"
        public bool Exists;               // fichier déjà présent (sinon premier lancement)

        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mimi", Program.Profile);
        static string FilePath => Path.Combine(Dir, "config.ini");

        public bool IsPaired => !string.IsNullOrWhiteSpace(Peer) && !string.IsNullOrWhiteSpace(PairCode);

        public Config Clone() => (Config)MemberwiseClone();

        public static Config Load()
        {
            var c = new Config();
            if (Program.Profile != "default")
            {
                // Profil de test sur un seul PC : autre port, autre raccourci.
                c.Port = DefaultPort + 1;
                c.HotKey = (int)Keys.J;
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
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                string s; int n; bool b;
                if (d.TryGetValue("Name", out s)) c.Name = s;
                if (d.TryGetValue("Peer", out s)) c.Peer = s;
                if (d.TryGetValue("PeerName", out s)) c.PeerName = s;
                if (d.TryGetValue("PairCode", out s)) c.PairCode = s;
                if (d.TryGetValue("Port", out s) && int.TryParse(s, out n) && n > 0 && n < 65536) c.Port = n;
                if (d.TryGetValue("HotMods", out s) && int.TryParse(s, out n)) c.HotMods = n;
                if (d.TryGetValue("HotKey", out s) && int.TryParse(s, out n)) c.HotKey = n;
                if (d.TryGetValue("Volume", out s) && int.TryParse(s, out n)) c.Volume = Math.Max(0, Math.Min(100, n));
                if (d.TryGetValue("Sound", out s) && bool.TryParse(s, out b)) c.Sound = b;
                if (d.TryGetValue("Echo", out s) && bool.TryParse(s, out b)) c.Echo = b;
                if (d.TryGetValue("Corner", out s) && int.TryParse(s, out n) && n >= 0 && n <= 2) c.Corner = n;
                if (d.TryGetValue("AutoUpdate", out s) && bool.TryParse(s, out b)) c.AutoUpdate = b;
                if (d.TryGetValue("Lang", out s) && (s == "en" || s == "fr")) c.Lang = s;
            }
            catch { /* réglages par défaut */ }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("# Mimi - réglages");
                sb.AppendLine("Name=" + Name);
                sb.AppendLine("Peer=" + Peer);
                sb.AppendLine("PeerName=" + PeerName);
                sb.AppendLine("PairCode=" + PairCode);
                sb.AppendLine("Port=" + Port);
                sb.AppendLine("HotMods=" + HotMods);
                sb.AppendLine("HotKey=" + HotKey);
                sb.AppendLine("Volume=" + Volume);
                sb.AppendLine("Sound=" + Sound);
                sb.AppendLine("Echo=" + Echo);
                sb.AppendLine("Corner=" + Corner);
                sb.AppendLine("AutoUpdate=" + AutoUpdate);
                sb.AppendLine("Lang=" + Lang);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                Exists = true;
            }
            catch { }
        }
    }
}
