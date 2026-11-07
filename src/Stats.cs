using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Mimi
{
    /// <summary>
    /// Cute statistics: hearts sent and received per day, messages, pictures, syncs, big hearts,
    /// and the streak (consecutive days with at least one heart). Stored in stats.ini.
    /// </summary>
    sealed class Stats
    {
        public sealed class Day { public int Sent, Received; }

        public long HeartsSent, HeartsReceived, NotesSent, NotesReceived, PicturesSent, PicturesReceived, Syncs, BigHearts;
        public readonly SortedDictionary<string, Day> Days = new SortedDictionary<string, Day>(StringComparer.Ordinal);

        /// <summary>Clock used for "today" (replaceable in tests).</summary>
        public Func<DateTime> Now = () => DateTime.Now;

        string path;
        bool dirty;

        static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        Day Today()
        {
            string k = Key(Now());
            if (!Days.TryGetValue(k, out var d)) Days[k] = d = new Day();
            return d;
        }

        public void HeartSent() { HeartsSent++; Today().Sent++; dirty = true; }
        public void HeartReceived() { HeartsReceived++; Today().Received++; dirty = true; }
        public void NoteSent() { NotesSent++; dirty = true; }
        public void NoteReceived() { NotesReceived++; dirty = true; }
        public void PictureSent() { PicturesSent++; dirty = true; }
        public void PictureReceived() { PicturesReceived++; dirty = true; }
        public void Sync() { Syncs++; dirty = true; }
        public void BigHeart() { BigHearts++; dirty = true; }

        public Day TodayCounts => Days.TryGetValue(Key(Now()), out var d) ? d : new Day();

        /// <summary>Consecutive days with at least one heart (today counts if it already has one).</summary>
        public int Streak()
        {
            var day = Now().Date;
            bool Active(DateTime d) => Days.TryGetValue(Key(d), out var c) && c.Sent + c.Received > 0;
            if (!Active(day)) day = day.AddDays(-1);
            int n = 0;
            while (Active(day)) { n++; day = day.AddDays(-1); }
            return n;
        }

        /// <summary>Hearts (sent, received) for the last <paramref name="count"/> days, oldest first.</summary>
        public List<(DateTime day, int sent, int received)> LastDays(int count)
        {
            var list = new List<(DateTime, int, int)>();
            var today = Now().Date;
            for (int i = count - 1; i >= 0; i--)
            {
                var d = today.AddDays(-i);
                Days.TryGetValue(Key(d), out var c);
                list.Add((d, c?.Sent ?? 0, c?.Received ?? 0));
            }
            return list;
        }

        // ───────────── storage ─────────────

        public static Stats Load(string file)
        {
            var s = new Stats { path = file };
            try
            {
                if (!File.Exists(file)) return s;
                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0 || line.StartsWith("#")) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    long.TryParse(v, out long n);
                    switch (k)
                    {
                        case "HeartsSent": s.HeartsSent = n; break;
                        case "HeartsReceived": s.HeartsReceived = n; break;
                        case "NotesSent": s.NotesSent = n; break;
                        case "NotesReceived": s.NotesReceived = n; break;
                        case "PicturesSent": s.PicturesSent = n; break;
                        case "PicturesReceived": s.PicturesReceived = n; break;
                        case "Syncs": s.Syncs = n; break;
                        case "BigHearts": s.BigHearts = n; break;
                        default:
                            if (k.StartsWith("Day."))
                            {
                                var parts = v.Split(',');
                                if (parts.Length == 2 && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b))
                                    s.Days[k.Substring(4)] = new Day { Sent = a, Received = b };
                            }
                            break;
                    }
                }
            }
            catch { }
            return s;
        }

        /// <summary>Writes to disk if something changed. Only the last 90 days are kept.</summary>
        public void Save()
        {
            if (!dirty || path == null) return;
            try
            {
                string oldest = Key(Now().AddDays(-90));
                foreach (var k in Days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList()) Days.Remove(k);
                var sb = new StringBuilder("# Mimi statistics\r\n");
                sb.AppendLine("HeartsSent=" + HeartsSent);
                sb.AppendLine("HeartsReceived=" + HeartsReceived);
                sb.AppendLine("NotesSent=" + NotesSent);
                sb.AppendLine("NotesReceived=" + NotesReceived);
                sb.AppendLine("PicturesSent=" + PicturesSent);
                sb.AppendLine("PicturesReceived=" + PicturesReceived);
                sb.AppendLine("Syncs=" + Syncs);
                sb.AppendLine("BigHearts=" + BigHearts);
                foreach (var kv in Days) sb.AppendLine("Day." + kv.Key + "=" + kv.Value.Sent + "," + kv.Value.Received);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                dirty = false;
            }
            catch { }
        }
    }
}
