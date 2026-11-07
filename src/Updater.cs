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
    /// Automatic updates via GitHub Releases.
    ///
    /// 1. Read the list of public releases of the repository.
    /// 2. Pick the highest version that has a "*-win-x64.exe" file.
    /// 3. If it is newer: download to %TEMP%, then verify
    ///    (size, SHA-256 digest provided by GitHub, executable header).
    /// 4. A running Windows exe cannot be overwritten, but it can be renamed:
    ///    Mimi.exe → Mimi.exe.old, the new one takes its place, we restart, and the old one is deleted on the next startup.
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

        internal static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string t = tag.TrimStart('v', 'V');
            int dash = t.IndexOf('-');
            if (dash >= 0) t = t.Substring(0, dash);
            if (!Version.TryParse(t, out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        /// <summary>Returns the available update, or null if up to date. Blocking call (background thread).</summary>
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

        /// <summary>Downloads and verifies the update. Returns the path of the downloaded file.</summary>
        public static string Download(UpdateInfo u)
        {
            if (!u.Url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected download URL");
            var dir = Path.Combine(Path.GetTempPath(), "Mimi-update");
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
            if (u.Size > 0 && bytes.Length != u.Size) throw new InvalidDataException("Incorrect size");
            if (bytes.Length < 1024 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("Not an executable");
            if (u.Sha256 != null)
            {
                using (var sha = SHA256.Create())
                {
                    var hex = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                    if (!hex.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 digest mismatch");
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        /// <summary>Replaces the current exe with the new one and starts the new version. The caller must then exit.</summary>
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
                File.Move(old, cur); // roll back
                throw;
            }
            try { File.Delete(newExe); } catch { }

            string args = "--updated";
            if (Program.Profile != "default") args += " --profile " + Program.Profile;
            if (Program.Portable) args += " --portable";
            // ShellExecute: the new version inherits nothing from this one (no socket, no file handle).
            Process.Start(new ProcessStartInfo(cur, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(cur) });
        }

        /// <summary>Deletes the old version left behind by an update.</summary>
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
