using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Mimi
{
    /// <summary>
    /// Translations. Each language is a lang/&lt;code&gt;.ini file embedded in the exe ("key=value", one per line,
    /// \n for a new line). A missing key falls back to English. On first launch the language follows Windows.
    /// To add a language: add lang/xx.ini and a line in <see cref="Languages"/>.
    /// </summary>
    static class L
    {
        public static string Code = "en";

        /// <summary>(code, name of the language in that language), in the order shown in the settings.</summary>
        public static readonly string[][] Languages =
        {
            new[] { "en", "English" },
            new[] { "fr", "Français" },
            new[] { "es", "Español" },
            new[] { "pt", "Português" },
            new[] { "it", "Italiano" },
            new[] { "de", "Deutsch" },
            new[] { "nl", "Nederlands" },
            new[] { "pl", "Polski" },
            new[] { "sv", "Svenska" },
            new[] { "da", "Dansk" },
            new[] { "nb", "Norsk" },
            new[] { "fi", "Suomi" },
            new[] { "cs", "Čeština" },
            new[] { "sk", "Slovenčina" },
            new[] { "el", "Ελληνικά" },
            new[] { "hu", "Magyar" },
            new[] { "ro", "Română" },
            new[] { "bg", "Български" },
            new[] { "hr", "Hrvatski" },
            new[] { "uk", "Українська" },
            new[] { "ru", "Русский" },
            new[] { "tr", "Türkçe" },
            new[] { "ar", "العربية" },
            new[] { "he", "עברית" },
            new[] { "fa", "فارسی" },
            new[] { "hi", "हिन्दी" },
            new[] { "zh", "中文" },
            new[] { "ja", "日本語" },
            new[] { "ko", "한국어" },
            new[] { "vi", "Tiếng Việt" },
            new[] { "th", "ไทย" },
            new[] { "id", "Bahasa Indonesia" },
        };

        /// <summary>Languages actually shipped in this exe (a lang/xx.ini file exists).</summary>
        public static string[][] Available => Languages.Where(l => Has(l[0])).ToArray();

        static readonly Dictionary<string, Dictionary<string, string>> cache = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>True if this language is shipped in the exe.</summary>
        public static bool Has(string code) => code != null && Load(code).Count > 0;

        /// <summary>The Windows display language if Mimi has it, otherwise English.</summary>
        public static string Detect()
        {
            string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (two == "no" || two == "nn") two = "nb";
            return Languages.Any(l => l[0] == two) && Has(two) ? two : "en";
        }

        /// <summary>Languages written right to left.</summary>
        public static bool IsRightToLeft => Code == "ar" || Code == "he" || Code == "fa";

        public static string T(string key)
        {
            if (Load(Code).TryGetValue(key, out var s)) return s;
            return Load("en").TryGetValue(key, out s) ? s : key;
        }

        public static string F(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch (FormatException) { return string.Format(Load("en").TryGetValue(key, out var s) ? s : key, args); }
        }

        static Dictionary<string, string> Load(string code)
        {
            lock (cache)
            {
                if (cache.TryGetValue(code, out var d)) return d;
                d = new Dictionary<string, string>();
                try
                {
                    using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mimi.lang." + code + ".ini"))
                        if (st != null)
                            d = Parse(new StreamReader(st, Encoding.UTF8).ReadToEnd());
                }
                catch { }
                cache[code] = d;
                return d;
            }
        }

        /// <summary>Parses "key=value" lines; \n in a value becomes a new line. Lines starting with # are comments.</summary>
        internal static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Replace("\\n", "\n");
            }
            return d;
        }
    }
}
