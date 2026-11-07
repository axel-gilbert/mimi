using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>
    /// Peer-to-peer link over UDP. No server, no relay: the two PCs talk directly.
    ///
    /// After pairing, every packet is encrypted and authenticated with the couple's key
    /// (see <see cref="SecurePacket"/>) and checked against replays (<see cref="ReplayGuard"/>).
    ///
    /// Pairing ("MIMIP" text packets, see <see cref="PairingSession"/>):
    ///   joiner  → broadcast  PAIR    (code fingerprint, joiner public key, name)
    ///   host    → joiner     PAIROK  (host public key, name, host confirmation)
    ///   joiner  → host       PAIRFIN (joiner confirmation)
    /// Both sides end up with the same master key and each other's address.
    ///
    /// Compatibility: couples paired with 0.1/0.2 keep working. Their key is derived from the old
    /// pairing code, and the old clear-text protocol ("MIMI|1|…") is still spoken until the partner
    /// sends a packet in the new format, which means they have updated too.
    ///
    /// If the partner's IP address changes, PINGs are also broadcast on the local network
    /// while offline, so both PCs find each other again by themselves.
    /// </summary>
    sealed class PeerLink : IDisposable
    {
        const int SIO_UDP_CONNRESET = -1744830452;
        static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(20);

        /// <summary>Random id of this app instance (lets us ignore our own broadcasts).</summary>
        public static readonly string Instance = Crypto.Hex(Crypto.RandomBytes(5));

        readonly Config cfg;
        readonly Control ui;
        readonly System.Windows.Forms.Timer pinger = new System.Windows.Forms.Timer { Interval = 6000 };
        readonly System.Windows.Forms.Timer pairTimer = new System.Windows.Forms.Timer { Interval = 700 };
        readonly System.Windows.Forms.Timer imageTimer = new System.Windows.Forms.Timer { Interval = 250 };
        readonly Dictionary<string, OutgoingImage> outgoing = new Dictionary<string, OutgoingImage>();
        readonly Dictionary<string, int> headerResends = new Dictionary<string, int>();
        readonly Dictionary<string, IncomingImage> incoming = new Dictionary<string, IncomingImage>();
        readonly HashSet<string> doneImages = new HashSet<string>();
        readonly ReplayGuard replay = new ReplayGuard();
        SessionKeys keys;
        UdpClient udp;
        volatile bool alive;
        bool reportedOnline;
        long lastSeenTicks, counter;
        IPEndPoint cachedEp; string cachedFor; DateTime cachedAt;

        // pairing state
        enum PairRole { None, Host, Join }
        PairRole role = PairRole.None;
        PairingSession pairing;
        DateTime pairUntil;
        readonly Dictionary<string, PendingJoin> pending = new Dictionary<string, PendingJoin>();
        sealed class PendingJoin { public byte[] Master, JoinerPub; public string Name; public byte[] PairOk; }

        public string Error { get; private set; }
        public string LastPeerName = "";

        public event Action<string, string> Heart;                 // sender name, sender's heart theme
        public event Action<string, bool, int, string> Hold;       // name, started?, duration ms, theme
        public event Action<string, string> Sync;                  // name, theme
        public event Action<string, string> Note;                  // name, text
        public event Action StatusChanged;
        public event Action<byte[], IPEndPoint, string> Paired;    // master key, address, name
        public event Action PairTimeout;
        public event Action<IPEndPoint> PeerMoved;
        public event Action LegacyRetired;                         // partner now speaks the new protocol
        public event Action<string, byte[], string> Picture;       // name, image bytes, caption
        public event Action<bool> PartnerDnd;                      // partner's "do not disturb" state
        public event Action<string, string, string> GifReceived;   // name, GIPHY id, caption

        /// <summary>Our own "do not disturb" state, told to the partner in PING/PONG.</summary>
        public bool Dnd;

        public PeerLink(Config cfg, Control ui)
        {
            this.cfg = cfg;
            this.ui = ui;
            if (cfg.Key != null) keys = new SessionKeys(cfg.Key);
            pinger.Tick += (s, e) => { Ping(); CheckStatus(); };
            pairTimer.Tick += (s, e) => PairTick();
            imageTimer.Tick += (s, e) => ImageTick();
        }

        public bool Online => keys != null && DateTime.UtcNow.Ticks - Interlocked.Read(ref lastSeenTicks) < OnlineWindow.Ticks;
        public bool IsPairing => role != PairRole.None;
        bool Legacy => cfg.LegacyCode.Length > 0;

        public void Start()
        {
            try
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.ExclusiveAddressUse = true;
                udp.EnableBroadcast = true;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, cfg.Port));
                try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { }
            }
            catch (Exception e)
            {
                Error = L.F("net.portused", cfg.Port, e.Message);
                Program.Trace("bind failed: " + e.Message);
                try { udp?.Close(); } catch { }
                udp = null;
                return;
            }
            alive = true;
            new Thread(RxLoop) { IsBackground = true, Name = "mimi-rx" }.Start();
            pinger.Start();
            Ping();
        }

        // ───────────── sending ─────────────

        public void SendHeart(string theme) => SendToPeer("HEART", "th=" + theme, "HEART");
        public void SendHold(bool start, int ms, string theme) => SendToPeer("HOLD", start ? "start;th=" + theme : "end:" + ms, start ? "HEART" : null);
        public void SendSync(string theme) => SendToPeer("SYNC", "th=" + theme, null);

        /// <summary>Extracts "th=…" from a message body (the sender's heart theme), or "".</summary>
        static string ThemeOf(string body)
        {
            int i = (body ?? "").IndexOf("th=", StringComparison.Ordinal);
            return i < 0 ? "" : Art.NormalizeTheme(body.Substring(i + 3));
        }
        public void SendNote(string text) => SendToPeer("MSG", text ?? "", null);
        public void SendGif(string giphyId, string caption) => SendToPeer("GIF", giphyId + "|" + (caption ?? "").Trim(), null);

        /// <summary>Sends a prepared picture (see <see cref="ImageCodec.Prepare(Image)"/>) with an optional caption.</summary>
        public void SendPicture(byte[] data, string caption)
        {
            if (keys == null || !cfg.IsPaired || udp == null) return;
            var img = new OutgoingImage(data, (caption ?? "").Trim());
            outgoing[img.Id] = img;
            headerResends[img.Id] = 0;
            imageTimer.Start();
            var u = udp;
            string peer = cfg.Peer;
            var header = Seal("IMG", img.Header);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var ep = Resolve(peer);
                    if (ep == null) return;
                    u.Send(header, header.Length, ep);
                    for (int i = 0; i < img.Count; i++)
                    {
                        var d = Seal("IMGP", img.Chunk(i));
                        u.Send(d, d.Length, ep);
                        if (i % 16 == 15) Thread.Sleep(2); // don't flood the network
                    }
                }
                catch { }
            });
        }

        void SendChunks(OutgoingImage img, IEnumerable<int> indices, IPEndPoint to)
        {
            var u = udp;
            if (u == null) return;
            var list = indices.Where(i => i >= 0 && i < img.Count).Distinct().ToList();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int n = 0;
                foreach (var i in list)
                {
                    try { var d = Seal("IMGP", img.Chunk(i)); u.Send(d, d.Length, to); } catch { }
                    if (++n % 16 == 0) Thread.Sleep(2);
                }
            });
        }

        /// <summary>Every 250 ms: ask for missing chunks, resend lost headers, forget old transfers.</summary>
        void ImageTick()
        {
            var now = DateTime.UtcNow;
            foreach (var inc in incoming.Values.ToList())
            {
                if (inc.Complete) continue;
                if ((now - inc.LastActivity).TotalMilliseconds < 400) continue;
                if (inc.NackRounds >= 10) { incoming.Remove(inc.Id); continue; }
                inc.NackRounds++;
                inc.LastActivity = now;
                var missing = inc.Missing().Take(150);
                SendToPeer("IMGN", inc.Id + "|" + string.Join(",", missing), null);
            }
            foreach (var og in outgoing.Values.ToList())
            {
                double age = (now - og.Created).TotalSeconds;
                if (age > 60) { outgoing.Remove(og.Id); headerResends.Remove(og.Id); continue; }
                if (age > 1.5 * (headerResends[og.Id] + 1) && headerResends[og.Id] < 3)
                {
                    headerResends[og.Id]++;
                    SendToPeer("IMG", og.Header, null);
                }
            }
            if (incoming.Count == 0 && outgoing.Count == 0) imageTimer.Stop();
        }

        /// <summary>Picture packets, handled on the UI thread.</summary>
        void HandleImage(string type, string body, IPEndPoint from, string who)
        {
            string id = IncomingImage.IdOf(body);
            switch (type)
            {
                case "IMG":
                    if (doneImages.Contains(id)) { SendToPeer("IMGA", id, null); return; }
                    if (!incoming.ContainsKey(id))
                    {
                        var inc = IncomingImage.FromHeader(body, who);
                        if (inc == null) return;
                        if (incoming.Count > 4) incoming.Clear();
                        incoming[id] = inc;
                        imageTimer.Start();
                    }
                    break;
                case "IMGP":
                    if (incoming.TryGetValue(id, out var part) && part.Add(body))
                    {
                        incoming.Remove(id);
                        doneImages.Add(id);
                        SendToPeer("IMGA", id, null);
                        Picture?.Invoke(part.From, part.Assemble(), part.Caption);
                    }
                    break;
                case "IMGN":
                    if (outgoing.TryGetValue(id, out var og))
                    {
                        var f = body.Split('|');
                        if (f.Length == 2)
                            SendChunks(og, f[1].Split(',').Select(x => int.TryParse(x, out int i) ? i : -1), from);
                    }
                    break;
                case "IMGA":
                    outgoing.Remove(id);
                    headerResends.Remove(id);
                    break;
            }
        }

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        byte[] Seal(string type, string body) => SecurePacket.Seal(keys, new PeerMessage
        {
            Type = type,
            Sender = Instance,
            Counter = Interlocked.Increment(ref counter),
            TimeMs = NowMs(),
            Name = Clean(cfg.Name),
            Body = body
        });

        void Ping()
        {
            if (keys == null || !cfg.IsPaired) return;
            SendToPeer("PING", Dnd ? "dnd" : "", "PING");
            // Offline? Also shout on the local network, in case the partner's IP address changed.
            if (!Online)
            {
                Broadcast(Seal("PING", Dnd ? "dnd" : ""), null);
                if (Legacy) Broadcast(LegacyPacket("PING"), null);
            }
        }

        /// <param name="legacyType">What to send to a partner still on 0.1/0.2 (null = nothing).</param>
        void SendToPeer(string type, string body, string legacyType)
        {
            var u = udp;
            if (u == null || keys == null || !cfg.IsPaired) return;
            var data = Seal(type, body);
            var old = Legacy && legacyType != null ? LegacyPacket(legacyType) : null;
            string peer = cfg.Peer;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var ep = Resolve(peer);
                    if (ep == null) return;
                    u.Send(data, data.Length, ep);
                    if (old != null) u.Send(old, old.Length, ep);
                }
                catch { }
            });
        }

        void SendTo(byte[] data, IPEndPoint ep)
        {
            try { udp?.Send(data, data.Length, ep); } catch { }
        }

        /// <summary>Sends to every local network (and to an optional explicit address, e.g. a Tailscale IP).</summary>
        void Broadcast(byte[] data, string extraPeer)
        {
            var u = udp;
            if (u == null) return;
            int[] ports = { Config.DefaultPort, Config.DefaultPort + 1 };
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var targets = new List<IPEndPoint>();
                foreach (var ip in new[] { IPAddress.Broadcast, IPAddress.Loopback }.Concat(DirectedBroadcasts()).Distinct())
                    foreach (var port in ports)
                        if (!(ip.Equals(IPAddress.Loopback) && port == cfg.Port)) targets.Add(new IPEndPoint(ip, port));
                if (!string.IsNullOrWhiteSpace(extraPeer))
                    try { var ep = Resolve(extraPeer); if (ep != null) targets.Add(ep); } catch { }
                foreach (var ep in targets)
                    try { u.Send(data, data.Length, ep); } catch { }
            });
        }

        static IEnumerable<IPAddress> DirectedBroadcasts()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var ip = ua.Address.GetAddressBytes();
                        var mask = ua.IPv4Mask.GetAddressBytes();
                        var b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                        list.Add(new IPAddress(b));
                    }
                }
            }
            catch { }
            return list;
        }

        IPEndPoint Resolve(string peer)
        {
            lock (this)
            {
                if (cachedEp != null && cachedFor == peer && (DateTime.UtcNow - cachedAt).TotalSeconds < 30) return cachedEp;
            }
            string host = peer.Trim();
            int port = Config.DefaultPort;
            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host.Substring(colon + 1), out int p))
            {
                port = p;
                host = host.Substring(0, colon);
            }
            if (!IPAddress.TryParse(host, out IPAddress ip))
            {
                ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip == null) return null;
            }
            var ep = new IPEndPoint(ip, port);
            lock (this) { cachedEp = ep; cachedFor = peer; cachedAt = DateTime.UtcNow; }
            return ep;
        }

        public static string FormatPeer(IPEndPoint ep) =>
            ep.Port == Config.DefaultPort ? ep.Address.ToString() : ep.Address + ":" + ep.Port;

        // ───────────── old protocol (0.1 / 0.2), kept for compatibility ─────────────

        /// <summary>Tag used by 0.1/0.2: first 8 bytes of SHA-256("mimi:" + code), upper-case hex.</summary>
        internal static string LegacyTag(string code)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes("mimi:" + (code ?? "").Trim()));
                return BitConverter.ToString(h, 0, 8).Replace("-", "");
            }
        }

        byte[] LegacyPacket(string type) =>
            Encoding.UTF8.GetBytes("MIMI|1|" + LegacyTag(cfg.LegacyCode) + "|" + type + "|" + Clean(cfg.Name) + "|" + Instance);

        void HandleLegacy(byte[] d, IPEndPoint from)
        {
            if (!Legacy || keys == null) return;
            var f = Encoding.UTF8.GetString(d).Split('|');
            if (f.Length < 6 || f[0] != "MIMI" || f[5] == Instance || f[2] != LegacyTag(cfg.LegacyCode)) return;
            Program.Trace("legacy " + f[3] + " from " + from);
            Interlocked.Exchange(ref lastSeenTicks, DateTime.UtcNow.Ticks);
            string name = Clean(f[4]);
            if (name.Length > 0) LastPeerName = name;
            string who = LastPeerName.Length > 0 ? LastPeerName : L.T("Your.half");
            if (f[3] == "PING") SendTo(LegacyPacket("PONG"), from);
            else if (f[3] == "HEART") Post(() => Heart?.Invoke(who, ""));
            Post(() => { FollowAddress(from); CheckStatus(); });
        }

        // ───────────── pairing ─────────────

        public static string NewCode()
        {
            var b = Crypto.RandomBytes(4);
            return (BitConverter.ToUInt32(b, 0) % 1000000).ToString("000000");
        }

        public static string NormalizeCode(string s) => new string((s ?? "").Where(char.IsDigit).ToArray());

        /// <summary>This PC shows a code and waits for the other one to type it (5 minutes).</summary>
        public void HostPairing(string code)
        {
            CancelPairing();
            role = PairRole.Host;
            pairing = new PairingSession(code);
            pairUntil = DateTime.UtcNow.AddMinutes(5);
            pairTimer.Start();
        }

        /// <summary>This PC looks for the one showing this code (20 seconds).</summary>
        public void JoinPairing(string code)
        {
            CancelPairing();
            role = PairRole.Join;
            pairing = new PairingSession(code);
            pairUntil = DateTime.UtcNow.AddSeconds(20);
            pairTimer.Start();
            PairTick();
        }

        public void CancelPairing()
        {
            role = PairRole.None;
            pairTimer.Stop();
            pairing?.Dispose();
            pairing = null;
            pending.Clear();
        }

        void PairTick()
        {
            if (role == PairRole.None) { pairTimer.Stop(); return; }
            if (DateTime.UtcNow > pairUntil)
            {
                CancelPairing();
                PairTimeout?.Invoke();
                return;
            }
            // The joiner also targets the address typed in "Advanced", for pairing over Tailscale.
            if (role == PairRole.Join)
            {
                Program.Trace("broadcast PAIR");
                Broadcast(PairPacket("PAIR", pairing.PublicKey, null), cfg.Peer);
            }
        }

        string CodeTag => PairingSession.CodeTag(pairing.Code);

        byte[] PairPacket(string type, byte[] pub, byte[] confirm) =>
            Encoding.ASCII.GetBytes(string.Join("|", "MIMIP", "3", CodeTag, type, Instance,
                Convert.ToBase64String(pub ?? new byte[0]),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Clean(cfg.Name))),
                Convert.ToBase64String(confirm ?? new byte[0])));

        /// <summary>Handles a pairing packet (UI thread).</summary>
        void HandlePairing(string[] f, IPEndPoint from)
        {
            Program.Trace("pair packet " + (f.Length > 3 ? f[3] : "?") + " from " + from + " role=" + role + " match=" + (pairing != null && f.Length > 2 && f[2] == CodeTag));
            if (pairing == null || f.Length != 8 || f[1] != "3" || f[2] != CodeTag) return;
            string type = f[3], inst = f[4];
            byte[] pub, conf; string name;
            try
            {
                pub = Convert.FromBase64String(f[5]);
                name = Clean(Encoding.UTF8.GetString(Convert.FromBase64String(f[6])));
                conf = Convert.FromBase64String(f[7]);
            }
            catch { return; }

            if (role == PairRole.Host && type == "PAIR" && pub.Length > 0 && pub.Length < 200)
            {
                // Same joiner retrying (lost packet)? Answer again with the same reply.
                if (!pending.TryGetValue(inst, out var pj) || !Crypto.FixedTimeEquals(pj.JoinerPub, pub))
                {
                    byte[] master;
                    try { master = pairing.DeriveMaster(pub, pub, pairing.PublicKey); }
                    catch (CryptographicException) { return; }
                    pj = new PendingJoin
                    {
                        Master = master, JoinerPub = pub, Name = name,
                        PairOk = PairPacket("PAIROK", pairing.PublicKey, PairingSession.Confirm(master, "host"))
                    };
                    if (pending.Count > 8) pending.Clear();
                    pending[inst] = pj;
                }
                SendTo(pj.PairOk, from);
            }
            else if (role == PairRole.Host && type == "PAIRFIN")
            {
                if (pending.TryGetValue(inst, out var pj) &&
                    Crypto.FixedTimeEquals(conf, PairingSession.Confirm(pj.Master, "join")))
                    Finish(pj.Master, from, pj.Name);
            }
            else if (role == PairRole.Join && type == "PAIROK" && pub.Length > 0 && pub.Length < 200)
            {
                byte[] master;
                try { master = pairing.DeriveMaster(pub, pairing.PublicKey, pub); }
                catch (CryptographicException) { return; }
                // The host proves it knows the code; otherwise this answer is ignored.
                if (!Crypto.FixedTimeEquals(conf, PairingSession.Confirm(master, "host"))) return;
                var fin = PairPacket("PAIRFIN", new byte[0], PairingSession.Confirm(master, "join"));
                for (int i = 0; i < 3; i++) SendTo(fin, from); // UDP may lose one
                Finish(master, from, name);
            }
        }

        void Finish(byte[] master, IPEndPoint ep, string name)
        {
            Program.Trace("paired with " + name + " at " + ep);
            CancelPairing();
            keys = new SessionKeys(master);
            Interlocked.Exchange(ref lastSeenTicks, DateTime.UtcNow.Ticks);
            LastPeerName = name;
            Paired?.Invoke(master, ep, name);
            CheckStatus();
        }

        /// <summary>Forgets the couple's key (unpair).</summary>
        public void Forget()
        {
            keys = null;
            Interlocked.Exchange(ref lastSeenTicks, 0);
            CheckStatus();
        }

        // ───────────── receiving ─────────────

        void RxLoop()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (alive)
            {
                byte[] d;
                try { d = udp.Receive(ref from); }
                catch
                {
                    if (!alive) return;
                    Thread.Sleep(50);
                    continue;
                }
                try { Handle(d, new IPEndPoint(from.Address, from.Port)); } catch { }
            }
        }

        void Handle(byte[] d, IPEndPoint from)
        {
            if (d.Length > 2048 || d.Length < 6) return;

            if (d[0] == 'M' && d[1] == 'I' && d[2] == 'M' && d[3] == 'I')
            {
                if (d[4] == 'P')
                {
                    // Pairing packet (plain text, no secret inside).
                    var f = Encoding.ASCII.GetString(d).Split('|');
                    if (f.Length == 8 && f[4] != Instance) Post(() => HandlePairing(f, from));
                }
                else if (d[4] == '|') HandleLegacy(d, from);
                return;
            }

            var k = keys;
            if (k == null || !SecurePacket.LooksLike(d)) return;
            var m = SecurePacket.Open(k, d);
            if (m == null || m.Sender == Instance) return;          // not for us, tampered, or our own broadcast
            if (!replay.Accept(m, NowMs())) return;                 // replayed or too old

            Interlocked.Exchange(ref lastSeenTicks, DateTime.UtcNow.Ticks);
            string name = Clean(m.Name);
            if (name.Length > 0) LastPeerName = name;
            string who = LastPeerName.Length > 0 ? LastPeerName : L.T("Your.half");

            switch (m.Type)
            {
                case "PING":
                    SendTo(Seal("PONG", Dnd ? "dnd" : ""), from);
                    Post(() => PartnerDnd?.Invoke(m.Body == "dnd"));
                    break;
                case "PONG": Post(() => PartnerDnd?.Invoke(m.Body == "dnd")); break;
                case "IMG":
                case "IMGP":
                case "IMGN":
                case "IMGA":
                    Post(() => HandleImage(m.Type, m.Body ?? "", from, who));
                    break;
                case "HEART": { string th = ThemeOf(m.Body); Post(() => Heart?.Invoke(who, th)); break; }
                case "SYNC": { string th = ThemeOf(m.Body); Post(() => Sync?.Invoke(who, th)); break; }
                case "MSG":
                    string text = (m.Body ?? "").Trim();
                    if (text.Length > 160) text = text.Substring(0, 160);
                    if (text.Length > 0) Post(() => Note?.Invoke(who, text));
                    break;
                case "GIF":
                {
                    var g = (m.Body ?? "").Split(new[] { '|' }, 2);
                    string cap = g.Length > 1 ? g[1].Trim() : "";
                    if (cap.Length > 160) cap = cap.Substring(0, 160);
                    if (Giphy.IsValidId(g[0])) Post(() => GifReceived?.Invoke(who, g[0], cap));
                    break;
                }
                case "HOLD":
                    if (m.Body == "start" || m.Body.StartsWith("start;")) { string th = ThemeOf(m.Body); Post(() => Hold?.Invoke(who, true, 0, th)); }
                    else if (m.Body.StartsWith("end:") && int.TryParse(m.Body.Substring(4), out int ms))
                        Post(() => Hold?.Invoke(who, false, ms, ""));
                    break;
            }

            Post(() =>
            {
                // The partner speaks the new protocol: no need for the old one anymore.
                if (Legacy) { Program.Trace("partner speaks the new protocol: legacy mode off"); LegacyRetired?.Invoke(); }
                FollowAddress(from);
                CheckStatus();
            });
        }

        /// <summary>The partner's address changed? Follow it (only authenticated packets get here).</summary>
        void FollowAddress(IPEndPoint from)
        {
            IPEndPoint known = null;
            try { known = Resolve(cfg.Peer); } catch { }
            if (known == null || !known.Equals(from))
            {
                lock (this) { cachedEp = null; }
                PeerMoved?.Invoke(from);
            }
        }

        void CheckStatus()
        {
            bool on = Online;
            if (on != reportedOnline)
            {
                reportedOnline = on;
                StatusChanged?.Invoke();
            }
        }

        void Post(Action a)
        {
            try { if (ui.IsHandleCreated) ui.BeginInvoke(a); } catch { }
        }

        static string Clean(string s)
        {
            s = (s ?? "").Replace("|", " ").Replace('\u001f', ' ').Trim();
            return s.Length > 24 ? s.Substring(0, 24) : s;
        }

        public static string LocalAddresses()
        {
            try
            {
                var ips = Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Select(a => a.ToString()).Distinct().Take(3).ToArray();
                return ips.Length > 0 ? string.Join("  ·  ", ips) : "?";
            }
            catch { return "?"; }
        }

        public void Dispose()
        {
            alive = false;
            pinger.Stop(); pinger.Dispose();
            pairTimer.Stop(); pairTimer.Dispose();
            imageTimer.Stop(); imageTimer.Dispose();
            pairing?.Dispose();
            try { udp?.Close(); } catch { }
            udp = null;
        }
    }
}
