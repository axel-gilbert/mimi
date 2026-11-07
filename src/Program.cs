using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

[assembly: InternalsVisibleTo("Mimi.Tests")]

namespace Mimi
{
    static class Program
    {
        /// <summary>Settings profile (lets you run two Mimi on the same PC for testing).</summary>
        public static string Profile = "default";
        public static bool Portable;
        /// <summary>Test options (automatic pairing): --pair-host 123456 / --pair-join 123456</summary>
        public static string TestPairHost, TestPairJoin;
        /// <summary>Opens the settings on this tab (0..4), for screenshots.</summary>
        public static int OpenTab = -1;
        /// <summary>Test driver: commands written to commands.txt in the profile folder are executed (tests only).</summary>
        public static bool TestDriver;

        [STAThread]
        static void Main(string[] args)
        {
            bool welcome = false, demo = false, uninstall = false, updated = false;
            string exportIcon = null, recordDemo = null;
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
                    case "--record-demo" when i + 1 < args.Length: recordDemo = args[++i]; break;
                    case "--test-driver": TestDriver = true; break;
                    case "--tab" when i + 1 < args.Length: int.TryParse(args[++i], out OpenTab); break;
                    case "--pair-host" when i + 1 < args.Length: TestPairHost = args[++i]; break;
                    case "--pair-join" when i + 1 < args.Length: TestPairJoin = args[++i]; break;
                }
            }
            if (Profile != "default") Portable = true;

            // Mimi always yields to your games and apps: it never makes the PC lag.
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }

            // Any error is logged to %APPDATA%\Mimi\<profile>\errors.log instead of crashing the app.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            L.Code = Config.Load().Lang;

            if (exportIcon != null) { Art.Init(); Art.ExportIco(exportIcon); return; }
            if (recordDemo != null) { DemoRecorder.Run(recordDemo); return; }
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
                    // Already running: ask the running instance to open its settings.
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

        static readonly bool tracing = Environment.GetEnvironmentVariable("MIMI_TRACE") == "1";

        /// <summary>Diagnostic trace (only when the MIMI_TRACE=1 environment variable is set).</summary>
        public static void Trace(string what)
        {
            if (!tracing) return;
            try
            {
                System.IO.Directory.CreateDirectory(Config.Dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(Config.Dir, "trace.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + what + Environment.NewLine);
            }
            catch { }
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
