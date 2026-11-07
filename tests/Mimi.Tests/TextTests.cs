using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mimi.Tests
{
    [TestClass]
    public class TextTests
    {
        [TestMethod]
        public void Truncate_NeverCutsAnEmojiInHalf()
        {
            string s = "abc\U0001F618\U0001F970"; // abc😘🥰  (each emoji = 2 UTF-16 units)
            Assert.AreEqual("abc", TextUtil.Truncate(s, 4));
            Assert.AreEqual("abc\U0001F618", TextUtil.Truncate(s, 5));
            Assert.AreEqual(s, TextUtil.Truncate(s, 7));
        }

        [TestMethod]
        public void Truncate_KeepsCombiningAccentsTogether()
        {
            string s = "Léa"; // "Léa" written with a combining accent
            Assert.AreEqual("L", TextUtil.Truncate(s, 2));
            Assert.AreEqual("Lé", TextUtil.Truncate(s, 3));
        }

        [TestMethod]
        public void Truncate_HandlesNullAndShortText()
        {
            Assert.AreEqual("", TextUtil.Truncate(null, 5));
            Assert.AreEqual("Zélie", TextUtil.Truncate("Zélie", 24));
        }

        [TestMethod]
        public void Translations_KeepAccentsEmojiAndNewLines()
        {
            // in the .ini files a new line is written as the two characters \ and n
            var d = L.Parse("# comment\nsay=Ça marche 💞" + @"\n" + "super\r\nother=x=y\n");
            Assert.AreEqual("Ça marche 💞\nsuper", d["say"]);
            Assert.AreEqual("x=y", d["other"]);
        }

        [TestMethod]
        public void EveryLanguageHasEveryKey()
        {
            string en = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "lang", "en.ini"));
            var keys = L.Parse(en).Keys;
            foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(Root(), "lang"), "*.ini"))
            {
                var d = L.Parse(System.IO.File.ReadAllText(file, System.Text.Encoding.UTF8));
                foreach (var k in keys)
                    Assert.IsTrue(d.ContainsKey(k), System.IO.Path.GetFileName(file) + " is missing " + k);
            }
        }

        static string Root()
        {
            var dir = System.AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Mimi.csproj"))) dir = System.IO.Path.GetDirectoryName(dir);
            if (dir == null) Assert.Inconclusive("repository root not found (tests copied elsewhere)");
            return dir;
        }
    }
}
