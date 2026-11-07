using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace Mimi
{
    /// <summary>
    /// Cute little sounds synthesized in code (no audio files to embed):
    ///  - rising "bloop" on a pentatonic scale (spamming plays a little melody),
    ///  - "mi-mi" chime when a wave of hearts arrives,
    ///  - magical arpeggio for the mega heart.
    /// Several players per sound so they can overlap.
    /// </summary>
    sealed class SoundBank
    {
        const int Rate = 44100;
        static readonly double[] Penta = { 523.25, 587.33, 659.25, 783.99, 880.0, 1046.5 };
        const int SEND = 6, CHIME = 7, MEGA = 8;

        readonly List<MediaPlayer[]> pools = new List<MediaPlayer[]>();
        readonly int[] next;
        readonly Random rng = new Random();
        readonly bool ok;
        DateTime lastPop = DateTime.MinValue;

        public int Volume = 70;

        public SoundBank()
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "Mimi-sounds-" + Program.Profile);
                Directory.CreateDirectory(dir);
                var clips = new List<short[]>();
                foreach (var f in Penta) clips.Add(Bloop(f, 0.55, 0.085));
                clips.Add(Bloop(1174.7, 0.30, 0.05)); // SEND: soft little "tick"
                clips.Add(ChimeClip());
                clips.Add(MegaArp());
                for (int i = 0; i < clips.Count; i++)
                {
                    var path = Path.Combine(dir, "s" + i + ".wav");
                    try { File.WriteAllBytes(path, Wav(clips[i])); } catch { /* already present / locked */ }
                    var arr = new MediaPlayer[i < Penta.Length ? 2 : 3];
                    for (int j = 0; j < arr.Length; j++)
                    {
                        arr[j] = new MediaPlayer();
                        arr[j].Open(new Uri(path));
                    }
                    pools.Add(arr);
                }
                next = new int[pools.Count];
                ok = true;
            }
            catch { ok = false; next = new int[0]; }
        }

        void Play(int i, double mul = 1)
        {
            if (!ok || Volume <= 0 || i >= pools.Count) return;
            try
            {
                var arr = pools[i];
                var mp = arr[next[i]++ % arr.Length];
                double v = Volume / 100.0;
                mp.Volume = Math.Min(1, v * v * mul + 0.0001);
                mp.Position = TimeSpan.Zero;
                mp.Play();
            }
            catch { }
        }

        public void Pop()
        {
            var now = DateTime.UtcNow;
            if ((now - lastPop).TotalMilliseconds < 45) return;
            lastPop = now;
            Play(rng.Next(Penta.Length));
        }
        public void Send() => Play(SEND, 0.7);
        public void Chime() => Play(CHIME);
        public void Mega() => Play(MEGA);

        // ───────────── synthesis ─────────────

        /// <summary>"Bloop": sine wave with a fast rising pitch + a small high-pitched sparkle.</summary>
        static short[] Bloop(double f0, double amp, double tau)
        {
            int n = (int)(Rate * 0.34);
            var buf = new double[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double f = f0 * (0.72 + 0.48 * (1 - Math.Exp(-t / 0.028)));
                phase += 2 * Math.PI * f / Rate;
                double env = (1 - Math.Exp(-t / 0.004)) * Math.Exp(-t / tau);
                double s = (Math.Sin(phase) * 0.8 + Math.Sin(2 * phase) * 0.18) * env;
                double t2 = t - 0.045;
                if (t2 > 0) s += Math.Sin(2 * Math.PI * f0 * 3 * t2) * Math.Exp(-t2 / 0.055) * (1 - Math.Exp(-t2 / 0.002)) * 0.16;
                buf[i] = s * amp;
            }
            return ToPcm(buf);
        }

        static void Bell(double[] buf, double startSec, double f, double amp, double tau)
        {
            int start = (int)(startSec * Rate);
            for (int i = start; i < buf.Length; i++)
            {
                double t = (double)(i - start) / Rate;
                double env = (1 - Math.Exp(-t / 0.003)) * Math.Exp(-t / tau);
                double s = Math.Sin(2 * Math.PI * f * t)
                         + 0.25 * Math.Sin(2 * Math.PI * 2 * f * t) * Math.Exp(-t / (tau * 0.4))
                         + 0.08 * Math.Sin(2 * Math.PI * 3.01 * f * t) * Math.Exp(-t / (tau * 0.25));
                buf[i] += s * env * amp;
            }
        }

        /// <summary>Two-note chime: "mi-mi".</summary>
        static short[] ChimeClip()
        {
            var buf = new double[(int)(Rate * 1.0)];
            Bell(buf, 0.00, 1318.5, 0.30, 0.30); // E6
            Bell(buf, 0.12, 1760.0, 0.26, 0.38); // A6
            return ToPcm(buf);
        }

        static short[] MegaArp()
        {
            var buf = new double[(int)(Rate * 1.4)];
            double[] notes = { 1046.5, 1318.5, 1568.0, 2093.0, 2637.0 };
            for (int i = 0; i < notes.Length; i++) Bell(buf, i * 0.065, notes[i], 0.22, 0.4);
            return ToPcm(buf);
        }

        static short[] ToPcm(double[] buf)
        {
            var o = new short[buf.Length];
            int fade = Math.Min(400, buf.Length);
            for (int i = 0; i < buf.Length; i++)
            {
                double v = buf[i];
                if (i > buf.Length - fade) v *= (double)(buf.Length - i) / fade;
                v = Math.Tanh(v * 1.2); // soft limiter
                o[i] = (short)(v * 30000);
            }
            return o;
        }

        static byte[] Wav(short[] s)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int data = s.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2);
                w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
                foreach (var v in s) w.Write(v);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
