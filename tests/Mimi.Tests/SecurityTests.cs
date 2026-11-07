using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mimi.Tests
{
    [TestClass]
    public class SecurityTests
    {
        static SessionKeys Keys(byte seed = 1) => new SessionKeys(Enumerable.Range(0, 32).Select(i => (byte)(i * seed + 3)).ToArray());

        static PeerMessage Msg(long counter = 1, string body = "") => new PeerMessage
        {
            Type = "MSG", Sender = "abc", Counter = counter,
            TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Name = "Léa", Body = body
        };

        [TestMethod]
        public void SealedMessage_OpensWithTheSameKey()
        {
            var k = Keys();
            var m = Msg(7, "Dinner tonight? 💗 « ça va ? »");
            var back = SecurePacket.Open(k, SecurePacket.Seal(k, m));
            Assert.IsNotNull(back);
            Assert.AreEqual(m.Type, back.Type);
            Assert.AreEqual(m.Sender, back.Sender);
            Assert.AreEqual(7, back.Counter);
            Assert.AreEqual(m.TimeMs, back.TimeMs);
            Assert.AreEqual("Léa", back.Name);
            Assert.AreEqual(m.Body, back.Body);
        }

        [TestMethod]
        public void TextIsNotReadableOnTheWire()
        {
            var k = Keys();
            var data = SecurePacket.Seal(k, Msg(1, "secret words"));
            Assert.IsFalse(System.Text.Encoding.UTF8.GetString(data).Contains("secret"));
        }

        [TestMethod]
        public void AnyModifiedByte_IsRejected()
        {
            var k = Keys();
            var data = SecurePacket.Seal(k, Msg(1, "hello"));
            for (int i = 0; i < data.Length; i++)
            {
                var copy = (byte[])data.Clone();
                copy[i] ^= 0x01;
                Assert.IsNull(SecurePacket.Open(k, copy));
            }
        }

        [TestMethod]
        public void WrongKey_IsRejected()
        {
            var data = SecurePacket.Seal(Keys(1), Msg());
            Assert.IsNull(SecurePacket.Open(Keys(2), data));
        }

        [TestMethod]
        public void SeparatorCharacterInText_CannotForgeFields()
        {
            var k = Keys();
            var back = SecurePacket.Open(k, SecurePacket.Seal(k, Msg(1, "a\u001fHEART\u001fx")));
            Assert.IsNotNull(back);
            Assert.AreEqual("MSG", back.Type);
        }

        [TestMethod]
        public void ReplayGuard_RejectsRepeatsAndOldPackets()
        {
            var guard = new ReplayGuard();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var m1 = Msg(1); var m2 = Msg(2);
            Assert.IsTrue(guard.Accept(m1, now));
            Assert.IsFalse(guard.Accept(m1, now));           // same packet again
            Assert.IsTrue(guard.Accept(m2, now));
            Assert.IsFalse(guard.Accept(Msg(1), now));       // older counter
            var old = Msg(3); old.TimeMs = now - ReplayGuard.WindowMs - 1000;
            Assert.IsFalse(guard.Accept(old, now));          // too old
            var other = Msg(1); other.Sender = "another";
            Assert.IsTrue(guard.Accept(other, now));         // counters are per sender
        }

        [TestMethod]
        public void Pairing_BothSidesGetTheSameKey()
        {
            using (var host = new PairingSession("482913"))
            using (var joiner = new PairingSession("482913"))
            {
                var hostMaster = host.DeriveMaster(joiner.PublicKey, joiner.PublicKey, host.PublicKey);
                var joinMaster = joiner.DeriveMaster(host.PublicKey, joiner.PublicKey, host.PublicKey);
                CollectionAssert.AreEqual(hostMaster, joinMaster);
                Assert.AreEqual(32, hostMaster.Length);
                CollectionAssert.AreEqual(PairingSession.Confirm(hostMaster, "host"), PairingSession.Confirm(joinMaster, "host"));
                CollectionAssert.AreNotEqual(PairingSession.Confirm(hostMaster, "host"), PairingSession.Confirm(hostMaster, "join"));
            }
        }

        [TestMethod]
        public void Pairing_WithWrongCode_FailsConfirmation()
        {
            using (var host = new PairingSession("482913"))
            using (var joiner = new PairingSession("000000"))
            {
                var hostMaster = host.DeriveMaster(joiner.PublicKey, joiner.PublicKey, host.PublicKey);
                var joinMaster = joiner.DeriveMaster(host.PublicKey, joiner.PublicKey, host.PublicKey);
                CollectionAssert.AreNotEqual(hostMaster, joinMaster);
                Assert.AreNotEqual(PairingSession.CodeTag("482913"), PairingSession.CodeTag("000000"));
            }
        }

        [TestMethod]
        public void Pairing_EachSessionHasFreshKeys()
        {
            using (var a = new PairingSession("111111"))
            using (var b = new PairingSession("111111"))
                CollectionAssert.AreNotEqual(a.PublicKey, b.PublicKey);
        }

        [TestMethod]
        public void LegacyCouples_DeriveTheSameKeyOnBothSides()
        {
            CollectionAssert.AreEqual(Config.LegacyKey("123456"), Config.LegacyKey(" 123456 "));
            CollectionAssert.AreNotEqual(Config.LegacyKey("123456"), Config.LegacyKey("123457"));
            Assert.AreEqual(32, Config.LegacyKey("123456").Length);
        }

        [TestMethod]
        public void LegacyTag_MatchesVersion02Format()
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mimi:123456"));
                Assert.AreEqual(BitConverter.ToString(h, 0, 8).Replace("-", ""), PeerLink.LegacyTag("123456"));
            }
        }

        [TestMethod]
        public void Dpapi_RoundTrip()
        {
            var secret = Crypto.RandomBytes(32);
            var stored = Crypto.Protect(secret);
            StringAssert.StartsWith(stored, "dpapi:");
            CollectionAssert.AreEqual(secret, Crypto.Unprotect(stored));
            Assert.IsNull(Crypto.Unprotect("garbage"));
        }
    }
}
