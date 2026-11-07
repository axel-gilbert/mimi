using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Windows.Forms;

#pragma warning disable 0649 // fields filled in by the JSON serializer

namespace Mimi
{
    /// <summary>
    /// Mises à jour automatiques via les Releases GitHub.
    ///
    /// 1. On lit la liste des releases publiques du dépôt.
    /// 2. On prend la plus haute version qui a un fichier "*-win-x64.exe".
    /// 3. Si elle est plus récente : téléchargement dans %TEMP%, vérification
    ///    (taille, empreinte SHA-256 fournie par GitHub, en-tête d'exécutable).
    /// 4. Un exe Windows en cours d'exécution ne peut pas être écrasé, mais il peut être renommé :
    ///    Mimi.exe → Mimi.exe.old, le nouveau prend sa place, on relance, l'ancien est supprimé au démarrage suivant.
    /// </summary>
    static class Updater
    {
        public const string Repo = "axel-gilbert/mimi";
        const string Api = "https://api.github.com/repos/" + Repo + "/releases?per_page=20";
        const string AssetSuffix = "-win-x64.exe";

        public sealed class UpdateInfo
        {
            public Version Version;
            public string Tag, Url, AssetName, Sha256, PageUrl;
            public long Size;
        }

        [DataContract]
        sealed class GhRelease
        {
            [DataMember(Name = "tag_name")] public string Tag;
            [DataMember(Name = "draft")] public bool Draft;
            [DataMember(Name = "html_url")] public string Html;
            [DataMember(Name = "assets")] public GhAsset[] Assets;
        }

        [DataContract]
        sealed class GhAsset
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "browser_download_url")] public string Url;
            [DataMember(Name = "size")] public long Size;
            [DataMember(Name = "digest")] public string Digest;
        }

        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string CurrentText => Current.ToString(3);

        static Updater()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mimi-updater/" + CurrentText;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            return req;
        }

        static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string t = tag.TrimStart('v', 'V');
            int dash = t.IndexOf('-');
            if (dash >= 0) t = t.Substring(0, dash);
            if (!Version.TryParse(t, out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        /// <summary>Renvoie la mise à jour disponible, ou null si on est à jour. Appel bloquant (thread de fond).</summary>
        public static UpdateInfo Check()
        {
            GhRelease[] releases;
            using (var resp = Request(Api).GetResponse())
            using (var s = resp.GetResponseStream())
                releases = (GhRelease[])new DataContractJsonSerializer(typeof(GhRelease[])).ReadObject(s);

            UpdateInfo best = null;
            foreach (var r in releases ?? new GhRelease[0])
            {
                if (r.Draft) continue;
                var v = ParseTag(r.Tag);
                if (v == null) continue;
                var asset = r.Assets?.FirstOrDefault(a => a.Name != null && a.Name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase));
                if (asset == null) continue;
                if (best != null && v <= best.Version) continue;
                string sha = null;
                if (asset.Digest != null && asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = asset.Digest.Substring(7);
                best = new UpdateInfo
                {
                    Version = v, Tag = r.Tag, Url = asset.Url, AssetName = asset.Name,
                    Size = asset.Size, Sha256 = sha, PageUrl = r.Html
                };
            }
            return best != null && best.Version > Current ? best : null;
        }

        /// <summary>Télécharge et vérifie la mise à jour. Renvoie le chemin du fichier téléchargé.</summary>
        public static string Download(UpdateInfo u)
        {
            if (!u.Url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Adresse de téléchargement inattendue");
            var dir = Path.Combine(Path.GetTempPath(), "Mimi-maj");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "Mimi-" + u.Version.ToString(3) + ".exe");
            var tmp = path + ".part";

            var req = Request(u.Url);
            req.Accept = "application/octet-stream";
            using (var resp = req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var f = File.Create(tmp))
                s.CopyTo(f);

            var bytes = File.ReadAllBytes(tmp);
            if (u.Size > 0 && bytes.Length != u.Size) throw new InvalidDataException("Taille incorrecte");
            if (bytes.Length < 1024 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("Ce n'est pas un exécutable");
            if (u.Sha256 != null)
            {
                using (var sha = SHA256.Create())
                {
                    var hex = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                    if (!hex.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Empreinte SHA-256 incorrecte");
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        /// <summary>Remplace l'exe actuel par le nouveau et lance la nouvelle version. L'appelant doit ensuite quitter.</summary>
        public static void ApplyAndRestart(string newExe)
        {
            string cur = Application.ExecutablePath;
            string old = cur + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(cur, old);
            try
            {
                File.Copy(newExe, cur, true);
            }
            catch
            {
                File.Move(old, cur); // retour arrière
                throw;
            }
            try { File.Delete(newExe); } catch { }

            string args = "--updated";
            if (Program.Profile != "default") args += " --profile " + Program.Profile;
            if (Program.Portable) args += " --portable";
            Process.Start(new ProcessStartInfo(cur, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(cur) });
        }

        /// <summary>Supprime l'ancienne version laissée par une mise à jour.</summary>
        public static void CleanupOld()
        {
            string old = Application.ExecutablePath + ".old";
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { System.Threading.Thread.Sleep(300); }
            }
        }
    }
}
