using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Mimi
{
    /// <summary>
    /// Sending pictures over UDP without a server.
    ///
    /// The picture is resized and compressed (PNG if it has transparency, JPEG otherwise), then cut into
    /// small chunks that each fit in one encrypted packet:
    ///   IMG    "id|chunkCount|byteCount|caption"   (header)
    ///   IMGP   "id|index|base64 data"               (one chunk)
    ///   IMGN   "id|3,17,42"                         (receiver: "I'm missing these chunks")
    ///   IMGA   "id"                                 (receiver: "got it all")
    /// Lost chunks are simply asked for again, a few times, until the picture is complete.
    /// </summary>
    static class ImageCodec
    {
        public const int MaxBytes = 450 * 1024;     // after compression
        public const int MaxSide = 1280;            // pixels, longest side
        public const int MaxDecodeSide = 4096;

        /// <summary>Loads, resizes and compresses a picture for sending. Throws if it can't be read.</summary>
        public static byte[] Prepare(Image src)
        {
            int side = MaxSide;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float k = Math.Min(1f, side / (float)Math.Max(src.Width, src.Height));
                int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(src, 0, 0, w, h);
                    }
                    byte[] data = HasTransparency(bmp) ? Png(bmp) : Jpeg(bmp, attempt < 2 ? 88L : 75L);
                    if (data.Length <= MaxBytes) return data;
                }
                side = (int)(side * 0.75);
            }
            throw new InvalidDataException("image too large");
        }

        public static byte[] Prepare(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var img = Image.FromStream(fs))
                return Prepare(img);
        }

        /// <summary>Decodes a received picture, with sanity limits.</summary>
        public static Bitmap Decode(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaxBytes * 2) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms, false, true))
                {
                    if (img.Width > MaxDecodeSide || img.Height > MaxDecodeSide) return null;
                    var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, img.Width, img.Height);
                    return bmp;
                }
            }
            catch { return null; }
        }

        static bool HasTransparency(Bitmap b)
        {
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < b.Height; y++)
                    {
                        uint* row = (uint*)((byte*)d.Scan0 + y * d.Stride);
                        for (int x = 0; x < b.Width; x++)
                            if ((row[x] >> 24) < 250) return true;
                    }
                }
                return false;
            }
            finally { b.UnlockBits(d); }
        }

        static byte[] Png(Bitmap b)
        {
            using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }

        static byte[] Jpeg(Bitmap b, long quality)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var ep = new EncoderParameters(1))
            using (var ms = new MemoryStream())
            {
                ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                b.Save(ms, codec, ep);
                return ms.ToArray();
            }
        }
    }

    /// <summary>Cuts a picture into chunks and answers requests for missing ones.</summary>
    sealed class OutgoingImage
    {
        public const int ChunkSize = 800; // raw bytes per packet (≈1.2 KB once encoded and encrypted)

        public readonly string Id;
        public readonly byte[] Data;
        public readonly string Caption;
        public readonly int Count;
        public readonly DateTime Created = DateTime.UtcNow;

        public OutgoingImage(byte[] data, string caption)
        {
            Id = Crypto.Hex(Crypto.RandomBytes(4));
            Data = data;
            Caption = caption ?? "";
            Count = (data.Length + ChunkSize - 1) / ChunkSize;
        }

        public string Header => Id + "|" + Count + "|" + Data.Length + "|" + Caption;

        public string Chunk(int i)
        {
            int off = i * ChunkSize, len = Math.Min(ChunkSize, Data.Length - off);
            return Id + "|" + i + "|" + Convert.ToBase64String(Data, off, len);
        }
    }

    /// <summary>Puts a received picture back together.</summary>
    sealed class IncomingImage
    {
        public readonly string Id, Caption, From;
        public readonly int Count, Size;
        readonly byte[][] chunks;
        int received;
        public DateTime LastActivity = DateTime.UtcNow;
        public int NackRounds;

        IncomingImage(string id, int count, int size, string caption, string from)
        {
            Id = id; Count = count; Size = size; Caption = caption; From = from;
            chunks = new byte[count][];
        }

        /// <summary>Parses an IMG header; null if invalid.</summary>
        public static IncomingImage FromHeader(string body, string from)
        {
            var f = body.Split(new[] { '|' }, 4);
            if (f.Length < 3 || !int.TryParse(f[1], out int count) || !int.TryParse(f[2], out int size)) return null;
            if (count <= 0 || size <= 0 || size > ImageCodec.MaxBytes * 2 || count != (size + OutgoingImage.ChunkSize - 1) / OutgoingImage.ChunkSize) return null;
            string caption = f.Length > 3 ? f[3] : "";
            if (caption.Length > 160) caption = caption.Substring(0, 160);
            return new IncomingImage(f[0], count, size, caption, from);
        }

        public static string IdOf(string body)
        {
            int i = body.IndexOf('|');
            return i > 0 ? body.Substring(0, i) : body;
        }

        public bool Complete => received == Count;

        /// <summary>Adds an IMGP chunk. Returns true when the picture becomes complete.</summary>
        public bool Add(string body)
        {
            var f = body.Split(new[] { '|' }, 3);
            if (f.Length != 3 || f[0] != Id || !int.TryParse(f[1], out int i) || i < 0 || i >= Count || chunks[i] != null) return false;
            byte[] data;
            try { data = Convert.FromBase64String(f[2]); } catch { return false; }
            int expected = i == Count - 1 ? Size - i * OutgoingImage.ChunkSize : OutgoingImage.ChunkSize;
            if (data.Length != expected) return false;
            chunks[i] = data;
            received++;
            LastActivity = DateTime.UtcNow;
            return Complete;
        }

        public IEnumerable<int> Missing() => Enumerable.Range(0, Count).Where(i => chunks[i] == null);

        public byte[] Assemble()
        {
            var all = new byte[Size];
            for (int i = 0; i < Count; i++) Buffer.BlockCopy(chunks[i], 0, all, i * OutgoingImage.ChunkSize, chunks[i].Length);
            return all;
        }
    }
}
