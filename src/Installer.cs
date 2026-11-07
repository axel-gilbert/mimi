using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Mimi
{
    /// <summary>
    /// Mimi installs itself: run Mimi.exe and it copies itself to
    /// %LOCALAPPDATA%\Programs\Mimi, adds itself to Windows startup, creates a Start menu
    /// shortcut and an "Installed apps" entry for uninstalling.
    /// No administrator rights required (except, optionally, for the firewall rule).
    /// </summary>
    static class Installer
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mimi";
        const string FirewallRule = "Mimi";

        public static string InstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Mimi");
        public static string InstalledExe => Path.Combine(InstallDir, "Mimi.exe");
        static string CurrentExe => Application.ExecutablePath;
        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mimi.lnk");
        static string RunValueName => Program.Profile == "default" ? "Mimi" : "Mimi_" + Program.Profile;
        static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static bool IsRunningInstalled() =>
            string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        public static bool IsInstalled => File.Exists(InstalledExe);

        // ───────────── autostart ─────────────

        public static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k?.GetValue(RunValueName) != null;
        }

        public static void SetAutostart(bool on, string exe = null)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (!on) { k.DeleteValue(RunValueName, false); return; }
                string args = " --autostart";
                if (Program.Profile != "default") args += " --profile " + Program.Profile;
                if (Program.Portable) args += " --portable";
                k.SetValue(RunValueName, "\"" + (exe ?? CurrentExe) + "\"" + args);
            }
        }

        // ───────────── installation ─────────────

        /// <summary>Offers to install. Returns true if this process should exit.</summary>
        public static bool OfferInstall()
        {
            string msg = IsInstalled ? L.T("install.already") : L.T("install.ask");
            var r = MessageBox.Show(msg, "Mimi", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return true;
            if (r == DialogResult.No) return false;
            try
            {
                Install();
            }
            catch (Exception e)
            {
                MessageBox.Show(L.F("install.failed", e.Message), "Mimi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return true;
            }
            Process.Start(InstalledExe, "--welcome");
            return true;
        }

        /// <summary>After a self-update: refresh the version shown in Windows "Installed apps".</summary>
        public static void RefreshVersion()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true))
                    k?.SetValue("DisplayVersion", Version);
            }
            catch { }
        }

        static void Install()
        {
            StopRunning("default");
            Directory.CreateDirectory(InstallDir);
            for (int i = 0; ; i++)
            {
                try { File.Copy(CurrentExe, InstalledExe, true); break; }
                catch (IOException) when (i < 15) { Thread.Sleep(300); }
            }

            SetAutostart(true, InstalledExe);
            CreateShortcut(StartMenuLink, InstalledExe);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Mimi");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Mimi");
                k.SetValue("DisplayIcon", InstalledExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }

            // Firewall rule (inbound UDP for Mimi.exe). Optional: if declined, Windows will ask on first launch.
            RunElevated("/c netsh advfirewall firewall delete rule name=\"" + FirewallRule + "\" >nul 2>&1 & " +
                        "netsh advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=in action=allow " +
                        "program=\"" + InstalledExe + "\" protocol=UDP enable=yes profile=any");
        }

        static void RunElevated(string cmdArgs)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                p?.WaitForExit(15000);
            }
            catch (Win32Exception) { /* the user declined: no big deal */ }
        }

        static void CreateShortcut(string lnkPath, string target)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { L.T("shortcut.desc") });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch { }
        }

        /// <summary>Politely asks an already running instance to close, then waits.</summary>
        static void StopRunning(string profile)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting("Mimi_" + profile + "_quit", out var quit))
                    using (quit) quit.Set();
                for (int i = 0; i < 25; i++)
                {
                    if (!Mutex.TryOpenExisting("Mimi_" + profile, out var m)) return;
                    m.Dispose();
                    Thread.Sleep(200);
                }
            }
            catch { }
        }

        // ───────────── uninstallation ─────────────

        /// <summary>Returns true if the uninstall took place.</summary>
        public static bool Uninstall(bool fromRunningApp)
        {
            if (MessageBox.Show(L.T("uninstall.ask"), "Mimi",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            if (!fromRunningApp) StopRunning("default");

            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null)
                        foreach (var v in k.GetValueNames())
                            if (v == "Mimi" || v.StartsWith("Mimi_")) k.DeleteValue(v, false);
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                if (File.Exists(StartMenuLink)) File.Delete(StartMenuLink);
                var cfgRoot = Path.GetDirectoryName(Config.Dir);
                if (Directory.Exists(cfgRoot)) Directory.Delete(cfgRoot, true);
            }
            catch { }

            RunElevated("/c netsh advfirewall firewall delete rule name=\"" + FirewallRule + "\"");

            MessageBox.Show(L.T("uninstall.done"), "Mimi", MessageBoxButtons.OK, MessageBoxIcon.Information);

            if (Directory.Exists(InstallDir))
            {
                // We can't delete ourselves: a small cmd takes care of it 2 s after we close.
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }

            return true;
        }
    }
}
