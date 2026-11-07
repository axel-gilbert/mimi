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
    /// Liaison pair-à-pair en UDP, sans serveur.
    ///
    /// Paquets texte : MIMI|1|&lt;tag&gt;|&lt;TYPE&gt;|&lt;prénom&gt;|&lt;instance&gt;
    ///   tag = empreinte SHA-256 du code du couple (le code lui-même ne circule jamais).
    ///   TYPE = HEART | PING | PONG | PAIR | PAIROK
    ///
    /// Appairage : un PC "crée un code" (6 chiffres) et écoute ; l'autre "rejoint" en diffusant
    /// (broadcast) un PAIR sur le réseau local. Le premier répond PAIROK ; chacun enregistre
    /// l'adresse de l'autre. Ensuite, si l'adresse IP change, les PING diffusés permettent
    /// de se retrouver tout seuls.
    /// </summary>
    sealed class PeerLink : IDisposable
    {
        const int SIO_UDP_CONNRESET = -1744830452;
        static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(20);

        readonly Config cfg;
        readonly Control ui;
        readonly System.Windows.Forms.Timer pinger = new System.Windows.Forms.Timer { Interval = 6000 };
        readonly System.Windows.Forms.Timer pairTimer = new System.Windows.Forms.Timer { Interval = 700 };
        UdpClient udp;
        volatile bool alive;
        bool reportedOnline;
        long lastSeenTicks;
        IPEndPoint cachedEp; string cachedFor; DateTime cachedAt;

        // appairage
        enum PairRole { None, Host, Join }
        PairRole role = PairRole.None;
        string pairCode;
        DateTime pairUntil;

        /// <summary>Identifiant aléatoire de cette instance, pour ignorer nos propres broadcasts.</summary>
        static readonly string Instance = Guid.NewGuid().ToString("N").Substring(0, 10);

        public string Error { get; private set; }
        public string LastPeerName = "";

        public event Action<string> Heart;                         // prénom de l'expéditeur
        public event Action StatusChanged;
        public event Action<string, IPEndPoint, string> Paired;    // code, adresse, prénom
        public event Action PairTimeout;
        public event Action<IPEndPoint> PeerMoved;

        public PeerLink(Config cfg, Control ui)
        {
            this.cfg = cfg;
            this.ui = ui;
            pinger.Tick += (s, e) => { Ping(); CheckStatus(); };
            pairTimer.Tick += (s, e) => PairTick();
        }

        public bool Online => DateTime.UtcNow.Ticks - Interlocked.Read(ref lastSeenTicks) < OnlineWindow.Ticks;
        public bool IsPairing => role != PairRole.None;

        public static string Tag(string secret)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes("mimi:" + (secret ?? "").Trim()));
                return BitConverter.ToString(h, 0, 8).Replace("-", "");
            }
        }

        string CoupleTag => Tag(cfg.PairCode);

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
                try { udp?.Close(); } catch { }
                udp = null;
                return;
            }
            alive = true;
            new Thread(RxLoop) { IsBackground = true, Name = "mimi-rx" }.Start();
            pinger.Start();
            Ping();
        }

        // ───────────── envoi ─────────────

        public void SendHeart() => SendToPeer("HEART");

        void Ping()
        {
            if (!cfg.IsPaired) return;
            SendToPeer("PING");
            // Hors ligne ? On crie aussi sur le réseau local, au cas où l'IP aurait changé.
            if (!Online) Broadcast(Packet(CoupleTag, "PING"));
        }

        byte[] Packet(string tag, string type) =>
            Encoding.UTF8.GetBytes("MIMI|1|" + tag + "|" + type + "|" + Clean(cfg.Name) + "|" + Instance);

        void SendToPeer(string type)
        {
            var u = udp;
            if (u == null || !cfg.IsPaired) return;
            var data = Packet(CoupleTag, type);
            string peer = cfg.Peer;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var ep = Resolve(peer);
                    if (ep != null) u.Send(data, data.Length, ep);
                }
                catch { }
            });
        }

        void SendTo(byte[] data, IPEndPoint ep)
        {
            try { udp?.Send(data, data.Length, ep); } catch { }
        }

        void Broadcast(byte[] data)
        {
            var u = udp;
            if (u == null) return;
            int[] ports = { Config.DefaultPort, Config.DefaultPort + 1 };
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
                targets.AddRange(DirectedBroadcasts());
                foreach (var ip in targets.Distinct())
                    foreach (var port in ports)
                    {
                        if (ip.Equals(IPAddress.Loopback) && port == cfg.Port) continue;
                        try { u.Send(data, data.Length, new IPEndPoint(ip, port)); } catch { }
                    }
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

        // ───────────── appairage ─────────────

        public static string NewCode()
        {
            var b = new byte[4];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return (BitConverter.ToUInt32(b, 0) % 1000000).ToString("000000");
        }

        public static string NormalizeCode(string s) =>
            new string((s ?? "").Where(char.IsDigit).ToArray());

        /// <summary>Ce PC affiche un code et attend que l'autre le tape (5 minutes).</summary>
        public void HostPairing(string code)
        {
            role = PairRole.Host;
            pairCode = code;
            pairUntil = DateTime.UtcNow.AddMinutes(5);
            pairTimer.Start();
        }

        /// <summary>Ce PC cherche sur le réseau local celui qui affiche ce code (20 secondes).</summary>
        public void JoinPairing(string code)
        {
            role = PairRole.Join;
            pairCode = code;
            pairUntil = DateTime.UtcNow.AddSeconds(20);
            pairTimer.Start();
            PairTick();
        }

        public void CancelPairing()
        {
            role = PairRole.None;
            pairTimer.Stop();
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
            if (role == PairRole.Join) Broadcast(Packet(Tag("pair:" + pairCode), "PAIR"));
        }

        void FinishPairing(string code, IPEndPoint ep, string name)
        {
            if (role == PairRole.None || code != pairCode) return;
            CancelPairing();
            Interlocked.Exchange(ref lastSeenTicks, DateTime.UtcNow.Ticks);
            Paired?.Invoke(code, ep, name);
            CheckStatus();
        }

        // ───────────── réception ─────────────

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
            if (d.Length > 300) return;
            var f = Encoding.UTF8.GetString(d).Split('|');
            if (f.Length < 6 || f[0] != "MIMI" || f[5] == Instance) return;
            string tag = f[2], type = f[3], name = Clean(f[4]);

            // Messages d'appairage
            if (type == "PAIR" || type == "PAIROK")
            {
                Post(() =>
                {
                    string code = pairCode;
                    bool matchesPairing = role != PairRole.None && code != null && tag == Tag("pair:" + code);
                    bool matchesCouple = cfg.IsPaired && tag == Tag("pair:" + cfg.PairCode);
                    if (type == "PAIR" && (role == PairRole.Host && matchesPairing || matchesCouple))
                    {
                        SendTo(Packet(tag, "PAIROK"), from);
                        if (role == PairRole.Host && matchesPairing) FinishPairing(code, from, name);
                    }
                    else if (type == "PAIROK" && role == PairRole.Join && matchesPairing)
                        FinishPairing(code, from, name);
                });
                return;
            }

            if (!cfg.IsPaired || tag != CoupleTag) return;
            Interlocked.Exchange(ref lastSeenTicks, DateTime.UtcNow.Ticks);
            if (name.Length > 0) LastPeerName = name;

            if (type == "PING") SendTo(Packet(CoupleTag, "PONG"), from);
            else if (type == "HEART") Post(() => Heart?.Invoke(LastPeerName.Length > 0 ? LastPeerName : L.T("Your.half")));

            Post(() =>
            {
                // Ta moitié a changé d'adresse ? On la suit.
                IPEndPoint known = null;
                try { known = Resolve(cfg.Peer); } catch { }
                if (known == null || !known.Equals(from))
                {
                    lock (this) { cachedEp = null; }
                    PeerMoved?.Invoke(from);
                }
                CheckStatus();
            });
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
            s = (s ?? "").Replace("|", " ").Trim();
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
            try { udp?.Close(); } catch { }
            udp = null;
        }
    }
}
