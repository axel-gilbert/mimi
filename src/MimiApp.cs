using System;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>Le cœur de l'appli : icône près de l'horloge, raccourci, réseau, overlay, sons.</summary>
    sealed class MimiApp : ApplicationContext
    {
        public readonly Config Cfg;
        readonly Overlay overlay;
        readonly GlobalHotkey hotkey = new GlobalHotkey();
        readonly SoundBank sound;
        readonly NotifyIcon tray;
        readonly Icon iconOn, iconOff;
        readonly ToolStripMenuItem statusItem;
        PeerLink link;
        SettingsForm settings;

        int recvCombo;
        DateTime lastRecv = DateTime.MinValue, lastToast = DateTime.MinValue, lastSend = DateTime.MinValue;
        bool hotkeyPaused;

        public event Action StateChanged;

        public MimiApp(EventWaitHandle showEvt, EventWaitHandle quitEvt, bool welcome, bool demo)
        {
            Art.Init();
            GC.Collect();
            Cfg = Config.Load();

            overlay = new Overlay();
            var _ = overlay.Handle; // crée la fenêtre + le contexte de synchronisation UI
            overlay.Corner = Cfg.Corner;

            sound = new SoundBank { Volume = Cfg.Sound ? Cfg.Volume : 0 };
            iconOn = Art.MakeIcon(true);
            iconOff = Art.MakeIcon(false);

            var menu = new ContextMenuStrip { ShowImageMargin = false };
            var title = new ToolStripMenuItem("Mimi 💗") { Enabled = false, Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            statusItem = new ToolStripMenuItem("") { Enabled = false };
            menu.Items.Add(title);
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Envoyer un cœur", null, (s, e) => SendHeart());
            menu.Items.Add("Tester l'animation", null, (s, e) => Demo());
            menu.Items.Add("Réglages et appairage…", null, (s, e) => ShowSettings());
            menu.Items.Add(new ToolStripSeparator());
            if (Installer.IsRunningInstalled())
                menu.Items.Add("Désinstaller…", null, (s, e) => { if (Installer.Uninstall(true)) Quit(); });
            menu.Items.Add("Quitter", null, (s, e) => Quit());

            tray = new NotifyIcon { Icon = iconOff, Visible = true, Text = "Mimi", ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

            hotkey.Pressed += SendHeart;
            ApplyHotkey(true);
            StartLink();

            ThreadPool.RegisterWaitForSingleObject(showEvt, (s, t) => UI(ShowSettings), null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(quitEvt, (s, t) => UI(Quit), null, -1, false);

            UpdateStatus();
            if (welcome || !Cfg.Exists || !Cfg.IsPaired) UI(ShowSettings);
            if (demo) UI(Demo);
            if (Program.TestPairHost != null) UI(() => { ShowSettings(); settings.AutoPair(true, Program.TestPairHost); });
            if (Program.TestPairJoin != null) UI(() => { ShowSettings(); settings.AutoPair(false, Program.TestPairJoin); });
        }

        void UI(Action a)
        {
            try { overlay.BeginInvoke(a); } catch { }
        }

        // ───────────── réseau ─────────────

        void StartLink()
        {
            link?.Dispose();
            link = new PeerLink(Cfg, overlay);
            link.Heart += OnHeart;
            link.StatusChanged += UpdateStatus;
            link.Paired += OnPaired;
            link.PairTimeout += () => settings?.OnPairTimeout();
            link.PeerMoved += ep =>
            {
                Cfg.Peer = PeerLink.FormatPeer(ep);
                Cfg.Save();
                settings?.RefreshFromConfig();
            };
            link.Start();
            if (link.Error != null) Balloon("Problème réseau", link.Error, ToolTipIcon.Warning);
        }

        public PeerLink Link => link;
        public bool Online => link != null && link.Online;
        public string PeerDisplayName => Cfg.PeerName.Length > 0 ? Cfg.PeerName : (link?.LastPeerName ?? "");

        void OnPaired(string code, IPEndPoint ep, string name)
        {
            Cfg.PairCode = code;
            Cfg.Peer = PeerLink.FormatPeer(ep);
            Cfg.PeerName = name;
            Cfg.Save();
            UpdateStatus();
            settings?.OnPaired(name);
            // petite fête
            overlay.Wave(10);
            overlay.Toast("Appairé avec " + (name.Length > 0 ? name : "ta moitié") + " 💞");
            if (Cfg.Sound) { sound.Chime(); sound.Mega(); }
        }

        void UpdateStatus()
        {
            bool on = Online;
            tray.Icon = on ? iconOn : iconOff;
            string s;
            if (link?.Error != null) s = "Erreur réseau";
            else if (!Cfg.IsPaired) s = "Pas encore appairé";
            else if (on) s = "Connecté à " + PeerDisplayName;
            else s = "En attente de " + (PeerDisplayName.Length > 0 ? PeerDisplayName : "ta moitié") + "…";
            statusItem.Text = s;
            string tip = "Mimi — " + s;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (on && link.LastPeerName.Length > 0 && link.LastPeerName != Cfg.PeerName)
            {
                Cfg.PeerName = link.LastPeerName;
                Cfg.Save();
            }
            StateChanged?.Invoke();
        }

        // ───────────── cœurs ─────────────

        public void SendHeart()
        {
            var now = DateTime.UtcNow;
            if ((now - lastSend).TotalMilliseconds < 60) return; // anti-rafale (~16/s max)
            lastSend = now;
            link?.SendHeart();
            if (Cfg.Echo) overlay.Echo();
            if (Cfg.Sound) sound.Send();
            if (!Cfg.IsPaired && (now - lastToast).TotalSeconds > 6)
            {
                lastToast = now;
                overlay.Toast("Mimi n'est pas encore appairé");
            }
        }

        void OnHeart(string name)
        {
            var now = DateTime.UtcNow;
            bool fresh = (now - lastRecv).TotalSeconds > 1.6;
            recvCombo = fresh ? 1 : recvCombo + 1;
            lastRecv = now;

            overlay.Wave(recvCombo);
            if (fresh && (now - lastToast).TotalSeconds > 8)
            {
                lastToast = now;
                overlay.Toast(name + " t'envoie des cœurs");
            }
            if (Cfg.Sound)
            {
                if (fresh) sound.Chime(); else sound.Pop();
                if (Overlay.IsMega(recvCombo)) sound.Mega();
            }
        }

        /// <summary>Simule une rafale de cœurs reçus (pour tester sans l'autre PC).</summary>
        public void Demo()
        {
            int n = 0;
            var t = new System.Windows.Forms.Timer { Interval = 130 };
            t.Tick += (s, e) =>
            {
                OnHeart(Cfg.Name.Length > 0 ? Cfg.Name : "Toi");
                if (++n >= 14) { t.Stop(); t.Dispose(); }
            };
            t.Start();
        }

        // ───────────── raccourci ─────────────

        public bool ApplyHotkey(bool notify)
        {
            if (hotkeyPaused) return true;
            bool ok = hotkey.Set(Cfg.HotMods, (Keys)Cfg.HotKey);
            if (!ok && notify)
                Balloon("Raccourci indisponible",
                    GlobalHotkey.Describe(Cfg.HotMods, (Keys)Cfg.HotKey) + " est déjà utilisé par une autre appli. Choisis-en un autre dans les réglages.",
                    ToolTipIcon.Warning);
            return ok;
        }

        public void PauseHotkey() { hotkeyPaused = true; hotkey.Clear(); }
        public void ResumeHotkey() { hotkeyPaused = false; ApplyHotkey(false); }

        /// <summary>Applique les réglages de la fenêtre. Renvoie un message d'erreur ou null.</summary>
        public string Apply(Config n, bool autostart)
        {
            bool restartNet = n.Port != Cfg.Port || n.Peer != Cfg.Peer;
            Cfg.Name = n.Name.Trim().Length > 0 ? n.Name.Trim() : Environment.UserName;
            Cfg.Peer = n.Peer.Trim();
            Cfg.Port = n.Port;
            Cfg.HotMods = n.HotMods;
            Cfg.HotKey = n.HotKey;
            Cfg.Volume = n.Volume;
            Cfg.Sound = n.Sound;
            Cfg.Echo = n.Echo;
            Cfg.Corner = n.Corner;
            overlay.Corner = n.Corner;
            Cfg.Save();
            sound.Volume = Cfg.Sound ? Cfg.Volume : 0;
            try { Installer.SetAutostart(autostart); } catch { }
            if (restartNet) StartLink();
            UpdateStatus();
            if (link?.Error != null) return link.Error;
            hotkeyPaused = false;
            if (!ApplyHotkey(false)) return "Ce raccourci est déjà pris par une autre appli 😕";
            return null;
        }

        public void Unpair()
        {
            Cfg.PairCode = "";
            Cfg.Peer = "";
            Cfg.PeerName = "";
            Cfg.Save();
            StartLink();
            UpdateStatus();
        }

        public void PreviewSound() { if (sound.Volume > 0) sound.Chime(); }
        public void SetPreviewVolume(int v) { sound.Volume = v; }
        public void RestoreVolume() { sound.Volume = Cfg.Sound ? Cfg.Volume : 0; }

        // ───────────── fenêtres ─────────────

        public void ShowSettings()
        {
            if (settings == null || settings.IsDisposed)
            {
                settings = new SettingsForm(this, iconOn);
                settings.FormClosed += (s, e) => { link?.CancelPairing(); ResumeHotkey(); RestoreVolume(); settings = null; };
            }
            settings.Show();
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
        }

        void Balloon(string title, string text, ToolTipIcon icon)
        {
            tray.BalloonTipTitle = title;
            tray.BalloonTipText = text;
            tray.BalloonTipIcon = icon;
            tray.ShowBalloonTip(5000);
        }

        void Quit()
        {
            tray.Visible = false;
            settings?.Close();
            link?.Dispose();
            hotkey.Dispose();
            overlay.Dispose();
            tray.Dispose();
            ExitThread();
        }
    }
}
