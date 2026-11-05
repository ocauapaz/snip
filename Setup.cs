// SnipSetup: installs Snip for the current user, without admin rights, with a Start menu shortcut
// so Windows Search finds it. Snip.exe rides inside this exe as a resource; build.cmd builds both.
// /uninstall removes it (Settings > Apps runs that); /silent shows no dialogs.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    const string Name = "Snip";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Snip";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppKey = @"Software\Snip";

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
    static readonly string Exe = Path.Combine(Dir, "Snip.exe");
    static readonly string Uninstaller = Path.Combine(Dir, "Uninstall.exe");
    static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Snip.lnk");

    [STAThread]
    static int Main(string[] args)
    {
        bool silent = args.Contains("/silent", StringComparer.OrdinalIgnoreCase);
        bool uninstall = args.Contains("/uninstall", StringComparer.OrdinalIgnoreCase);
        Application.EnableVisualStyles();
        try
        {
            if (uninstall)
            {
                if (!silent && !Ask("Remove Snip from this PC?\n\nYour screenshots in Pictures\\Snip stay.")) return 1;
                Uninstall();
                if (!silent) MessageBox.Show("Snip was removed.", "Snip");
            }
            else
            {
                if (!silent && !Ask("Install Snip for this user?\n\nSnip goes to " + Dir + " and to the Start menu, so Windows Search finds it. " +
                    "If Snip is running, it closes and starts again.")) return 1;
                Install();
                if (!silent) MessageBox.Show("Snip is installed and running.\n\nPress Print Screen to capture, or search \"Snip\" in Start.", "Snip");
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            if (!silent) MessageBox.Show((uninstall ? "Snip could not be removed: " : "Snip could not be installed: ") + e.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    static bool Ask(string question)
    {
        return MessageBox.Show(question, "Snip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    static void Install()
    {
        StopSnip();
        Directory.CreateDirectory(Dir);
        using (Stream packed = Assembly.GetExecutingAssembly().GetManifestResourceStream("Snip.exe"))
        {
            if (packed == null) throw new InvalidOperationException("this setup has no Snip.exe inside: build it with build.cmd.");
            using (FileStream f = File.Create(Exe)) packed.CopyTo(f);
        }
        string self = Path.GetFullPath(Application.ExecutablePath);
        if (!string.Equals(self, Uninstaller, StringComparison.OrdinalIgnoreCase)) File.Copy(self, Uninstaller, true);
        MakeShortcut();

        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", Name);
            k.SetValue("DisplayIcon", Exe);
            k.SetValue("Publisher", "ocauapaz");
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", Quote(Uninstaller) + " /uninstall");
            k.SetValue("QuietUninstallString", Quote(Uninstaller) + " /uninstall /silent");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            long bytes = new FileInfo(Exe).Length + new FileInfo(Uninstaller).Length;
            k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
        }

        // Snip writes its own Run entry on start, so "Start with Windows" now points at the installed copy.
        Process.Start(Exe);
    }

    static void Uninstall()
    {
        StopSnip();
        File.Delete(Shortcut);
        using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (run != null) run.DeleteValue(Name, false);
        Registry.CurrentUser.DeleteSubKeyTree(AppKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (!Directory.Exists(Dir)) return;
        // This exe is usually Uninstall.exe in Dir, and Windows locks a running exe: cmd removes the folder after it ends.
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q " + Quote(Dir))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    // Snip runs as a single instance, so a running copy would keep the new one from starting and lock its exe.
    static void StopSnip()
    {
        foreach (Process p in Process.GetProcessesByName(Name))
            using (p)
            {
                if (p.HasExited) continue;
                p.Kill();
                p.WaitForExit(5000);
            }
    }

    static void MakeShortcut()
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell", true);
        object shell = Activator.CreateInstance(shellType);
        try
        {
            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Shortcut });
            try
            {
                Set(link, "TargetPath", Exe);
                Set(link, "WorkingDirectory", Dir);
                Set(link, "Description", "Screenshots, OCR, GIFs and videos");
                Set(link, "IconLocation", Exe + ",0");
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    static void Set(object com, string property, string value)
    {
        com.GetType().InvokeMember(property, BindingFlags.SetProperty, null, com, new object[] { value });
    }

    static string Quote(string path)
    {
        return "\"" + path + "\"";
    }
}
