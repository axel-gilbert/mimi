using System;
using System.Drawing;

namespace Mimi
{
    /// <summary>
    /// Mini moteur de rendu logiciel : dessine un sprite (rotation, échelle, squash &amp; stretch, opacité)
    /// directement dans la mémoire de la surface, en virgule fixe.
    /// Beaucoup plus léger que GDI+ (pas de transformations ni d'ImageAttributes).
    ///
    /// Gère aussi une grille de tuiles "sales" : on n'efface et n'envoie à l'écran que
    /// les zones où quelque chose a été dessiné, pas tout l'écran.
    /// </summary>
    unsafe sealed class Blitter
    {
        const int Tile = 64;
        readonly uint* px;
        readonly int w, h, tw, th;
        bool[] prev, cur;

        public Blitter(IntPtr bits, int width, int height)
        {
            px = (uint*)bits;
            w = width; h = height;
            tw = (w + Tile - 1) / Tile;
            th = (h + Tile - 1) / Tile;
            prev = new bool[tw * th];
            cur = new bool[tw * th];
        }

        /// <summary>Efface ce qui a été dessiné à l'image précédente. Renvoie la zone à envoyer à l'écran.</summary>
        public Rectangle BeginFrame()
        {
            // les tuiles dessinées à l'image précédente doivent être effacées puis renvoyées
            var t = prev; prev = cur; cur = t;
            Array.Clear(cur, 0, cur.Length);
            for (int ty = 0; ty < th; ty++)
                for (int tx = 0; tx < tw; tx++)
                {
                    if (!prev[ty * tw + tx]) continue;
                    // fusionne les tuiles consécutives d'une même ligne
                    int tx2 = tx;
                    while (tx2 + 1 < tw && prev[ty * tw + tx2 + 1]) tx2++;
                    int x0 = tx * Tile, x1 = Math.Min(w, (tx2 + 1) * Tile);
                    int y0 = ty * Tile, y1 = Math.Min(h, y0 + Tile);
                    int n = x1 - x0;
                    for (int y = y0; y < y1; y++)
                    {
                        uint* row = px + (long)y * w + x0;
                        int i = 0;
                        for (; i + 1 < n; i += 2) *(ulong*)(row + i) = 0;
                        if (i < n) row[i] = 0;
                    }
                    tx = tx2;
                }
            return Rectangle.Empty;
        }

        /// <summary>Zone englobant ce qui a été effacé et ce qui vient d'être dessiné.</summary>
        public Rectangle DirtyBounds()
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int ty = 0; ty < th; ty++)
                for (int tx = 0; tx < tw; tx++)
                {
                    int i = ty * tw + tx;
                    if (!prev[i] && !cur[i]) continue;
                    if (tx < minX) minX = tx;
                    if (tx > maxX) maxX = tx;
                    if (ty < minY) minY = ty;
                    if (ty > maxY) maxY = ty;
                }
            if (maxX < 0) return Rectangle.Empty;
            int x0 = minX * Tile, y0 = minY * Tile;
            return Rectangle.FromLTRB(x0, y0, Math.Min(w, (maxX + 1) * Tile), Math.Min(h, (maxY + 1) * Tile));
        }

        public void Mark(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, w, h));
            if (r.Width <= 0 || r.Height <= 0) return;
            int tx0 = r.Left / Tile, tx1 = (r.Right - 1) / Tile, ty0 = r.Top / Tile, ty1 = (r.Bottom - 1) / Tile;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                    cur[ty * tw + tx] = true;
        }

        /// <summary>
        /// Dessine le sprite centré en (cx, cy). Transformations appliquées au sprite dans l'ordre :
        /// échelle → rotation (rot) → étirement (sx, sy) selon l'axe stretchDeg.
        /// </summary>
        public void Draw(Art.Sprite s, float cx, float cy, float scale, float rotDeg, float sx, float sy, float stretchDeg, float alpha)
        {
            int a = (int)(alpha * 256);
            if (a <= 2 || scale <= 0.001f) return;
            if (a > 256) a = 256;

            double rr = rotDeg * Math.PI / 180, cr = Math.Cos(rr), sr = Math.Sin(rr);
            double m00 = cr * scale, m01 = -sr * scale, m10 = sr * scale, m11 = cr * scale;
            if (sx != 1f || sy != 1f)
            {
                double ra = stretchDeg * Math.PI / 180, ca = Math.Cos(ra), sa = Math.Sin(ra);
                double s00 = ca * ca * sx + sa * sa * sy, s01 = ca * sa * (sx - sy), s11 = sa * sa * sx + ca * ca * sy;
                double n00 = s00 * m00 + s01 * m10, n01 = s00 * m01 + s01 * m11;
                double n10 = s01 * m00 + s11 * m10, n11 = s01 * m01 + s11 * m11;
                m00 = n00; m01 = n01; m10 = n10; m11 = n11;
            }

            double hw = s.W / 2.0, hh = s.H / 2.0;
            // boîte englobante à l'écran
            double ex = Math.Abs(m00) * hw + Math.Abs(m01) * hh;
            double ey = Math.Abs(m10) * hw + Math.Abs(m11) * hh;
            int x0 = Math.Max(0, (int)Math.Floor(cx - ex)), x1 = Math.Min(w, (int)Math.Ceiling(cx + ex));
            int y0 = Math.Max(0, (int)Math.Floor(cy - ey)), y1 = Math.Min(h, (int)Math.Ceiling(cy + ey));
            if (x0 >= x1 || y0 >= y1) return;
            Mark(Rectangle.FromLTRB(x0, y0, x1, y1));

            double det = m00 * m11 - m01 * m10;
            if (Math.Abs(det) < 1e-9) return;
            double i00 = m11 / det, i01 = -m01 / det, i10 = -m10 / det, i11 = m00 / det;

            const double F = 65536.0;
            int du = (int)(i00 * F), dv = (int)(i10 * F);
            int sw = s.W, sh = s.H;
            fixed (int* spx = s.Px)
            {
                uint* src = (uint*)spx;
                for (int y = y0; y < y1; y++)
                {
                    double dy = y + 0.5 - cy, dx = x0 + 0.5 - cx;
                    int u = (int)((i00 * dx + i01 * dy + hw) * F);
                    int v = (int)((i10 * dx + i11 * dy + hh) * F);
                    uint* d = px + (long)y * w + x0;
                    for (int x = x0; x < x1; x++, d++, u += du, v += dv)
                    {
                        int iu = u >> 16, iv = v >> 16;
                        if ((uint)iu >= (uint)sw || (uint)iv >= (uint)sh) continue;
                        uint p = src[iv * sw + iu];
                        if (p == 0) continue;
                        if (a < 256)
                        {
                            p = (((p & 0x00FF00FFu) * (uint)a >> 8) & 0x00FF00FFu)
                              | (((p >> 8 & 0x00FF00FFu) * (uint)a) & 0xFF00FF00u);
                        }
                        uint sa = p >> 24;
                        if (sa == 255) { *d = p; continue; }
                        uint dp = *d;
                        if (dp == 0) { *d = p; continue; }
                        uint inv = 255 - sa;
                        uint rb = ((dp & 0x00FF00FFu) * inv >> 8) & 0x00FF00FFu;
                        uint ag = ((dp >> 8 & 0x00FF00FFu) * inv) & 0xFF00FF00u;
                        *d = p + rb + ag;
                    }
                }
            }
        }
    }
}
