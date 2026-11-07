using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

#pragma warning disable 0649 // fields filled in by the JSON serializer

namespace Mimi
{
    /// <summary>
    /// GIF search with GIPHY (free API key from developers.giphy.com).
    ///
    /// Only the GIF's GIPHY id travels between the two PCs (in an encrypted message); the receiver downloads
    /// the animation itself from media.giphy.com. The id is strictly validated, so a message can never make
    /// Mimi download anything else.
    ///
    /// The key comes from the settings, or from the build (official releases embed it from the
    /// GIPHY_API_KEY repository secret, see .github/workflows/release.yml).
    /// </summary>
    static class Giphy
    {
        static readonly Regex IdPattern = new Regex("^[A-Za-z0-9]{5,40}$");

        public sealed class Gif
        {
            public string Id, Title, PreviewUrl;
        }

        [DataContract] sealed class Response { [DataMember(Name = "data")] public Item[] Data; }
        [DataContract] sealed class Item
        {
            [DataMember(Name = "id")] public string Id;
            [DataMember(Name = "title")] public string Title;
            [DataMember(Name = "images")] public Images Images;
        }
        [DataContract] sealed class Images { [DataMember(Name = "fixed_height_small")] public Rendition Small; }
        [DataContract] sealed class Rendition { [DataMember(Name = "url")] public string Url; }

        /// <summary>Key from the settings, else the one embedded at build time, else null.</summary>
        public static string ApiKey(Config cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.GiphyKey)) return cfg.GiphyKey.Trim();
            var meta = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "GiphyApiKey");
            return string.IsNullOrWhiteSpace(meta?.Value) ? null : meta.Value;
        }

        public static bool IsValidId(string id) => id != null && IdPattern.IsMatch(id);

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "Mimi/" + Updater.CurrentText;
            return wc;
        }

        /// <summary>Searches GIFs (or trending ones when the query is empty). Blocking: call from a background thread.</summary>
        public static List<Gif> Search(string apiKey, string query, string lang)
        {
            string url = string.IsNullOrWhiteSpace(query)
                ? "https://api.giphy.com/v1/gifs/trending?limit=24&rating=pg-13&api_key=" + Uri.EscapeDataString(apiKey)
                : "https://api.giphy.com/v1/gifs/search?limit=24&rating=pg-13&lang=" + Uri.EscapeDataString(lang) +
                  "&q=" + Uri.EscapeDataString(query.Trim()) + "&api_key=" + Uri.EscapeDataString(apiKey);
            using (var wc = Client())
            using (var ms = new MemoryStream(wc.DownloadData(url)))
            {
                var r = (Response)new DataContractJsonSerializer(typeof(Response)).ReadObject(ms);
                return (r.Data ?? new Item[0])
                    .Where(i => IsValidId(i.Id) && i.Images?.Small?.Url != null && i.Images.Small.Url.StartsWith("https://"))
                    .Select(i => new Gif { Id = i.Id, Title = i.Title, PreviewUrl = i.Images.Small.Url })
                    .ToList();
            }
        }

        /// <summary>Downloads a small preview image (for the picker). Blocking.</summary>
        public static byte[] Download(string url)
        {
            var u = new Uri(url);
            if (u.Scheme != "https" || !(u.Host.EndsWith(".giphy.com") || u.Host == "giphy.com")) throw new InvalidOperationException("unexpected host");
            using (var wc = Client()) return wc.DownloadData(u);
        }

        /// <summary>Downloads the full animation of a GIF id from media.giphy.com. Blocking.</summary>
        public static byte[] DownloadById(string id)
        {
            if (!IsValidId(id)) throw new ArgumentException("invalid GIF id");
            var data = Download("https://media.giphy.com/media/" + id + "/giphy.gif");
            if (data.Length > 12 * 1024 * 1024) throw new InvalidDataException("GIF too large");
            return data;
        }

        /// <summary>A decoded animation: frames (premultiplied ARGB) and their durations in ms.</summary>
        public sealed class Animation : IDisposable
        {
            public Bitmap[] Frames;
            public int[] Delays;
            public int TotalMs => Delays.Sum();
            public void Dispose() { foreach (var f in Frames) f.Dispose(); }
        }

        /// <summary>Decodes an animated GIF (at most 80 frames, sides ≤ 1000 px). Returns null if invalid.</summary>
        public static Animation Decode(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms, false, true))
                {
                    if (img.Width > 2000 || img.Height > 2000) return null;
                    float k = Math.Min(1f, 1000f / Math.Max(img.Width, img.Height));
                    int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
                    int count = 1;
                    try { count = img.GetFrameCount(FrameDimension.Time); } catch { }
                    int[] delays = new int[count];
                    try
                    {
                        var prop = img.GetPropertyItem(0x5100); // frame delays, 1/100 s, 4 bytes each
                        for (int i = 0; i < count && i * 4 + 3 < prop.Value.Length; i++)
                            delays[i] = BitConverter.ToInt32(prop.Value, i * 4) * 10;
                    }
                    catch { }
                    int step = Math.Max(1, (int)Math.Ceiling(count / 80.0));
                    var frames = new List<Bitmap>();
                    var times = new List<int>();
                    for (int i = 0; i < count; i += step)
                    {
                        if (count > 1) img.SelectActiveFrame(FrameDimension.Time, i);
                        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.DrawImage(img, 0, 0, w, h);
                        }
                        frames.Add(bmp);
                        int d = 0;
                        for (int j = i; j < Math.Min(count, i + step); j++) d += delays[j] < 20 ? 100 : delays[j];
                        times.Add(d);
                    }
                    return new Animation { Frames = frames.ToArray(), Delays = times.ToArray() };
                }
            }
            catch { return null; }
        }
    }
}
