// Snip: Print Screen opens a snip overlay. The selection goes to the clipboard
// as an image, or as text (Windows OCR, offline). Lives in the tray, starts with Windows.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, "Snip.SingleInstance", out first))
        {
            if (!first) return;
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: real pixels on every screen
            Application.EnableVisualStyles();
            Application.Run(new App());
        }
    }
}

sealed class App : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppKey = @"Software\Snip";
    const string RunName = "Snip";
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    const int VK_SNAPSHOT = 0x2C, VK_ESCAPE = 0x1B, VK_TAB = 0x09, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    readonly Overlay overlay = new Overlay();
    readonly Native.HookProc hookProc; // field, so the GC never collects the callback
    IntPtr hook;
    readonly NotifyIcon tray;
    readonly OcrEngine ocr = Ocr.CreateEngine();
    readonly System.Windows.Forms.Timer sweep = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };

    public App()
    {
        overlay.Captured += OnCaptured;
        IntPtr created = overlay.Handle; // BeginInvoke needs the handle before the first key
        hookProc = OnKey;
        // The hook gets its own thread: Windows silently removes a low-level hook whose callback is late once,
        // and the UI thread is busy for a moment on every capture (screen copy, clipboard, editor).
        int hookError = 0;
        using (var ready = new ManualResetEvent(false))
        {
            new Thread(() =>
            {
                hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(null), 0);
                hookError = Marshal.GetLastWin32Error();
                ready.Set();
                if (hook != IntPtr.Zero) Application.Run(); // the hook is called through this thread's message loop
            }) { IsBackground = true, Name = "Snip key hook" }.Start();
            ready.WaitOne();
        }
        if (hook == IntPtr.Zero)
        {
            MessageBox.Show("Snip could not register the Print Screen key (error " + hookError + ").", "Snip");
            Environment.Exit(1);
        }

        using (var k = Registry.CurrentUser.CreateSubKey(AppKey))
        {
            if (k.GetValue("Installed") == null) { SetStartup(true); k.SetValue("Installed", 1); }
        }
        if (StartupEnabled()) SetStartup(true); // keeps the path right if the exe moved
        OfferSnippingKeyOff();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Capture  (Print Screen)", null, delegate { StartDelayed(); });
        menu.Items.Add("Open screenshots folder", null, delegate { Directory.CreateDirectory(Store.Folder); Process.Start("explorer.exe", "\"" + Store.Folder + "\""); });
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += delegate { SetStartup(startup.Checked); };
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, delegate { ExitThread(); });
        tray = new NotifyIcon { Icon = Ui.MakeIcon((int)Math.Round(16 * Native.DpiAt(Screen.PrimaryScreen.Bounds.Location) / 96)), Text = "Snip: Print Screen", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) StartDelayed(); };

        // Screenshots in Pictures\Snip live for Store.Ttl; sweep now and every hour.
        Store.SweepInBackground();
        sweep.Tick += delegate { Store.SweepInBackground(); };
        sweep.Start();
    }

    // The tray menu fades out; wait so it is not in the capture.
    async void StartDelayed()
    {
        await Task.Delay(250);
        overlay.Begin();
    }

    IntPtr OnKey(int code, IntPtr msg, IntPtr data)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(data);
            int m = msg.ToInt32();
            bool down = m == WM_KEYDOWN || m == WM_SYSKEYDOWN;
            if (overlay.Open)
            {
                if (vk == VK_ESCAPE || vk == VK_TAB || vk == VK_SNAPSHOT)
                {
                    if (down && vk == VK_ESCAPE) overlay.BeginInvoke((Action)overlay.Cancel);
                    if (down && vk == VK_TAB) overlay.BeginInvoke((Action)overlay.ToggleMode);
                    return (IntPtr)1;
                }
            }
            // Alt+PrtSc and Win+PrtSc keep their Windows behavior.
            else if (vk == VK_SNAPSHOT && !Held(VK_MENU) && !Held(VK_LWIN) && !Held(VK_RWIN))
            {
                if (down) overlay.BeginInvoke((Action)overlay.Begin);
                return (IntPtr)1;
            }
        }
        return Native.CallNextHookEx(hook, code, msg, data);
    }

    static bool Held(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

    async void OnCaptured(Bitmap crop, bool text)
    {
        try
        {
            if (!text)
            {
                Clip.SetImage(crop);
                Store.SaveInBackground(crop.Clone(new Rectangle(Point.Empty, crop.Size), PixelFormat.Format32bppArgb), "");
                Toast.Pop("Screenshot copied", "Click to edit  ·  " + crop.Width + " × " + crop.Height, Toast.Kind.Image, crop);
                return;
            }
            if (ocr == null)
            {
                Toast.Pop("No OCR language", "Add a Windows language with text recognition.", Toast.Kind.Warn, null);
                return;
            }
            string found = (await Ocr.Read(ocr, crop)).Trim();
            if (found.Length == 0)
            {
                Toast.Pop("No text found", "Try a larger area or more contrast.", Toast.Kind.Warn, null);
                return;
            }
            Clipboard.SetDataObject(found, true, 5, 60);
            Toast.Pop("Text copied", Preview(found), Toast.Kind.Text, null);
        }
        catch (Exception ex)
        {
            Toast.Pop("Something went wrong", ex.Message, Toast.Kind.Warn, null);
        }
        finally
        {
            crop.Dispose();
        }
    }

    static string Preview(string s)
    {
        string flat = string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        return flat.Length > 140 ? flat.Substring(0, 140) + "…" : flat;
    }

    // Windows 11 can open its own Snipping Tool on Print Screen. Ask once before turning that off.
    static void OfferSnippingKeyOff()
    {
        const string keyboardKey = @"Control Panel\Keyboard", snipValue = "PrintScreenKeyForSnippingEnabled";
        using (var app = Registry.CurrentUser.CreateSubKey(AppKey))
        using (var kb = Registry.CurrentUser.OpenSubKey(keyboardKey, true))
        {
            if (app.GetValue("AskedSnippingKey") != null || kb == null) return;
            object current = kb.GetValue(snipValue); // absent = Windows default, which is on
            if (current is int && (int)current == 0) return;
            app.SetValue("AskedSnippingKey", 1);
            DialogResult answer = MessageBox.Show(
                "Windows opens the Snipping Tool when you press Print Screen, so it can open together with Snip.\n\n" +
                "Turn off this Windows option now?\n\n" +
                "To turn it on again: Settings > Accessibility > Keyboard > \"Use the Print screen key to open screen capture\". " +
                "If the Snipping Tool still opens, sign out and sign in again.",
                "Snip", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) kb.SetValue(snipValue, 0, RegistryValueKind.DWord);
        }
    }

    static bool StartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunName) != null;
    }

    static void SetStartup(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(RunName, false);
        }
    }

    protected override void ExitThreadCore()
    {
        sweep.Dispose();
        Native.UnhookWindowsHookEx(hook); // the hook thread is a background thread and ends with the process
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

// Pictures\Snip: every capture is saved there as a PNG and deleted after Ttl.
// Only files named "Snip *.png" are touched, so files the user puts in the folder stay.
static class Store
{
    // The local Pictures folder, never the Windows "Pictures" library: that one can be in OneDrive and upload every capture.
    public static readonly string Pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
    public static readonly string Folder = Path.Combine(Pictures, "Snip");
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(2);
    const string Prefix = "Snip ";

    public static bool IsExpired(string fileName, DateTime writtenUtc, DateTime nowUtc, TimeSpan ttl)
    {
        return fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && nowUtc - writtenUtc > ttl;
    }

    static string Save(Bitmap bmp, string suffix)
    {
        Directory.CreateDirectory(Folder);
        string stem = Prefix + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + suffix;
        string path = Path.Combine(Folder, stem + ".png");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(Folder, stem + " (" + n + ").png");
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    // Deletes expired screenshots; returns the ones Windows would not delete (open in another app).
    static string[] Sweep()
    {
        if (!Directory.Exists(Folder)) return new string[0];
        DateTime now = DateTime.UtcNow;
        var failed = new System.Collections.Generic.List<string>();
        foreach (FileInfo f in new DirectoryInfo(Folder).GetFiles("*.png"))
        {
            if (!IsExpired(f.Name, f.LastWriteTimeUtc, now, Ttl)) continue;
            try { f.Delete(); }
            catch (IOException) { failed.Add(f.Name); }                 // the next sweep tries again
            catch (UnauthorizedAccessException) { failed.Add(f.Name); }
        }
        return failed.ToArray();
    }

    // PNG encoding takes up to a few hundred ms on a 4K capture, so it runs off the UI thread.
    // Takes ownership of bmp. Failures show a card; they are never dropped.
    public static async void SaveInBackground(Bitmap bmp, string suffix)
    {
        try
        {
            string[] stuck = await Task.Run(() => { using (bmp) Save(bmp, suffix); return Sweep(); });
            ReportStuck(stuck);
        }
        catch (Exception ex)
        {
            Toast.Pop("Could not save to Pictures\\Snip", ex.Message, Toast.Kind.Warn, null);
        }
    }

    public static async void SweepInBackground()
    {
        try { ReportStuck(await Task.Run(() => Sweep())); }
        catch (Exception ex) { Toast.Pop("Could not clean Pictures\\Snip", ex.Message, Toast.Kind.Warn, null); }
    }

    static void ReportStuck(string[] stuck)
    {
        if (stuck.Length > 0)
            Toast.Pop("Could not delete " + stuck.Length + " old screenshot" + (stuck.Length == 1 ? "" : "s"), "Close the app that has " + stuck[0] + " open.", Toast.Kind.Warn, null);
    }
}

static class Clip
{
    public static void SetImage(Bitmap bmp)
    {
        using (var rgb = bmp.Clone(new Rectangle(Point.Empty, bmp.Size), PixelFormat.Format24bppRgb))
        {
            var data = new DataObject();
            data.SetImage(rgb);
            Clipboard.SetDataObject(data, true, 5, 60);
        }
    }
}

// Colors from the Windows app theme (light or dark).
sealed class Theme
{
    public static bool? Force { get; set; } // a preview harness can set it; the app follows Windows

    public bool Dark;
    public Color Surface, Canvas, Ink, Muted, Hairline, Edge, Selected, Hover, Accent;

    public static Theme Current()
    {
        bool dark;
        if (Force.HasValue) dark = Force.Value;
        else
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("AppsUseLightTheme"); // absent = light, the Windows default
                dark = v is int && (int)v == 0;
            }
        }
        return dark
            ? new Theme { Dark = true, Surface = Color.FromArgb(39, 39, 41), Canvas = Color.FromArgb(24, 24, 26), Ink = Color.White,
                          Muted = Color.FromArgb(161, 161, 166), Hairline = Color.FromArgb(28, 255, 255, 255), Edge = Color.FromArgb(62, 62, 66),
                          Selected = Color.FromArgb(40, 255, 255, 255), Hover = Color.FromArgb(18, 255, 255, 255), Accent = Color.FromArgb(41, 151, 255) }
            : new Theme { Dark = false, Surface = Color.FromArgb(245, 245, 247), Canvas = Color.FromArgb(232, 232, 237), Ink = Color.FromArgb(29, 29, 31),
                          Muted = Color.FromArgb(110, 110, 115), Hairline = Color.FromArgb(210, 210, 215), Edge = Color.FromArgb(210, 210, 215),
                          Selected = Color.FromArgb(20, 0, 0, 0), Hover = Color.FromArgb(10, 0, 0, 0), Accent = Color.FromArgb(0, 102, 204) };
    }
}

// Full-screen frozen copy of every monitor; drag to select, click to take the whole monitor.
sealed class Overlay : Form
{
    public event Action<Bitmap, bool> Captured;
    public volatile bool Open; // Visible, readable from the key hook thread

    protected override void OnVisibleChanged(EventArgs e)
    {
        Open = Visible;
        base.OnVisibleChanged(e);
    }

    bool textMode; // image capture is the main mode
    Bitmap shot, dim;
    Point start;
    Rectangle sel;
    bool dragging;
    int hover; // 0 none, 1 image button, 2 text button
    Rectangle bar, btnImage, btnText;
    float scale = 1;
    Theme theme;
    Font label, small;

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.Cross;
        Opacity = 0; // layered: Begin paints the new frame before it becomes visible
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // WS_EX_TOOLWINDOW: not in Alt+Tab
    }

    public void Begin()
    {
        if (Visible) return;
        Rectangle v = SystemInformation.VirtualScreen;
        shot = new Bitmap(v.Width, v.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(shot))
            g.CopyFromScreen(v.Location, Point.Empty, v.Size, CopyPixelOperation.SourceCopy); // DWM already composites layered windows; the enum check rejects CaptureBlt
        dim = new Bitmap(v.Width, v.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(dim))
        using (var shade = new SolidBrush(Color.FromArgb(96, 0, 0, 0)))
        {
            g.DrawImageUnscaled(shot, 0, 0);
            g.FillRectangle(shade, 0, 0, v.Width, v.Height);
        }
        theme = Theme.Current();
        LayoutBar(v);
        sel = Rectangle.Empty;
        dragging = false;
        hover = 0;
        Bounds = v;
        Show();
        Refresh();
        Opacity = 254 / 255.0; // stays layered, so the next Show never flashes the old frame
        Activate();
    }

    public void Cancel()
    {
        if (!Visible) return;
        Hide();
        Opacity = 0;
        dragging = false;
        shot.Dispose();
        dim.Dispose();
        shot = dim = null;
    }

    public void ToggleMode()
    {
        textMode = !textMode;
        Invalidate(bar);
    }

    void Finish(Rectangle r)
    {
        r.Intersect(new Rectangle(Point.Empty, shot.Size));
        if (r.Width < 1 || r.Height < 1) { Cancel(); return; }
        Bitmap crop = shot.Clone(r, PixelFormat.Format32bppArgb);
        bool text = textMode;
        Cancel();
        if (Captured != null) Captured(crop, text);
    }

    int S(float px) { return (int)Math.Round(px * scale); }

    // A floating bar at the bottom center of the monitor under the cursor.
    void LayoutBar(Rectangle v)
    {
        Point c = Cursor.Position;
        Rectangle area = Screen.FromPoint(c).WorkingArea;
        area.Offset(-v.X, -v.Y);
        scale = Native.DpiAt(c) / 96f;
        int pad = S(6), bw = S(104), bh = S(40), keys = S(180);
        bar = new Rectangle(0, 0, pad + bw + S(2) + bw + S(21) + keys, bh + 2 * pad);
        bar.Location = new Point(area.Left + (area.Width - bar.Width) / 2, area.Bottom - bar.Height - S(28));
        btnImage = new Rectangle(bar.X + pad, bar.Y + pad, bw, bh);
        btnText = new Rectangle(btnImage.Right + S(2), btnImage.Y, bw, bh);
        if (label != null) { label.Dispose(); small.Dispose(); }
        label = new Font(Ui.Text, 13 * scale, GraphicsUnit.Pixel);
        small = new Font(Ui.Text, 11.5f * scale, GraphicsUnit.Pixel);
    }

    Rectangle SizePill(Rectangle r)
    {
        if (r.Width == 0 && r.Height == 0) return Rectangle.Empty;
        int w = S(84), h = S(22), gap = S(8);
        int y = r.Bottom + gap + h <= ClientSize.Height ? r.Bottom + gap : r.Bottom - gap - h; // inside when there is no room below
        return new Rectangle(Math.Max(0, r.Right - w), y, w, h);
    }

    void SetSel(Rectangle r)
    {
        Rectangle grown = r;
        grown.Inflate(S(3), S(3));
        Rectangle dirty = Rectangle.Union(grown, SizePill(r));
        if (sel.Width > 0 || sel.Height > 0)
        {
            Rectangle old = sel;
            old.Inflate(S(3), S(3));
            dirty = Rectangle.Union(dirty, Rectangle.Union(old, SizePill(sel)));
        }
        sel = r;
        Invalidate(dirty);
    }

    static Rectangle FromPoints(Point a, Point b)
    {
        return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Cancel(); return; }
        if (e.Button != MouseButtons.Left) return;
        if (btnImage.Contains(e.Location)) { if (textMode) ToggleMode(); return; }
        if (btnText.Contains(e.Location)) { if (!textMode) ToggleMode(); return; }
        if (bar.Contains(e.Location)) return;
        dragging = true;
        start = e.Location;
        SetSel(new Rectangle(e.Location, Size.Empty));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) { SetSel(FromPoints(start, e.Location)); return; }
        int h = btnImage.Contains(e.Location) ? 1 : btnText.Contains(e.Location) ? 2 : 0;
        Cursor want = bar.Contains(e.Location) ? Cursors.Default : Cursors.Cross;
        if (Cursor != want) Cursor = want;
        if (h != hover) { hover = h; Invalidate(bar); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        dragging = false;
        Rectangle r = FromPoints(start, e.Location);
        if (r.Width < 4 || r.Height < 4)
        {
            r = Screen.FromPoint(Cursor.Position).Bounds; // a plain click takes the whole monitor
            r.Offset(-Bounds.X, -Bounds.Y);
        }
        Finish(r);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (dim == null) return;
        Graphics g = e.Graphics;
        Rectangle clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(dim, clip, clip, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        if (sel.Width > 0 && sel.Height > 0)
        {
            Rectangle lit = Rectangle.Intersect(sel, clip);
            if (!lit.IsEmpty) g.DrawImage(shot, lit, lit, GraphicsUnit.Pixel);
            // A dark hairline outside the white one keeps the edge visible on white pages.
            using (var outer = new Pen(Color.FromArgb(90, 0, 0, 0), 1))
                g.DrawRectangle(outer, sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
            using (var inner = new Pen(Color.White, Math.Max(1, S(1))))
                g.DrawRectangle(inner, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
            if (dragging) DrawSizePill(g);
        }
        if (clip.IntersectsWith(bar)) DrawBar(g);
    }

    void DrawSizePill(Graphics g)
    {
        Rectangle p = SizePill(sel);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var bg = new SolidBrush(Color.FromArgb(210, 29, 29, 31)))
        using (var path = Ui.Round(p, p.Height / 2f))
            g.FillPath(bg, path);
        g.DrawString(sel.Width + " × " + sel.Height, small, Brushes.White, p, Ui.Center);
        g.SmoothingMode = SmoothingMode.None;
    }

    void DrawBar(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var bg = new SolidBrush(theme.Surface))
        using (var edge = new Pen(theme.Hairline, 1))
        using (var path = Ui.Round(bar, S(14)))
        {
            g.FillPath(bg, path);
            g.DrawPath(edge, path);
        }
        DrawButton(g, btnImage, "Image", !textMode, hover == 1, false);
        DrawButton(g, btnText, "Text", textMode, hover == 2, true);

        int sepX = btnText.Right + S(10);
        using (var sep = new Pen(theme.Hairline, 1))
            g.DrawLine(sep, sepX, bar.Y + S(15), sepX, bar.Bottom - S(15));

        float x = sepX + S(14);
        x = DrawKey(g, "tab", "switch", x);
        DrawKey(g, "esc", "cancel", x + S(12));
        g.SmoothingMode = SmoothingMode.None;
    }

    void DrawButton(Graphics g, Rectangle r, string text, bool on, bool hot, bool textGlyph)
    {
        if (on || hot)
        {
            using (var b = new SolidBrush(on ? theme.Selected : theme.Hover))
            using (var p = Ui.Round(r, S(9)))
                g.FillPath(b, p);
        }
        Color fg = on ? theme.Ink : theme.Muted;
        float icon = S(18);
        SizeF ts = g.MeasureString(text, label, PointF.Empty, StringFormat.GenericTypographic);
        float left = r.X + (r.Width - icon - S(8) - ts.Width) / 2;
        var box = new RectangleF(left, r.Y + (r.Height - icon) / 2, icon, icon);
        if (textGlyph) Ui.DrawTextGlyph(g, box, fg, Math.Max(1.4f, 1.5f * scale));
        else Ui.DrawImageGlyph(g, box, fg, Math.Max(1.4f, 1.5f * scale));
        using (var b = new SolidBrush(fg))
            g.DrawString(text, label, b, left + icon + S(8), r.Y + (r.Height - ts.Height) / 2, StringFormat.GenericTypographic);
    }

    // A keycap hint: [tab] switch
    float DrawKey(Graphics g, string key, string action, float x)
    {
        SizeF ks = g.MeasureString(key, small, PointF.Empty, StringFormat.GenericTypographic);
        float h = S(20), w = ks.Width + S(12), y = bar.Y + (bar.Height - h) / 2;
        var cap = new RectangleF(x, y, w, h);
        using (var edge = new Pen(theme.Hairline, 1))
        using (var path = Ui.Round(cap, S(5)))
            g.DrawPath(edge, path);
        using (var b = new SolidBrush(theme.Muted))
        {
            g.DrawString(key, small, b, cap, Ui.Center);
            SizeF a = g.MeasureString(action, small, PointF.Empty, StringFormat.GenericTypographic);
            g.DrawString(action, small, b, x + w + S(6), y + (h - a.Height) / 2, StringFormat.GenericTypographic);
            return x + w + S(6) + a.Width;
        }
    }
}

// Card at the bottom right: thumbnail or icon tile, title, detail.
// A screenshot card opens the editor on click and waits while the mouse is on it. Never takes focus.
sealed class Toast : Form
{
    public enum Kind { Image, Text, Saved, Warn }

    const int FadeInMs = 140, FadeOutMs = 220, TickMs = 15;
    static Toast current;

    readonly string title, detail;
    readonly Kind kind;
    readonly int holdMs;
    readonly Bitmap thumb;
    Bitmap full; // Image cards: the capture, handed to the editor on click
    readonly Theme theme = Theme.Current();
    readonly Font bold, body;
    readonly float s;
    readonly Rectangle tile;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = TickMs };
    int elapsed;
    bool hovered;

    public static void Pop(string title, string detail, Kind kind, Bitmap source)
    {
        if (current != null) current.Close();
        current = new Toast(title, detail, kind, source);
        current.Show();
    }

    Toast(string title, string detail, Kind kind, Bitmap source)
    {
        this.title = title;
        this.detail = detail;
        this.kind = kind;
        holdMs = kind == Kind.Image ? 5000 : 2400;
        Point c = Cursor.Position;
        s = Native.DpiAt(c) / 96f;
        bold = new Font(Ui.TextBold, 13.5f * s, GraphicsUnit.Pixel);
        body = new Font(Ui.Text, 12.5f * s, GraphicsUnit.Pixel);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = theme.Surface;
        Opacity = 0;
        Cursor = kind == Kind.Image ? Cursors.Hand : Cursors.Default;

        int pad = Px(12), w = Px(356), h = Px(80);
        tile = kind == Kind.Image
            ? new Rectangle(pad, pad, Px(74), h - 2 * pad)
            : new Rectangle(pad, (h - Px(44)) / 2, Px(44), Px(44));
        if (source != null)
        {
            thumb = MakeThumb(source, tile.Size);
            if (kind == Kind.Image) full = source.Clone(new Rectangle(Point.Empty, source.Size), PixelFormat.Format32bppPArgb);
        }
        Rectangle wa = Screen.FromPoint(c).WorkingArea;
        Bounds = new Rectangle(wa.Right - w - Px(16), wa.Bottom - h - Px(16), w, h);

        timer.Tick += delegate { Step(); };
        timer.Start();
    }

    int Px(float v) { return (int)Math.Round(v * s); }

    // Contain-fit copy of the capture on the tile background.
    Bitmap MakeThumb(Bitmap src, Size box)
    {
        var bmp = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppArgb);
        float k = Math.Min(box.Width / (float)src.Width, box.Height / (float)src.Height);
        float tw = src.Width * k, th = src.Height * k;
        using (var g = Graphics.FromImage(bmp))
        using (var bg = new SolidBrush(theme.Dark ? Color.FromArgb(29, 29, 31) : Color.FromArgb(232, 232, 237)))
        {
            g.FillRectangle(bg, 0, 0, box.Width, box.Height);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, (box.Width - tw) / 2, (box.Height - th) / 2, tw, th);
        }
        return bmp;
    }

    void Step()
    {
        if (hovered && elapsed >= FadeInMs) { elapsed = FadeInMs; Opacity = 0.99; return; } // wait while the mouse is on the card
        elapsed += TickMs;
        if (elapsed <= FadeInMs) Opacity = 0.99 * elapsed / FadeInMs;
        else if (elapsed <= FadeInMs + holdMs) Opacity = 0.99;
        else if (elapsed <= FadeInMs + holdMs + FadeOutMs) Opacity = 0.99 * (1 - (elapsed - FadeInMs - holdMs) / (double)FadeOutMs);
        else Close();
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        if (full != null)
        {
            var editor = new Editor(full); // the editor owns the bitmap now
            full = null;
            editor.Show();
            editor.Activate();
        }
        Close();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x8; // NOACTIVATE | TOOLWINDOW | TOPMOST
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
        int border = ColorTranslator.ToWin32(theme.Edge); // DWMWA_BORDER_COLOR
        Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Round(tile, Px(kind == Kind.Image ? 6 : 10)))
        {
            if (thumb != null)
            {
                using (var tex = new TextureBrush(thumb))
                {
                    tex.TranslateTransform(tile.X, tile.Y);
                    g.FillPath(tex, path);
                }
                using (var edge = new Pen(theme.Hairline, 1)) g.DrawPath(edge, path);
            }
            else
            {
                Color fill = kind == Kind.Warn ? Color.FromArgb(255, 159, 10) : kind == Kind.Saved ? Color.FromArgb(52, 199, 89) : theme.Accent;
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                RectangleF glyph = new RectangleF(tile.X + tile.Width * 0.27f, tile.Y + tile.Height * 0.27f, tile.Width * 0.46f, tile.Height * 0.46f);
                if (kind == Kind.Text) Ui.DrawTextGlyph(g, glyph, Color.White, 2f * s);
                else if (kind == Kind.Saved) Ui.DrawCheckGlyph(g, glyph, Color.White, 2.2f * s);
                else Ui.DrawWarnGlyph(g, glyph, Color.White, 2.2f * s);
            }
        }
        int x = tile.Right + Px(12), right = Width - Px(14);
        int th = TextRenderer.MeasureText(title, bold).Height;
        int top = (Height - th - Px(36)) / 2;
        var one = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, title, bold, new Rectangle(x, top, right - x, th), theme.Ink, theme.Surface, one);
        var two = TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        TextRenderer.DrawText(g, detail, body, new Rectangle(x, top + th + Px(1), right - x, Px(36)), theme.Muted, theme.Surface, two);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (current == this) current = null;
        timer.Dispose();
        bold.Dispose();
        body.Dispose();
        if (thumb != null) thumb.Dispose();
        if (full != null) full.Dispose();
        base.OnFormClosed(e);
    }
}

static class Ocr
{
    // pt-BR reads English too, while en-US turns "ação" into "agäo". Without pt-BR: the Windows language list.
    const string Preferred = "pt-BR";

    public static OcrEngine CreateEngine()
    {
        var pt = new Windows.Globalization.Language(Preferred);
        return OcrEngine.IsLanguageSupported(pt) ? OcrEngine.TryCreateFromLanguage(pt) : OcrEngine.TryCreateFromUserProfileLanguages();
    }

    // Windows OCR misreads small screen text and drops glyphs that touch the edge:
    // scale up to 2x and add a margin in the background color.
    public static async Task<string> Read(OcrEngine engine, Bitmap bmp)
    {
        const int margin = 16;
        double s = Math.Min(2.0, ((int)OcrEngine.MaxImageDimension - 2 * margin) / (double)Math.Max(bmp.Width, bmp.Height));
        int w = (int)(bmp.Width * s) + 2 * margin, h = (int)(bmp.Height * s) + 2 * margin;
        byte[] pixels = new byte[w * h * 4];
        using (var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(bmp.GetPixel(0, 0));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(bmp, margin, margin, w - 2 * margin, h - 2 * margin);
            }
            BitmapData data = canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            canvas.UnlockBits(data);
        }
        using (var sb = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h))
        {
            OcrResult result = await engine.RecognizeAsync(sb);
            return string.Join("\r\n", result.Lines.Select(l => l.Text));
        }
    }
}

static class Ui
{
    // Segoe UI Variable is the Windows 11 font closest to SF Pro; Windows 10 has only Segoe UI.
    public static readonly string Text = Pick("Segoe UI Variable Text", "Segoe UI");
    public static readonly string TextBold = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    public static readonly StringFormat Center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    public static readonly Icon AppIcon = MakeIcon(32);

    static string Pick(string preferred, string other)
    {
        return FontFamily.Families.Any(f => f.Name == preferred) ? preferred : other;
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Photo: frame, sun, mountain.
    public static void DrawImageGlyph(Graphics g, RectangleF b, Color c, float stroke)
    {
        float u = b.Width / 18f;
        using (var pen = new Pen(c, stroke) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var frame = Round(new RectangleF(b.X + u, b.Y + 2.5f * u, 16 * u, 13 * u), 2.5f * u))
        using (var fill = new SolidBrush(c))
        {
            g.DrawPath(pen, frame);
            g.FillEllipse(fill, b.X + 11.5f * u, b.Y + 5 * u, 3 * u, 3 * u);
            g.DrawLines(pen, new[] {
                new PointF(b.X + 2 * u, b.Y + 14 * u), new PointF(b.X + 7 * u, b.Y + 9 * u),
                new PointF(b.X + 11 * u, b.Y + 13 * u), new PointF(b.X + 13 * u, b.Y + 11 * u), new PointF(b.X + 16 * u, b.Y + 14 * u) });
        }
    }

    // Text: a paragraph of three lines.
    public static void DrawTextGlyph(Graphics g, RectangleF b, Color c, float stroke)
    {
        float u = b.Width / 18f;
        using (var pen = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(pen, b.X + 2 * u, b.Y + 4.5f * u, b.X + 16 * u, b.Y + 4.5f * u);
            g.DrawLine(pen, b.X + 2 * u, b.Y + 9 * u, b.X + 16 * u, b.Y + 9 * u);
            g.DrawLine(pen, b.X + 2 * u, b.Y + 13.5f * u, b.X + 11 * u, b.Y + 13.5f * u);
        }
    }

    // Warning: exclamation mark.
    public static void DrawWarnGlyph(Graphics g, RectangleF b, Color c, float stroke)
    {
        float u = b.Width / 18f;
        using (var pen = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var fill = new SolidBrush(c))
        {
            g.DrawLine(pen, b.X + 9 * u, b.Y + 2.5f * u, b.X + 9 * u, b.Y + 11 * u);
            g.FillEllipse(fill, b.X + 7.5f * u, b.Y + 13.5f * u, 3 * u, 3 * u);
        }
    }

    // Done: check mark.
    public static void DrawCheckGlyph(Graphics g, RectangleF b, Color c, float stroke)
    {
        float u = b.Width / 18f;
        using (var pen = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, new[] { new PointF(b.X + 3 * u, b.Y + 9.5f * u), new PointF(b.X + 7.5f * u, b.Y + 14 * u), new PointF(b.X + 15.5f * u, b.Y + 4.5f * u) });
    }

    // The Snip logo: a white crop mark on a graphite squircle, on a 32 x 32 grid.
    // The single source for the tray, the windows and snip.ico (IconGen.cs).
    // At tray sizes the tile is lighter and the stroke thicker, so it reads on a dark taskbar.
    public static void DrawLogo(Graphics g, int size)
    {
        bool small = size <= 24;
        float k = size / 32f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var tile = Round(new RectangleF(1 * k, 1 * k, 30 * k, 30 * k), 8 * k))
        using (var bg = new SolidBrush(small ? Color.FromArgb(58, 58, 60) : Color.FromArgb(29, 29, 31)))
        using (var edge = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1, k)))
        using (var pen = new Pen(Color.White, (small ? 3.5f : 2.6f) * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.FillPath(bg, tile);
            if (!small) g.DrawPath(edge, tile); // keeps the tile visible on dark backgrounds
            g.DrawLines(pen, new[] { new PointF(11 * k, 6 * k), new PointF(11 * k, 21 * k), new PointF(26 * k, 21 * k) });
            g.DrawLines(pen, new[] { new PointF(6 * k, 11 * k), new PointF(21 * k, 11 * k), new PointF(21 * k, 26 * k) });
        }
    }

    public static Icon MakeIcon(int size)
    {
        using (var bmp = new Bitmap(size, size))
        {
            using (var g = Graphics.FromImage(bmp)) DrawLogo(g, size);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}

static class Native
{
    public delegate IntPtr HookProc(int code, IntPtr msg, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr data);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    public static float DpiAt(Point p)
    {
        uint x, y;
        var pt = new POINT { X = p.X, Y = p.Y };
        Marshal.ThrowExceptionForHR(GetDpiForMonitor(MonitorFromPoint(pt, 2), 0, out x, out y));
        return x;
    }
}
