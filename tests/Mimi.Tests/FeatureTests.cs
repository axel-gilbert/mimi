using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mimi.Tests
{
    [TestClass]
    public class ImageTransferTests
    {
        [TestMethod]
        public void Picture_IsCutAndReassembled_InAnyOrder()
        {
            var data = new byte[5000];
            new Random(42).NextBytes(data);
            var og = new OutgoingImage(data, "for you ♥");
            var inc = IncomingImage.FromHeader(og.Header, "Léa");
            Assert.IsNotNull(inc);
            Assert.AreEqual("for you ♥", inc.Caption);

            var order = Enumerable.Range(0, og.Count).Reverse().ToList();
            bool done = false;
            foreach (var i in order) done = inc.Add(og.Chunk(i));
            Assert.IsTrue(done);
            CollectionAssert.AreEqual(data, inc.Assemble());
        }

        [TestMethod]
        public void MissingChunks_AreReported_AndDuplicatesIgnored()
        {
            var data = new byte[3000];
            var og = new OutgoingImage(data, "");
            var inc = IncomingImage.FromHeader(og.Header, "x");
            inc.Add(og.Chunk(0));
            Assert.IsFalse(inc.Add(og.Chunk(0)));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, inc.Missing().ToArray());
        }

        [DataTestMethod]
        [DataRow("abc|0|10|")]
        [DataRow("abc|5|10|")]                 // count doesn't match size
        [DataRow("abc|1|99999999|")]           // too big
        [DataRow("garbage")]
        public void BadHeaders_AreRejected(string header) => Assert.IsNull(IncomingImage.FromHeader(header, "x"));

        [TestMethod]
        public void CorruptedChunk_IsRejected()
        {
            var og = new OutgoingImage(new byte[2000], "");
            var inc = IncomingImage.FromHeader(og.Header, "x");
            Assert.IsFalse(inc.Add(og.Id + "|0|not base64!!"));
            Assert.IsFalse(inc.Add(og.Id + "|0|" + Convert.ToBase64String(new byte[10]))); // wrong length
            Assert.IsFalse(inc.Add("other|0|" + Convert.ToBase64String(new byte[OutgoingImage.ChunkSize])));
        }

        [TestMethod]
        public void TransparentPicture_StaysPng_AndDecodes()
        {
            using (var bmp = new Bitmap(300, 200, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.FillEllipse(Brushes.HotPink, 20, 20, 160, 160);
                }
                var data = ImageCodec.Prepare(bmp);
                Assert.AreEqual(0x89, data[0]); // PNG signature
                Assert.AreEqual((byte)'P', data[1]);
                using (var back = ImageCodec.Decode(data))
                {
                    Assert.IsNotNull(back);
                    Assert.AreEqual(300, back.Width);
                }
            }
        }

        [TestMethod]
        public void BigPhoto_IsResizedUnderTheLimit()
        {
            using (var bmp = new Bitmap(4000, 3000, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    for (int i = 0; i < 400; i++)
                        g.FillRectangle(new SolidBrush(Color.FromArgb(i * 7 % 255, i * 13 % 255, i * 29 % 255)), i * 10, (i * 37) % 3000, 60, 60);
                var data = ImageCodec.Prepare(bmp);
                Assert.IsTrue(data.Length <= ImageCodec.MaxBytes);
                using (var back = ImageCodec.Decode(data))
                    Assert.IsTrue(Math.Max(back.Width, back.Height) <= ImageCodec.MaxSide);
            }
        }

        [TestMethod]
        public void Garbage_DoesNotDecode() => Assert.IsNull(ImageCodec.Decode(new byte[] { 1, 2, 3, 4 }));
    }

    [TestClass]
    public class StatsTests
    {
        [TestMethod]
        public void Streak_CountsConsecutiveDays()
        {
            var now = new DateTime(2026, 11, 7, 20, 0, 0);
            var s = new Stats { Now = () => now };
            s.Days["2026-11-04"] = new Stats.Day { Sent = 1 };
            s.Days["2026-11-05"] = new Stats.Day { Received = 2 };
            s.Days["2026-11-06"] = new Stats.Day { Sent = 3 };
            Assert.AreEqual(3, s.Streak());          // today has nothing yet: streak still alive
            s.HeartSent();
            Assert.AreEqual(4, s.Streak());
            s.Days.Remove("2026-11-05");
            Assert.AreEqual(2, s.Streak());
        }

        [TestMethod]
        public void LastDays_ReturnsOldestFirst_WithZeros()
        {
            var now = new DateTime(2026, 11, 7);
            var s = new Stats { Now = () => now };
            s.HeartReceived();
            var days = s.LastDays(7);
            Assert.AreEqual(7, days.Count);
            Assert.AreEqual(new DateTime(2026, 11, 1), days[0].day);
            Assert.AreEqual(1, days[6].received);
            Assert.AreEqual(0, days[0].sent);
        }
    }

    [TestClass]
    public class UpdaterTests
    {
        [DataTestMethod]
        [DataRow("v0.3.0", "0.3.0")]
        [DataRow("0.2.1", "0.2.1")]
        [DataRow("v1.2.3-beta", "1.2.3")]
        [DataRow("v2.0", "2.0.0")]
        public void Tags_AreParsed(string tag, string expected) => Assert.AreEqual(Version.Parse(expected), Updater.ParseTag(tag));

        [DataTestMethod]
        [DataRow("")]
        [DataRow(null)]
        [DataRow("latest")]
        public void BadTags_AreIgnored(string tag) => Assert.IsNull(Updater.ParseTag(tag));
    }
}
