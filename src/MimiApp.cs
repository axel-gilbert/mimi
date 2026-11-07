using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>
    /// The heart of the app: tray icon, shortcuts, network, overlay, sounds, statistics,
    /// "do not disturb" and updates are wired together here.
    /// </summary>
    sealed class MimiApp : ApplicationContext
    {
        static readonly TimeSpan SyncWindow = TimeSpan.FromSeconds(1.0);   // both press within 1 s = sync
        static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(350); // longer press = big heart
        public static readonly int[] DndMinutes = { 10, 30, 60, 120, 180, 240, 360, 720, 1440 };

        public readonly Config Cfg;
        public readonly Stats Stats;
        readonly Overlay overlay;
        readonly GlobalHotkey heartKey = new GlobalHotkey(), messageKey = new GlobalHotkey();
        readonly SoundBank sound;
        readonly NotifyIcon tray;
        readonly Icon iconOn, iconOff, iconDnd;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 20000 };
        readonly System.Windows.Forms.Timer holdPoll = new System.Windows.Forms.Timer { Interval = 25 };
        readonly System.Windows.Forms.Timer slowTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        ToolStripMenuItem statusItem, dndMenu;
        PeerLink link;
        SettingsForm settings;
        MessageBar bar;

        int recvCombo, missed;
        DateTime lastRecv = DateTime.MinValue, lastToast = DateTime.MinValue, lastSend = DateTime.MinValue,
                 lastSync = DateTime.MinValue, lastPartnerDndToast = DateTime.MinValue;
        bool hotkeysPaused, updating, holdTracking, holdActive, partnerDnd;
        DateTime holdStart;
        Version failedUpdate;
        readonly Queue<Action> waitingForDndEnd = new Queue<Action>();

        public event Action StateChanged;

        /// <summary>The look of my hearts.</summary>
        public Art.Theme MyTheme => Art.GetTheme(Cfg.Theme);
        string partnerTheme = "";
        /// <summary>The look of my partner's hearts on my screen (theirs, unless turned off).</summary>
        Art.Theme PartnerTheme => Cfg.PartnerColors && partnerTheme.Length > 0 ? Art.GetTheme(partnerTheme) : MyTheme;
        void NotePartnerTheme(string th) { if (!string.IsNullOrEmpty(th)) partnerTheme = th; }

        public MimiApp(EventWaitHandle showEvt, EventWaitHandle quitEvt, bool welcome, bool demo, bool updated)
        {
            Art.Init();
            GC.Collect();
            Cfg = Config.Load();
            L.Code = Cfg.Lang;
            Stats = Stats.Load(Path.Combine(Config.Dir, "stats.ini"));

            overlay = new Overlay();
            var _ = overlay.Handle; // creates the window + the UI synchronization context
            overlay.Corner = Cfg.Corner;

            sound = new SoundBank { Volume = Cfg.Sound ? Cfg.Volume : 0 };
            iconOn = Art.MakeIcon(true);
            iconOff = Art.MakeIcon(false);
            using (var b = Art.HeartBitmap(32, Color.FromArgb(214, 186, 255), Color.FromArgb(130, 90, 210), false, 0.94f, false))
                iconDnd = Icon.FromHandle(b.GetHicon());

            tray = new NotifyIcon { Icon = iconOff, Visible = true, Text = "Mimi" };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
            BuildMenu();

            heartKey.Pressed += OnHeartKey;
            messageKey.Pressed += ShowMessageBar;
            ApplyHotkeys(true);
            holdPoll.Tick += (s, e) => PollHold();
            StartLink();

            ThreadPool.RegisterWaitForSingleObject(showEvt, (s, t) => UI(ShowSettings), null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(quitEvt, (s, t) => UI(Quit), null, -1, false);

            // Automatic updates: first check 20 s after start, then every 6 hours.
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = 6 * 3600 * 1000;
                if (Cfg.AutoUpdate && Program.Profile == "default") CheckForUpdate(false);
            };
            updateTimer.Start();

            // Every 15 s: save statistics, end "do not disturb" when its time is up.
            slowTimer.Tick += (s, e) =>
            {
                Stats.Save();
                if (!Dnd && link != null && link.Dnd) EndDnd();
            };
            slowTimer.Start();
            // Windows shutting down / logging off: keep the statistics.
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => { Stats.Save(); Cfg.Save(); };
            if (link != null) link.Dnd = Dnd;

            if (updated)
            {
                Updater.CleanupOld();
                if (Installer.IsRunningInstalled()) Installer.RefreshVersion();
                UI(() =>
                {
                    overlay.Toast(L.F("upd.done", Updater.CurrentText));
                    if (Cfg.Sound) sound.Chime();
                });
            }

            // A couple paired with an older version gets the stronger key from now on; save it.
            if (Cfg.LegacyCode.Length > 0) Cfg.Save();

            UpdateStatus();
            if (welcome || !Cfg.Exists || !Cfg.IsPaired || Program.OpenTab >= 0) UI(ShowSettings);
            if (Program.OpenTab >= 0) UI(() => settings?.SelectTab(Program.OpenTab));
            if (Program.TestDriver) StartTestDriver();
            if (demo) UI(DemoHearts);
            if (Program.TestPairHost != null) UI(() => { ShowSettings(); settings.AutoPair(true, Program.TestPairHost); });
            if (Program.TestPairJoin != null) UI(() => { ShowSettings(); settings.AutoPair(false, Program.TestPairJoin); });
        }

        void UI(Action a)
        {
            try { overlay.BeginInvoke(a); } catch { }
        }

        // ───────────── tray menu ─────────────

        void BuildMenu()
        {
            var old = tray.ContextMenuStrip;
            var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false };
            var title = new ToolStripMenuItem("Mimi " + Updater.CurrentText + " 💗") { Enabled = false, Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            statusItem = new ToolStripMenuItem("") { Enabled = false };
            menu.Items.Add(title);
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("menu.send"), null, (s, e) => SendHeart());
            menu.Items.Add(L.T("menu.message"), null, (s, e) => ShowMessageBar());

            dndMenu = new ToolStripMenuItem(L.T("menu.dnd"));
            var dd = (ToolStripDropDownMenu)dndMenu.DropDown;
            dd.ShowImageMargin = false;
            dd.ShowCheckMargin = true;
            dndMenu.DropDownOpening += (s, e) => RefreshDndMenu();
            menu.Items.Add(dndMenu);
            RefreshDndMenu();

            menu.Items.Add(L.T("menu.settings"), null, (s, e) => ShowSettings());
            menu.Items.Add(L.T("menu.test"), null, (s, e) => DemoHearts());
            menu.Items.Add(L.T("menu.update"), null, (s, e) => CheckForUpdate(true));
            menu.Items.Add(new ToolStripSeparator());
            if (Installer.IsRunningInstalled())
                menu.Items.Add(L.T("menu.uninstall"), null, (s, e) => { if (Installer.Uninstall(true)) Quit(); });
            menu.Items.Add(L.T("menu.quit"), null, (s, e) => Quit());
            tray.ContextMenuStrip = menu;
            old?.Dispose();
        }

        public static string DurationText(int minutes) =>
            minutes < 60 ? minutes + " min" : (minutes / 60) + " h";

        void RefreshDndMenu()
        {
            dndMenu.DropDownItems.Clear();
            bool on = Dnd, forever = on && Cfg.DndUntil == DateTime.MaxValue;
            dndMenu.DropDownItems.Add(new ToolStripMenuItem(L.T("dnd.off"), null, (s, e) => SetDnd(null)) { Checked = !on });
            dndMenu.DropDownItems.Add(new ToolStripMenuItem(L.T("dnd.on"), null, (s, e) => SetDnd(TimeSpan.MaxValue)) { Checked = forever });
            dndMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (int m in DndMinutes)
            {
                int minutes = m;
                dndMenu.DropDownItems.Add(L.F("dnd.for", DurationText(minutes)), null, (s, e) => SetDnd(TimeSpan.FromMinutes(minutes)));
            }
            dndMenu.Checked = on;
        }

        // ───────────── do not disturb ─────────────

        public bool Dnd => DateTime.UtcNow < Cfg.DndUntil;

        /// <summary>null = off, TimeSpan.MaxValue = until turned off, otherwise for that long.</summary>
        public void SetDnd(TimeSpan? duration)
        {
            if (duration == null)
            {
                Cfg.DndUntil = DateTime.MinValue;
                Cfg.Save();
                EndDnd();
                return;
            }
            bool wasOn = Dnd;
            Cfg.DndUntil = duration.Value == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.UtcNow + duration.Value;
            Cfg.Save();
            if (!wasOn) missed = 0;
            if (link != null) link.Dnd = true;
            UpdateStatus();
        }

        /// <summary>"Do not disturb" is over: show what happened meanwhile.</summary>
        void EndDnd()
        {
            if (link != null) link.Dnd = false;
            if (missed > 0)
            {
                overlay.Wave(Math.Min(missed, 12), PartnerTheme);
                overlay.Toast(missed == 1 ? L.T("toast.dnd.end.one") : L.F("toast.dnd.end", missed));
                if (Cfg.Sound) sound.Chime();
            }
            missed = 0;
            while (waitingForDndEnd.Count > 0) waitingForDndEnd.Dequeue()();
            UpdateStatus();
        }

        /// <summary>Runs now, or after "do not disturb" ends (at most 10 things are kept).</summary>
        void ShowOrKeep(Action show)
        {
            if (!Dnd) { show(); return; }
            if (waitingForDndEnd.Count < 10) waitingForDndEnd.Enqueue(show);
        }

        // ───────────── network ─────────────

        void StartLink()
        {
            link?.Dispose();
            link = new PeerLink(Cfg, overlay) { Dnd = Dnd };
            link.Heart += OnHeart;
            link.Hold += OnHold;
            link.Sync += (name, th) => { NotePartnerTheme(th); if (DateTime.UtcNow - lastSync > TimeSpan.FromSeconds(4)) TriggerSync(false); };
            link.Note += OnNote;
            link.Picture += OnPicture;
            link.GifReceived += OnGif;
            link.StatusChanged += UpdateStatus;
            link.Paired += OnPaired;
            link.PairTimeout += () => settings?.OnPairTimeout();
            link.PartnerDnd += on => { if (on != partnerDnd) { partnerDnd = on; UpdateStatus(); } };
            link.LegacyRetired += () => { Cfg.LegacyCode = ""; Cfg.Save(); };
            link.PeerMoved += ep =>
            {
                Cfg.Peer = PeerLink.FormatPeer(ep);
                Cfg.Save();
                settings?.RefreshFromConfig();
            };
            link.Start();
            if (link.Error != null)
            {
                Balloon(L.T("net.problem"), link.Error, ToolTipIcon.Warning);
                // try again a bit later: the port may just be busy for a moment
                var retry = new System.Windows.Forms.Timer { Interval = 20000 };
                var mine = link;
                retry.Tick += (s, e) =>
                {
                    retry.Stop(); retry.Dispose();
                    if (link == mine && link.Error != null) { StartLink(); UpdateStatus(); }
                };
                retry.Start();
            }
        }

        public PeerLink Link => link;
        public bool Online => link != null && link.Online;
        public string PeerDisplayName => Cfg.PeerName.Length > 0 ? Cfg.PeerName : (link?.LastPeerName ?? "");
        string Who => PeerDisplayName.Length > 0 ? PeerDisplayName : L.T("your.half");

        void OnPaired(byte[] master, IPEndPoint ep, string name)
        {
            Cfg.Key = master;
            Cfg.LegacyCode = "";
            Cfg.WeakKey = false;
            Cfg.Peer = PeerLink.FormatPeer(ep);
            Cfg.PeerName = name;
            Cfg.PairedSince = DateTime.Now.ToString("yyyy-MM-dd");
            Cfg.Save();
            UpdateStatus();
            settings?.OnPaired(name);
            overlay.Wave(10);
            overlay.Toast(L.F("toast.paired", name.Length > 0 ? name : L.T("your.half")));
            if (Cfg.Sound) { sound.Chime(); sound.Mega(); }
        }

        void UpdateStatus()
        {
            bool on = Online;
            tray.Icon = Dnd ? iconDnd : on ? iconOn : iconOff;
            string s;
            if (link?.Error != null) s = L.T("status.neterr");
            else if (Dnd) s = Cfg.DndUntil == DateTime.MaxValue ? L.T("status.dnd.forever") : L.F("status.dnd", Cfg.DndUntil.ToLocalTime().ToString("HH:mm"));
            else if (!Cfg.IsPaired) s = L.T("status.unpaired");
            else if (on && partnerDnd) s = L.F("status.partner.dnd", Who);
            else if (on) s = L.F("status.online", PeerDisplayName);
            else s = L.F("status.waiting", Who);
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

        public string StatusText => statusItem.Text;

        // ───────────── hearts: tap, hold, sync ─────────────

        /// <summary>
        /// Heart shortcut pressed. A tap sends a heart right away. If the key is still held after 350 ms,
        /// a big heart starts growing on both screens until the key is released. Keyboard auto-repeat
        /// is ignored, so holding the key never spams.
        /// </summary>
        void OnHeartKey()
        {
            if (holdTracking) return;
            holdTracking = true;
            holdActive = false;
            holdStart = DateTime.UtcNow;
            SendHeart();
            holdPoll.Start();
        }

        void PollHold()
        {
            bool down = (Native.GetAsyncKeyState(Cfg.HotKey) & 0x8000) != 0;
            var held = DateTime.UtcNow - holdStart;
            if (down && held < TimeSpan.FromSeconds(8.2))
            {
                if (!holdActive && held > HoldDelay)
                {
                    holdActive = true;
                    link?.SendHold(true, 0, Cfg.Theme);
                    if (Cfg.Echo) overlay.HoldStart("local", 0.9f, MyTheme);
                    Stats.BigHeart();
                }
                return;
            }
            holdPoll.Stop();
            holdTracking = false;
            if (holdActive)
            {
                holdActive = false;
                link?.SendHold(false, (int)held.TotalMilliseconds, Cfg.Theme);
                overlay.HoldEnd("local");
            }
        }

        public void SendHeart()
        {
            var now = DateTime.UtcNow;
            if ((now - lastSend).TotalMilliseconds < 60) return; // burst limit (~16/s max)
            lastSend = now;
            link?.SendHeart(Cfg.Theme);
            if (Cfg.Echo) overlay.Echo(MyTheme);
            if (Cfg.Sound) sound.Send();
            if (!Cfg.IsPaired) { NotPairedToast(); return; }
            Stats.HeartSent();
            if (partnerDnd && (now - lastPartnerDndToast).TotalSeconds > 60)
            {
                lastPartnerDndToast = now;
                overlay.Toast(L.F("toast.partner.dnd", Who));
            }
            if (now - lastRecv < SyncWindow && now - lastSync > TimeSpan.FromSeconds(4)) TriggerSync(true);
        }

        void NotPairedToast()
        {
            var now = DateTime.UtcNow;
            if ((now - lastToast).TotalSeconds < 6) return;
            lastToast = now;
            overlay.Toast(L.T("toast.notpaired"));
        }

        void OnHeart(string name, string theme)
        {
            NotePartnerTheme(theme);
            var now = DateTime.UtcNow;
            Stats.HeartReceived();
            bool fresh = (now - lastRecv).TotalSeconds > 1.6;
            lastRecv = now;
            if (Dnd) { missed++; return; }
            recvCombo = fresh ? 1 : recvCombo + 1;

            overlay.Wave(recvCombo, PartnerTheme);
            if (fresh && (now - lastToast).TotalSeconds > 8)
            {
                lastToast = now;
                overlay.Toast(L.F("toast.sends", name));
            }
            if (Cfg.Sound)
            {
                if (fresh) sound.Chime(); else sound.Pop();
                if (Overlay.IsMega(recvCombo)) sound.Mega();
            }
            if (now - lastSend < SyncWindow && now - lastSync > TimeSpan.FromSeconds(4)) TriggerSync(true);
        }

        void OnHold(string name, bool start, int ms, string theme)
        {
            NotePartnerTheme(theme);
            if (Dnd) { if (start) missed++; return; }
            if (start)
            {
                overlay.HoldStart("remote", 1f, PartnerTheme);
                if (Cfg.Sound) sound.Pop();
            }
            else
            {
                overlay.HoldEnd("remote");
                if (Cfg.Sound) sound.Mega();
            }
        }

        /// <summary>Both pressed at (almost) the same time. Tells the partner too, in case they didn't notice.</summary>
        void TriggerSync(bool tellPartner)
        {
            lastSync = DateTime.UtcNow;
            if (tellPartner) link?.SendSync(Cfg.Theme);
            Stats.Sync();
            ShowOrKeep(() =>
            {
                overlay.SyncBurst(MyTheme, PartnerTheme);
                overlay.Toast(L.T("toast.sync"));
                if (Cfg.Sound) sound.Mega();
            });
        }

        // ───────────── messages & pictures ─────────────

        void ShowMessageBar()
        {
            if (!Cfg.IsPaired) { NotPairedToast(); return; }
            if (bar != null && !bar.IsDisposed) { bar.Activate(); return; }
            bar = new MessageBar(iconOn, Giphy.ApiKey(Cfg));
            bar.Sent += SendFromBar;
            bar.SentGif += (text, id) =>
            {
                link?.SendGif(id, text);
                Stats.PictureSent();
                overlay.Toast(L.T("toast.sent.gif"));
                if (Cfg.Sound) sound.Send();
            };
            bar.FormClosed += (s, e) => bar = null;
            bar.Show();
        }

        void SendFromBar(string text, Image picture)
        {
            if (picture != null)
            {
                try
                {
                    var data = ImageCodec.Prepare(picture);
                    link?.SendPicture(data, text);
                    Stats.PictureSent();
                    overlay.Toast(L.T("toast.sent.pic"));
                }
                catch (Exception e)
                {
                    Program.LogError(e);
                    overlay.Toast(L.T("toast.pic.failed"));
                }
                finally { picture.Dispose(); }
            }
            else
            {
                link?.SendNote(text);
                Stats.NoteSent();
                overlay.Toast(L.T("toast.sent.note"));
            }
            if (Cfg.Sound) sound.Send();
            if (partnerDnd) overlay.Toast(L.F("toast.partner.dnd", Who));
        }

        void OnNote(string name, string text)
        {
            Stats.NoteReceived();
            ShowOrKeep(() =>
            {
                overlay.Note(name, text);
                if (Cfg.Sound) sound.Chime();
            });
        }

        void OnPicture(string name, byte[] data, string caption)
        {
            Stats.PictureReceived();
            ShowOrKeep(() =>
            {
                var bmp = ImageCodec.Decode(data);
                if (bmp == null) return;
                overlay.Picture(name, bmp, caption);
                if (Cfg.Sound) sound.Chime();
            });
        }

        /// <summary>A GIF was received: download it from GIPHY in the background, then show it.</summary>
        void OnGif(string name, string id, string caption)
        {
            Stats.PictureReceived();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Giphy.Animation anim = null;
                try { anim = Giphy.Decode(Giphy.DownloadById(id)); }
                catch (Exception e) { Program.LogError(e); }
                if (anim == null || anim.Frames.Length == 0) return;
                UI(() => ShowOrKeep(() =>
                {
                    overlay.Animated(name, anim, caption);
                    if (Cfg.Sound) sound.Chime();
                }));
            });
        }

        // ───────────── test driver (--test-driver only) ─────────────

        /// <summary>
        /// For automated end-to-end tests: commands written to commands.txt in the profile folder are run
        /// as if the user had used the shortcuts. Only active with the --test-driver option.
        /// Commands: heart, hold:ms, note:text, picture:path|caption, gif:id|caption, dnd:minutes|on|off, theme:id
        /// </summary>
        void StartTestDriver()
        {
            string file = Path.Combine(Config.Dir, "commands.txt");
            var t = new System.Windows.Forms.Timer { Interval = 150 };
            t.Tick += (s, e) =>
            {
                if (!File.Exists(file)) return;
                string[] lines;
                try { lines = File.ReadAllLines(file); File.Delete(file); } catch { return; }
                foreach (var line in lines) RunTestCommand(line.Trim());
            };
            t.Start();
        }

        void RunTestCommand(string line)
        {
            Program.Trace("test command: " + line);
            int i = line.IndexOf(':');
            string cmd = i < 0 ? line : line.Substring(0, i), arg = i < 0 ? "" : line.Substring(i + 1);
            switch (cmd)
            {
                case "heart": SendHeart(); break;
                case "hold":
                    link?.SendHold(true, 0, Cfg.Theme);
                    overlay.HoldStart("local", 0.9f, MyTheme);
                    int ms = int.TryParse(arg, out int v) ? v : 2000;
                    var t = new System.Windows.Forms.Timer { Interval = ms };
                    t.Tick += (s, e) => { t.Stop(); t.Dispose(); link?.SendHold(false, ms, Cfg.Theme); overlay.HoldEnd("local"); };
                    t.Start();
                    break;
                case "note": SendFromBar(arg, null); break;
                case "picture":
                {
                    var f = arg.Split(new[] { '|' }, 2);
                    SendFromBar(f.Length > 1 ? f[1] : "", Image.FromFile(f[0]));
                    break;
                }
                case "gif":
                {
                    var f = arg.Split(new[] { '|' }, 2);
                    link?.SendGif(f[0], f.Length > 1 ? f[1] : "");
                    break;
                }
                case "dnd":
                    if (arg == "off") SetDnd(null);
                    else if (arg == "on") SetDnd(TimeSpan.MaxValue);
                    else if (int.TryParse(arg, out int m)) SetDnd(TimeSpan.FromMinutes(m));
                    break;
                case "theme": Cfg.Theme = Art.NormalizeTheme(arg); break;
            }
        }

        // ───────────── previews (settings & tray) ─────────────

        public void DemoHearts()
        {
            int n = 0;
            var t = new System.Windows.Forms.Timer { Interval = 130 };
            t.Tick += (s, e) =>
            {
                recvCombo = n == 0 ? 1 : recvCombo + 1;
                overlay.Wave(recvCombo, MyTheme);
                if (Cfg.Sound) { if (n == 0) sound.Chime(); else sound.Pop(); }
                if (++n >= 14) { t.Stop(); t.Dispose(); }
            };
            t.Start();
        }

        /// <summary>Shows a few hearts in the given theme (settings preview).</summary>
        public void PreviewTheme(string themeId)
        {
            var t = Art.GetTheme(themeId);
            for (int i = 0; i < 3; i++) overlay.Wave(1 + i * 2, t);
            if (Cfg.Sound) sound.Pop();
        }

        public void DemoBig()
        {
            overlay.HoldStart("remote", 1f, MyTheme);
            if (Cfg.Sound) sound.Pop();
            var t = new System.Windows.Forms.Timer { Interval = 2600 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); overlay.HoldEnd("remote"); if (Cfg.Sound) sound.Mega(); };
            t.Start();
        }

        public void DemoSync()
        {
            overlay.SyncBurst(MyTheme, PartnerTheme);
            overlay.Toast(L.T("toast.sync"));
            if (Cfg.Sound) sound.Mega();
        }

        public void DemoNote()
        {
            overlay.Note(Cfg.Name.Length > 0 ? Cfg.Name : "Mimi", L.T("demo.note"));
            if (Cfg.Sound) sound.Chime();
        }

        // ───────────── updates ─────────────

        public void CheckForUpdate(bool manual)
        {
            if (updating) return;
            updating = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.UpdateInfo info = null;
                string file = null;
                Exception error = null;
                try
                {
                    info = Updater.Check();
                    if (info != null && (manual || !info.Version.Equals(failedUpdate)))
                        file = Updater.Download(info);
                }
                catch (Exception e) { error = e; }
                UI(() => OnUpdateChecked(manual, info, file, error));
            });
        }

        void OnUpdateChecked(bool manual, Updater.UpdateInfo info, string file, Exception error)
        {
            updating = false;
            if (error != null)
            {
                Program.LogError(error);
                if (manual) Balloon(L.T("upd.title"), L.F("upd.failed", error.Message), ToolTipIcon.Warning);
                return;
            }
            if (info == null)
            {
                if (manual) Balloon(L.T("upd.title"), L.F("upd.uptodate", Updater.CurrentText), ToolTipIcon.Info);
                return;
            }
            if (file == null) return;
            try
            {
                Updater.ApplyAndRestart(file);
                Quit();
            }
            catch (Exception e)
            {
                failedUpdate = info.Version;
                Program.LogError(e);
                if (manual) Balloon(L.T("upd.title"), L.F("upd.failed", e.Message), ToolTipIcon.Warning);
            }
        }

        // ───────────── shortcuts ─────────────

        public bool ApplyHotkeys(bool notify)
        {
            if (hotkeysPaused) return true;
            bool ok1 = heartKey.Set(Cfg.HotMods, (Keys)Cfg.HotKey);
            bool ok2 = messageKey.Set(Cfg.MsgMods, (Keys)Cfg.MsgKey);
            if (notify)
            {
                if (!ok1) Balloon(L.T("hk.unavailable.title"), L.F("hk.unavailable", GlobalHotkey.Describe(Cfg.HotMods, (Keys)Cfg.HotKey)), ToolTipIcon.Warning);
                else if (!ok2) Balloon(L.T("hk.unavailable.title"), L.F("hk.unavailable", GlobalHotkey.Describe(Cfg.MsgMods, (Keys)Cfg.MsgKey)), ToolTipIcon.Warning);
            }
            return ok1 && ok2;
        }

        public void PauseHotkeys() { hotkeysPaused = true; heartKey.Clear(); messageKey.Clear(); }
        public void ResumeHotkeys() { hotkeysPaused = false; ApplyHotkeys(false); }

        /// <summary>Applies the settings window values. Returns an error message or null.</summary>
        public string Apply(Config n, bool autostart)
        {
            bool restartNet = n.Port != Cfg.Port || n.Peer != Cfg.Peer;
            bool langChanged = n.Lang != Cfg.Lang;
            Cfg.Name = n.Name.Trim().Length > 0 ? n.Name.Trim() : Environment.UserName;
            Cfg.Peer = n.Peer.Trim();
            Cfg.Port = n.Port;
            Cfg.HotMods = n.HotMods;
            Cfg.HotKey = n.HotKey;
            Cfg.MsgMods = n.MsgMods;
            Cfg.MsgKey = n.MsgKey;
            Cfg.Volume = n.Volume;
            Cfg.Sound = n.Sound;
            Cfg.Echo = n.Echo;
            Cfg.Corner = n.Corner;
            Cfg.AutoUpdate = n.AutoUpdate;
            Cfg.Lang = n.Lang;
            Cfg.GiphyKey = n.GiphyKey ?? "";
            Cfg.Theme = Art.NormalizeTheme(n.Theme);
            Cfg.PartnerColors = n.PartnerColors;
            overlay.Corner = n.Corner;
            Cfg.Save();
            sound.Volume = Cfg.Sound ? Cfg.Volume : 0;
            try { Installer.SetAutostart(autostart); } catch { }
            if (langChanged)
            {
                L.Code = Cfg.Lang;
                BuildMenu();
            }
            if (restartNet) StartLink();
            UpdateStatus();
            if (link?.Error != null) return link.Error;
            hotkeysPaused = false;
            if (!ApplyHotkeys(false)) return L.T("hk.taken");
            if (langChanged) UI(ReopenSettings);
            return null;
        }

        void ReopenSettings()
        {
            settings?.Close();
            ShowSettings();
        }

        public void Unpair()
        {
            Cfg.Key = null;
            Cfg.LegacyCode = "";
            Cfg.WeakKey = false;
            Cfg.Peer = "";
            Cfg.PeerName = "";
            Cfg.PairedSince = "";
            Cfg.Save();
            StartLink();
            UpdateStatus();
        }

        public void PreviewSound() { if (sound.Volume > 0) sound.Chime(); }
        public void SetPreviewVolume(int v) { sound.Volume = v; }
        public void RestoreVolume() { sound.Volume = Cfg.Sound ? Cfg.Volume : 0; }

        // ───────────── windows ─────────────

        public void ShowSettings()
        {
            if (settings == null || settings.IsDisposed)
            {
                var f = new SettingsForm(this, iconOn);
                f.FormClosed += (s, e) =>
                {
                    link?.CancelPairing();
                    ResumeHotkeys();
                    RestoreVolume();
                    if (settings == f) settings = null;
                };
                settings = f;
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
            updateTimer.Stop();
            slowTimer.Stop();
            holdPoll.Stop();
            Stats.Save();
            tray.Visible = false;
            settings?.Close();
            bar?.Close();
            link?.Dispose();
            heartKey.Dispose();
            messageKey.Dispose();
            overlay.Dispose();
            tray.Dispose();
            ExitThread();
        }
    }
}
