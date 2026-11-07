using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mimi
{
    /// <summary>Small cryptographic helpers (HMAC-SHA256, constant-time compare, random bytes).</summary>
    static class Crypto
    {
        public static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            using (var h = new HMACSHA256(key))
            {
                foreach (var p in parts) h.TransformBlock(p, 0, p.Length, null, 0);
                h.TransformFinalBlock(new byte[0], 0, 0);
                return h.Hash;
            }
        }

        public static byte[] Hmac(byte[] key, string label, params byte[][] parts) =>
            Hmac(key, new[] { Encoding.UTF8.GetBytes(label) }.Concat(parts).ToArray());

        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return b;
        }

        public static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

        /// <summary>Encrypts a secret for the current Windows user (DPAPI) before writing it to disk.</summary>
        public static string Protect(byte[] secret) =>
            "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(secret, Encoding.UTF8.GetBytes("Mimi"), DataProtectionScope.CurrentUser));

        public static byte[] Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dpapi:")) return null;
            try { return ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), Encoding.UTF8.GetBytes("Mimi"), DataProtectionScope.CurrentUser); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Keys derived from the couple's 32-byte master secret (established once, at pairing).
    /// Separate keys for encryption and authentication, plus a public 8-byte tag that lets
    /// a receiver quickly ignore packets that are not for this couple.
    /// </summary>
    sealed class SessionKeys
    {
        public readonly byte[] EncKey, MacKey, Tag;

        public SessionKeys(byte[] master)
        {
            if (master == null || master.Length != 32) throw new ArgumentException("master key must be 32 bytes");
            EncKey = Crypto.Hmac(master, "mimi v3 enc");
            MacKey = Crypto.Hmac(master, "mimi v3 mac");
            Tag = Crypto.Hmac(master, "mimi v3 tag").Take(8).ToArray();
        }
    }

    /// <summary>An application message, before encryption / after decryption.</summary>
    sealed class PeerMessage
    {
        public string Type = "";     // HEART, HOLD, SYNC, MSG, PING, PONG
        public string Sender = "";   // random id of the sending app instance
        public long Counter;         // strictly increasing per sender (anti-replay)
        public long TimeMs;          // sender clock, Unix milliseconds (anti-replay)
        public string Name = "";     // sender's display name
        public string Body = "";     // type-specific payload (e.g. the text of a MSG)
    }

    /// <summary>
    /// Encrypted, authenticated packets ("encrypt-then-MAC"):
    ///   "M3" | tag (8) | IV (16) | AES-256-CBC ciphertext | HMAC-SHA256 (32)
    /// The HMAC covers everything before it, so any modified byte makes the packet rejected.
    /// </summary>
    static class SecurePacket
    {
        static readonly byte[] Magic = { (byte)'M', (byte)'3' };
        const char Sep = '\u001f'; // ASCII "unit separator": cannot be typed in a message

        public static bool LooksLike(byte[] d) => d != null && d.Length >= 2 + 8 + 16 + 16 + 32 && d[0] == Magic[0] && d[1] == Magic[1];

        public static byte[] TagOf(byte[] d) => d.Skip(2).Take(8).ToArray();

        public static byte[] Seal(SessionKeys k, PeerMessage m)
        {
            string plain = string.Join(Sep.ToString(), new[]
            {
                Clean(m.Type), Clean(m.Sender), m.Counter.ToString(), m.TimeMs.ToString(), Clean(m.Name), Clean(m.Body)
            });
            byte[] iv = Crypto.RandomBytes(16);
            byte[] ct;
            using (var aes = Aes.Create())
            {
                aes.Key = k.EncKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var enc = aes.CreateEncryptor())
                {
                    var pt = Encoding.UTF8.GetBytes(plain);
                    ct = enc.TransformFinalBlock(pt, 0, pt.Length);
                }
            }
            using (var ms = new MemoryStream())
            {
                ms.Write(Magic, 0, 2);
                ms.Write(k.Tag, 0, 8);
                ms.Write(iv, 0, 16);
                ms.Write(ct, 0, ct.Length);
                var head = ms.ToArray();
                var mac = Crypto.Hmac(k.MacKey, head);
                ms.Write(mac, 0, mac.Length);
                return ms.ToArray();
            }
        }

        /// <summary>Returns the message, or null if the packet is not for us, modified, or malformed.</summary>
        public static PeerMessage Open(SessionKeys k, byte[] d)
        {
            if (!LooksLike(d)) return null;
            if (!Crypto.FixedTimeEquals(TagOf(d), k.Tag)) return null;
            int macAt = d.Length - 32;
            var mac = Crypto.Hmac(k.MacKey, d.Take(macAt).ToArray());
            if (!Crypto.FixedTimeEquals(mac, d.Skip(macAt).ToArray())) return null;
            var iv = d.Skip(10).Take(16).ToArray();
            var ct = d.Skip(26).Take(macAt - 26).ToArray();
            if (ct.Length == 0 || ct.Length % 16 != 0) return null;
            string plain;
            try
            {
                using (var aes = Aes.Create())
                {
                    aes.Key = k.EncKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    using (var dec = aes.CreateDecryptor())
                        plain = Encoding.UTF8.GetString(dec.TransformFinalBlock(ct, 0, ct.Length));
                }
            }
            catch { return null; }
            var f = plain.Split(Sep);
            if (f.Length != 6 || !long.TryParse(f[2], out long counter) || !long.TryParse(f[3], out long time)) return null;
            return new PeerMessage { Type = f[0], Sender = f[1], Counter = counter, TimeMs = time, Name = f[4], Body = f[5] };
        }

        static string Clean(string s) => (s ?? "").Replace(Sep, ' ');
    }

    /// <summary>
    /// Rejects replayed packets: the timestamp must be recent and the counter of each sender
    /// must always increase.
    /// </summary>
    sealed class ReplayGuard
    {
        public static readonly long WindowMs = 10 * 60 * 1000;
        readonly Dictionary<string, long> last = new Dictionary<string, long>();
        readonly Queue<string> order = new Queue<string>();

        public bool Accept(PeerMessage m, long nowMs)
        {
            if (Math.Abs(nowMs - m.TimeMs) > WindowMs) return false;
            lock (last)
            {
                if (last.TryGetValue(m.Sender, out long c))
                {
                    if (m.Counter <= c) return false;
                }
                else
                {
                    order.Enqueue(m.Sender);
                    if (order.Count > 64) last.Remove(order.Dequeue());
                }
                last[m.Sender] = m.Counter;
                return true;
            }
        }
    }

    /// <summary>
    /// One side of a pairing handshake.
    ///
    /// Both PCs generate a fresh ECDH P-256 key pair and exchange their public keys; the shared
    /// secret is mixed with the 6-digit code, so only someone who knows the code AND took part in
    /// the exchange gets the master key. Each side then proves it holds the same key with a
    /// confirmation HMAC. The code itself never travels on the network: only a short fingerprint
    /// of it, used to find the right partner.
    /// </summary>
    sealed class PairingSession : IDisposable
    {
        public readonly string Code;
        public readonly byte[] PublicKey;
        readonly ECDiffieHellmanCng ecdh;

        public PairingSession(string code)
        {
            Code = code;
            ecdh = new ECDiffieHellmanCng(256)
            {
                KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash,
                HashAlgorithm = CngAlgorithm.Sha256
            };
            PublicKey = ecdh.PublicKey.ToByteArray();
        }

        /// <summary>Fingerprint of the code, sent in clear so the right host answers.</summary>
        public static string CodeTag(string code) =>
            Crypto.Hex(Crypto.Hmac(Encoding.UTF8.GetBytes("mimi pairing code"), Encoding.UTF8.GetBytes(code)).Take(8).ToArray());

        /// <summary>Master key = HMAC(code, ECDH secret || joiner public key || host public key).</summary>
        public byte[] DeriveMaster(byte[] peerPublic, byte[] joinerPublic, byte[] hostPublic)
        {
            byte[] shared;
            using (var pk = ECDiffieHellmanCngPublicKey.FromByteArray(peerPublic, CngKeyBlobFormat.EccPublicBlob))
                shared = ecdh.DeriveKeyMaterial(pk);
            return Crypto.Hmac(Encoding.UTF8.GetBytes("mimi pairing v3 " + Code), shared, joinerPublic, hostPublic);
        }

        public static byte[] Confirm(byte[] master, string role) => Crypto.Hmac(master, "mimi confirm " + role);

        public void Dispose() => ecdh.Dispose();
    }
}
