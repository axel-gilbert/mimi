using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Mimi
{
    /// <summary>
    /// Text rendering with DirectWrite + Direct2D, the modern Windows text engine: colour emoji 💗😘🇫🇷,
    /// proper fallback fonts for every script (accents, Arabic, CJK…) and transparent backgrounds.
    /// GDI+ (Graphics.DrawString) can't do colour emoji and often shows empty boxes for them.
    ///
    /// The text is drawn into a small premultiplied-alpha bitmap (cached), which is then drawn with GDI+.
    /// If DirectWrite isn't available, <see cref="Txt"/> falls back to GDI+.
    /// COM objects are used through their vtables directly, so no extra library is needed.
    /// </summary>
    static class ColorText
    {
        [StructLayout(LayoutKind.Sequential)] struct ColorF { public float R, G, B, A; }
        [StructLayout(LayoutKind.Sequential)] struct PointF2 { public float X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct RenderTargetProperties
        {
            public int Type, Format, AlphaMode;
            public float DpiX, DpiY;
            public int Usage, MinLevel;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct TextMetrics
        {
            public float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight;
            public uint MaxBidiReorderingDepth, LineCount;
        }

        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr options, out IntPtr factory);
        [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, ref Guid riid, out IntPtr factory);

        // vtable signatures (x64: one calling convention; the first argument is the COM "this")
        delegate uint ReleaseFn(IntPtr self);
        delegate int CreateDCRenderTargetFn(IntPtr self, ref RenderTargetProperties props, out IntPtr rt);
        delegate int CreateTextFormatFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string family, IntPtr collection,
            int weight, int style, int stretch, float size, [MarshalAs(UnmanagedType.LPWStr)] string locale, out IntPtr format);
        delegate int CreateTextLayoutFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text, int length, IntPtr format,
            float maxWidth, float maxHeight, out IntPtr layout);
        delegate int SetIntFn(IntPtr self, int value);
        delegate int GetMetricsFn(IntPtr self, out TextMetrics metrics);
        delegate int BindDCFn(IntPtr self, IntPtr hdc, ref Native.RECT rect);
        delegate void VoidFn(IntPtr self);
        delegate int EndDrawFn(IntPtr self, out ulong tag1, out ulong tag2);
        delegate void ClearFn(IntPtr self, ref ColorF color);
        delegate int CreateBrushFn(IntPtr self, ref ColorF color, IntPtr props, out IntPtr brush);
        delegate void DrawTextLayoutFn(IntPtr self, PointF2 origin, IntPtr layout, IntPtr brush, int options);
        delegate void SetModeFn(IntPtr self, int mode);

        // vtable indices
        const int Release = 2;
        const int Factory_CreateDCRenderTarget = 16;
        const int DWrite_CreateTextFormat = 15, DWrite_CreateTextLayout = 18;
        const int Format_SetTextAlignment = 3, Format_SetWordWrapping = 5, Layout_GetMetrics = 60;
        const int RT_CreateSolidColorBrush = 8, RT_DrawTextLayout = 28, RT_SetTextAntialiasMode = 34,
                  RT_Clear = 47, RT_BeginDraw = 48, RT_EndDraw = 49, DCRT_BindDC = 57;

        static IntPtr d2d, dwrite;
        static bool? available;
        static readonly object gate = new object();

        static T Fn<T>(IntPtr com, int index) where T : class =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), index * IntPtr.Size));

        static void Rel(ref IntPtr com)
        {
            if (com != IntPtr.Zero) { Fn<ReleaseFn>(com, Release)(com); com = IntPtr.Zero; }
        }

        public static bool Available
        {
            get
            {
                lock (gate)
                {
                    if (available.HasValue) return available.Value;
                    try
                    {
                        var iidD2d = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
                        var iidDw = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
                        available = D2D1CreateFactory(0, ref iidD2d, IntPtr.Zero, out d2d) == 0 &&
                                    DWriteCreateFactory(0, ref iidDw, out dwrite) == 0;
                    }
                    catch { available = false; }
                    if (Environment.GetEnvironmentVariable("MIMI_GDI_TEXT") == "1") available = false;
                    return available.Value;
                }
            }
        }

        static IntPtr Layout(string text, string family, int weight, float sizePx, float maxWidth, bool wrap, out IntPtr format)
        {
            format = IntPtr.Zero;
            if (Fn<CreateTextFormatFn>(dwrite, DWrite_CreateTextFormat)(dwrite, family, IntPtr.Zero, weight, 0, 5, sizePx, "en-us", out format) != 0) return IntPtr.Zero;
            Fn<SetIntFn>(format, Format_SetWordWrapping)(format, wrap ? 0 : 1);
            if (Fn<CreateTextLayoutFn>(dwrite, DWrite_CreateTextLayout)(dwrite, text, text.Length, format, Math.Max(1, maxWidth), 100000, out IntPtr layout) != 0)
                return IntPtr.Zero;
            return layout;
        }

        /// <summary>Size of the text in pixels (width, height).</summary>
        public static SizeF Measure(string text, string family, int weight, float sizePx, float maxWidth, bool wrap)
        {
            lock (gate)
            {
                IntPtr format = IntPtr.Zero, layout = IntPtr.Zero;
                try
                {
                    layout = Layout(text, family, weight, sizePx, maxWidth, wrap, out format);
                    if (layout == IntPtr.Zero) return SizeF.Empty;
                    Fn<GetMetricsFn>(layout, Layout_GetMetrics)(layout, out var m);
                    return new SizeF(m.WidthIncludingTrailingWhitespace, m.Height);
                }
                finally { Rel(ref layout); Rel(ref format); }
            }
        }

        /// <summary>Renders the text (transparent background, colour emoji). The bitmap has a 2 px margin.</summary>
        public static Bitmap Render(string text, string family, int weight, float sizePx, Color color, float maxWidth, bool wrap)
        {
            lock (gate)
            {
                IntPtr format = IntPtr.Zero, layout = IntPtr.Zero, rt = IntPtr.Zero, brush = IntPtr.Zero, dc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    layout = Layout(text, family, weight, sizePx, maxWidth, wrap, out format);
                    if (layout == IntPtr.Zero) return null;
                    Fn<GetMetricsFn>(layout, Layout_GetMetrics)(layout, out var m);
                    int w = (int)Math.Ceiling(Math.Max(m.WidthIncludingTrailingWhitespace, m.Width) + Math.Max(0, m.Left)) + 4;
                    int h = (int)Math.Ceiling(m.Height) + 4;
                    if (w <= 4 || h <= 4 || w > 8000 || h > 8000) return null;

                    var bi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                    IntPtr screen = Native.GetDC(IntPtr.Zero);
                    dc = Native.CreateCompatibleDC(screen);
                    dib = Native.CreateDIBSection(screen, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
                    Native.ReleaseDC(IntPtr.Zero, screen);
                    old = Native.SelectObject(dc, dib);

                    var props = new RenderTargetProperties { Type = 0, Format = 87 /* B8G8R8A8 */, AlphaMode = 1 /* premultiplied */, DpiX = 96, DpiY = 96 };
                    if (Fn<CreateDCRenderTargetFn>(d2d, Factory_CreateDCRenderTarget)(d2d, ref props, out rt) != 0) return null;
                    var rc = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
                    if (Fn<BindDCFn>(rt, DCRT_BindDC)(rt, dc, ref rc) != 0) return null;
                    Fn<SetModeFn>(rt, RT_SetTextAntialiasMode)(rt, 2); // grayscale: works on a transparent background
                    Fn<VoidFn>(rt, RT_BeginDraw)(rt);
                    var clear = new ColorF();
                    Fn<ClearFn>(rt, RT_Clear)(rt, ref clear);
                    var col = new ColorF { R = color.R / 255f, G = color.G / 255f, B = color.B / 255f, A = color.A / 255f };
                    Fn<CreateBrushFn>(rt, RT_CreateSolidColorBrush)(rt, ref col, IntPtr.Zero, out brush);
                    Fn<DrawTextLayoutFn>(rt, RT_DrawTextLayout)(rt, new PointF2 { X = 2, Y = 2 }, layout, brush, 4 /* ENABLE_COLOR_FONT */);
                    if (Fn<EndDrawFn>(rt, RT_EndDraw)(rt, out _, out _) != 0) return null;

                    var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(bits + y * w * 4, row, 0, row.Length);
                        Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                    }
                    bmp.UnlockBits(data);
                    return bmp;
                }
                catch { return null; }
                finally
                {
                    Rel(ref brush); Rel(ref rt); Rel(ref layout); Rel(ref format);
                    if (dc != IntPtr.Zero) { Native.SelectObject(dc, old); Native.DeleteDC(dc); }
                    if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                }
            }
        }
    }

    /// <summary>
    /// Drop-in replacements for Graphics.DrawString / MeasureString that use <see cref="ColorText"/>
    /// (colour emoji, every script) and fall back to GDI+ when needed. Rendered texts are cached.
    /// </summary>
    static class Txt
    {
        sealed class Entry { public Bitmap Bmp; public SizeF Size; }
        static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();

        static string Family(Font f)
        {
            string n = f.FontFamily.Name;
            foreach (var suffix in new[] { " Semibold", " Black", " Light", " Semilight", " Bold" })
                if (n.EndsWith(suffix)) return n.Substring(0, n.Length - suffix.Length);
            return n;
        }

        static int Weight(Font f)
        {
            string n = f.FontFamily.Name;
            if (n.EndsWith(" Black")) return 900;
            if (f.Bold || n.EndsWith(" Bold")) return 700;
            if (n.EndsWith(" Semibold")) return 600;
            if (n.EndsWith(" Light")) return 300;
            return 400;
        }

        static float Px(Graphics g, Font f) => f.Unit == GraphicsUnit.Pixel ? f.Size : f.SizeInPoints * g.DpiY / 72f;

        static Entry Get(Graphics g, string text, Font f, Color c, float maxWidth, bool wrap, bool render)
        {
            float px = Px(g, f);
            string key = text + "\u0001" + f.FontFamily.Name + "|" + px.ToString("0.0", CultureInfo.InvariantCulture) + "|" + (int)f.Style + "|" +
                         c.ToArgb() + "|" + (wrap ? (int)maxWidth : -1) + "|" + render;
            lock (cache)
            {
                if (cache.TryGetValue(key, out var e)) return e;
                if (cache.Count > 400)
                {
                    foreach (var v in cache.Values) v.Bmp?.Dispose();
                    cache.Clear();
                }
                e = new Entry
                {
                    Size = ColorText.Measure(text, Family(f), Weight(f), px, wrap ? maxWidth : 100000, wrap),
                    Bmp = render ? ColorText.Render(text, Family(f), Weight(f), px, c, wrap ? maxWidth : 100000, wrap) : null
                };
                cache[key] = e;
                return e;
            }
        }

        public static SizeF MeasureString(Graphics g, string text, Font f) => MeasureString(g, text, f, 0);

        public static SizeF MeasureString(Graphics g, string text, Font f, int width)
        {
            if (string.IsNullOrEmpty(text)) return g.MeasureString(" ", f) is var e ? new SizeF(0, e.Height) : SizeF.Empty;
            if (!ColorText.Available) return width > 0 ? g.MeasureString(text, f, width) : g.MeasureString(text, f);
            var s = Get(g, text, f, Color.Black, width, width > 0, false).Size;
            return s.IsEmpty ? (width > 0 ? g.MeasureString(text, f, width) : g.MeasureString(text, f)) : new SizeF(s.Width + 2, s.Height);
        }

        public static void DrawString(Graphics g, string text, Font f, Brush b, float x, float y) =>
            DrawString(g, text, f, b, new RectangleF(x, y, 0, 0), null);

        public static void DrawString(Graphics g, string text, Font f, Brush b, RectangleF r) =>
            DrawString(g, text, f, b, r, null);

        public static void DrawString(Graphics g, string text, Font f, Brush b, RectangleF r, StringFormat fmt)
        {
            if (string.IsNullOrEmpty(text)) return;
            var color = (b as SolidBrush)?.Color ?? Color.Black;
            if (!ColorText.Available || color.A == 0)
            {
                if (r.Width <= 0) g.DrawString(text, f, b, r.X, r.Y, fmt ?? StringFormat.GenericDefault);
                else g.DrawString(text, f, b, r, fmt ?? StringFormat.GenericDefault);
                return;
            }
            bool sized = r.Width > 0;
            bool noWrap = !sized || (fmt != null && (fmt.FormatFlags & StringFormatFlags.NoWrap) != 0);
            bool ellipsis = fmt != null && fmt.Trimming != StringTrimming.None && fmt.Trimming != StringTrimming.Character;

            // shorten with "…" when it doesn't fit on one line
            if (sized && noWrap && ellipsis)
            {
                var full = Get(g, text, f, color, 0, false, false).Size;
                if (full.Width > r.Width) text = Ellipsize(g, text, f, color, r.Width);
            }

            var e = Get(g, text, f, color, sized && !noWrap ? r.Width : 0, sized && !noWrap, true);
            if (e.Bmp == null)
            {
                if (!sized) g.DrawString(text, f, b, r.X, r.Y, fmt ?? StringFormat.GenericDefault);
                else g.DrawString(text, f, b, r, fmt ?? StringFormat.GenericDefault);
                return;
            }
            float w = e.Size.Width, h = e.Size.Height;
            float x = r.X, y = r.Y;
            if (sized && fmt != null)
            {
                if (fmt.Alignment == StringAlignment.Center) x = r.X + (r.Width - w) / 2;
                else if (fmt.Alignment == StringAlignment.Far) x = r.Right - w;
                if (r.Height > 0)
                {
                    if (fmt.LineAlignment == StringAlignment.Center) y = r.Y + (r.Height - h) / 2;
                    else if (fmt.LineAlignment == StringAlignment.Far) y = r.Bottom - h;
                }
            }
            var mode = g.InterpolationMode;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(e.Bmp, (float)Math.Round(x - 2), (float)Math.Round(y - 2), e.Bmp.Width, e.Bmp.Height);
            g.InterpolationMode = mode;
        }

        static string Ellipsize(Graphics g, string text, Font f, Color c, float width)
        {
            var parts = TextUtil.Elements(text);
            int lo = 0, hi = parts.Count;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                string t = string.Concat(parts.GetRange(0, mid)).TrimEnd() + "…";
                if (Get(g, t, f, c, 0, false, false).Size.Width <= width) lo = mid; else hi = mid - 1;
            }
            return string.Concat(parts.GetRange(0, lo)).TrimEnd() + "…";
        }
    }

    /// <summary>Unicode-safe text helpers: never cut an accent or an emoji in half.</summary>
    static class TextUtil
    {
        /// <summary>Splits into user-perceived characters (an emoji with skin tone, a flag, é with a combining accent…).</summary>
        public static List<string> Elements(string s)
        {
            var list = new List<string>();
            var e = StringInfo.GetTextElementEnumerator(s ?? "");
            while (e.MoveNext()) list.Add((string)e.Current);
            return list;
        }

        /// <summary>Keeps at most <paramref name="max"/> UTF-16 units without breaking a character.</summary>
        public static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            var sb = new System.Text.StringBuilder();
            foreach (var el in Elements(s))
            {
                if (sb.Length + el.Length > max) break;
                sb.Append(el);
            }
            return sb.ToString();
        }
    }
}
