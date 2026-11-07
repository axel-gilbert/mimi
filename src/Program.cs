using System;
using System.Threading;
using System.Windows.Forms;

namespace Mimi
{
    static class Program
    {
        /// <summary>Profil de réglages (permet de lancer 2 Mimi sur le même PC pour tester).</summary>
        public static string Profile = "default";
        public static bool Portable;
        /// <summary>Options de test (appairage automatique) : --pair-host 123456 / --pair-join 123456</summary>
        public static string TestPairHost, TestPairJoin;

        [STAThread]
        static void Main(string[] args)
        {
            bool welcome = false, demo = false, uninstall = false, updated = false;
            string exportIcon = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--profile" when i + 1 < args.Length:
                        Profile = SafeName(args[++i]);
                        break;
                    case "--portable": Portable = true; break;
                    case "--welcome": welcome = true; break;
                    case "--demo": demo = true; break;
                    case "--updated": updated = true; break;
                    case "--uninstall": uninstall = true; break;
                    case "--export-icon" when i + 1 < args.Length: exportIcon = args[++i]; break;
                    case "--pair-host" when i + 1 < args.Length: TestPairHost = args[++i]; break;
                    case "--pair-join" when i + 1 < args.Length: TestPairJoin = args[++i]; break;
                }
            }
            if (Profile != "default") Portable = true;

            // Mimi passe toujours après tes jeux et applis : jamais de lag.
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }

            // Any error is logged to %APPDATA%\Mimi\<profile>\errors.log instead of crashing the app.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            L.Code = Config.Load().Lang;

            if (exportIcon != null) { Art.Init(); Art.ExportIco(exportIcon); return; }
            if (uninstall) { Installer.Uninstall(false); return; }

            if (!Portable && !Installer.IsRunningInstalled())
            {
                if (Installer.OfferInstall()) return;
                Portable = true;
            }

            using (var mutex = new Mutex(true, "Mimi_" + Profile, out bool first))
            {
                if (!first && updated)
                {
                    // Just updated: wait for the previous version to finish closing.
                    try { first = mutex.WaitOne(15000); }
                    catch (AbandonedMutexException) { first = true; }
                }
                if (!first)
                {
                    // Déjà lancé : on demande à l'instance existante d'ouvrir ses réglages.
                    try { using (var h = EventWaitHandle.OpenExisting("Mimi_" + Profile + "_show")) h.Set(); } catch { }
                    return;
                }
                using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Mimi_" + Profile + "_show"))
                using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Mimi_" + Profile + "_quit"))
                {
                    Application.Run(new MimiApp(show, quit, welcome, demo, updated));
                }
                GC.KeepAlive(mutex);
            }
        }

        public static void LogError(Exception e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Config.Dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(Config.Dir, "errors.log"),
                    DateTime.Now.ToString("s") + "  " + e + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        static string SafeName(string s)
        {
            var chars = new System.Text.StringBuilder();
            foreach (var c in s) if (char.IsLetterOrDigit(c)) chars.Append(c);
            return chars.Length == 0 ? "default" : chars.ToString();
        }
    }
}
