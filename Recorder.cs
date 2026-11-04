// GIF recording: a capture thread copies the chosen screen area 15 times a second, keeps only frames that changed,
// and hands the result to the GIF editor. A red ring marks the area and a small bar holds the time and the Stop button;
// both stay out of the frames. Print Screen also stops a recording (see App.OnKey).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

sealed class Recorder
{
    const int Fps = 15;
    const long BudgetBytes = 1L << 30;  // frames are kept raw until the export, so a large area fills memory fast
    const int MaxMs = 2 * 60 * 1000;
    const int StartDelayMs = 150;       // the overlay must be off the screen before the first frame

    readonly Control ui;
    public volatile bool Active; // read from the key hook thread
    public event Action<Recording> Finished;

    Thread thread;
    volatile bool stopping;
    readonly Stopwatch clock = new Stopwatch();
    Rectangle area;
    RecordFrame frame;
    RecordBar bar;
    List<byte[]> frames;
    List<int> starts;
    int endMs;
    string stopReason; // set by the capture thread when it stops itself

    public Recorder(Control ui) { this.ui = ui; }

    public int ElapsedMs { get { return (int)clock.ElapsedMilliseconds; } }

    public void Start(Rectangle r)
    {
        if (Active || r.Width < 1 || r.Height < 1) return;
        area = r;
        frames = new List<byte[]>();
        starts = new List<int>();
        stopReason = null;
        stopping = false;
        clock.Reset();
        Active = true;
        frame = new RecordFrame(r);
        frame.Show();
        bar = new RecordBar(r, this);
        bar.Show();
        thread = new Thread(Run) { IsBackground = true, Name = "Snip recorder" };
        thread.Start();
    }

    // keep false drops the frames. Runs on the UI thread.
    public void Stop(bool keep)
    {
        if (!Active) return;
        stopping = true;
        thread.Join();
        Active = false;
        frame.Close();
        bar.Close();
        clock.Stop();
        if (!keep) { frames = null; return; }
        if (stopReason != null) Toast.Pop("Recording stopped", stopReason, Toast.Kind.Warn, null);
        else if (frames.Count == 0) Toast.Pop("Recording too short", "Record for a moment before you stop.", Toast.Kind.Warn, null);
        if (frames.Count == 0) return;
        var rec = new Recording(area.Size, frames.ToArray(), starts.ToArray(), endMs);
        frames = null;
        if (Finished != null) Finished(rec);
    }

    void Run()
    {
        Thread.Sleep(StartDelayMs);
        clock.Start();
        int interval = 1000 / Fps;
        long next = 0, bytes = 0;
        byte[] last = null;
        try
        {
            using (var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb))
            using (var g = Graphics.FromImage(bmp))
            {
                while (!stopping)
                {
                    int t = ElapsedMs;
                    if (t >= MaxMs) { stopReason = "Recordings can be at most 2 minutes long."; break; }
                    g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
                    Native.DrawCursor(g, area.Location);
                    var px = new byte[area.Width * area.Height * 4];
                    Gif.ReadPixels(bmp, px);
                    if (last == null || !Gif.Same(px, last))
                    {
                        bytes += px.Length;
                        if (bytes > BudgetBytes) { stopReason = "The recording used all the memory it can have. Record a smaller area for longer GIFs."; break; }
                        frames.Add(px);
                        starts.Add(t);
                        last = px;
                    }
                    next += interval;
                    long wait = next - ElapsedMs;
                    if (wait > 0) Thread.Sleep((int)wait);
                    else next = ElapsedMs; // a slow copy: do not try to catch up
                }
            }
        }
        catch (Exception e)
        {
            stopReason = "The screen could not be copied: " + e.Message;
        }
        endMs = Math.Max(ElapsedMs, starts.Count == 0 ? 0 : starts[starts.Count - 1] + interval);
        if (stopReason != null && !stopping) ui.BeginInvoke((Action)(() => Stop(true)));
    }
}

// The red ring just outside the recorded area. Its region is only the ring, so the area stays clickable.
sealed class RecordFrame : Form
{
    public RecordFrame(Rectangle area)
    {
        float s = Native.DpiAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2)) / 96f;
        int t = Math.Max(2, (int)Math.Round(2 * s));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(255, 59, 48);
        Rectangle outer = Rectangle.Inflate(area, t, t);
        Bounds = outer;
        var ring = new Region(new Rectangle(0, 0, outer.Width, outer.Height));
        ring.Exclude(new Rectangle(t, t, area.Width, area.Height));
        Region = ring;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x8; // NOACTIVATE | TOOLWINDOW | TOPMOST
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int square = 1; // DWMWCP_DONOTROUND
        Native.DwmSetWindowAttribute(Handle, 33, ref square, 4);
        Native.ExcludeFromCapture(Handle);
    }
}
// The bar of a recording: a blinking dot, the time, Stop (keep the recording) and a cross (throw it away).
// It sits under the area, else over it, else inside its bottom edge.
sealed class RecordBar : Form
{
    static readonly Color Red = Color.FromArgb(255, 59, 48);
    readonly Recorder recorder;
    readonly Theme theme = Theme.Current();
    readonly float s;
    readonly Font font;
    readonly Rectangle dot, time, stop, discard;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
    int hover; // 0 none, 1 Stop, 2 discard

    public RecordBar(Rectangle area, Recorder recorder)
    {
        this.recorder = recorder;
        s = Native.DpiAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2)) / 96f;
        font = new Font(Ui.TextBold, 13f * s, GraphicsUnit.Pixel);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = theme.Surface;
        DoubleBuffered = true;

        int h = S(40), pad = S(12), bh = S(28), top = (h - bh) / 2;
        dot = new Rectangle(pad, (h - S(10)) / 2, S(10), S(10));
        time = new Rectangle(dot.Right + S(8), 0, S(44), h);
        stop = new Rectangle(time.Right + S(6), top, S(68), bh);
        discard = new Rectangle(stop.Right + S(4), top, bh, bh);
        int w = discard.Right + S(6);

        Rectangle screen = Screen.FromRectangle(area).WorkingArea;
        int gap = S(10), x = area.X + (area.Width - w) / 2, y;
        if (area.Bottom + gap + h <= screen.Bottom) y = area.Bottom + gap;
        else if (area.Top - gap - h >= screen.Top) y = area.Top - gap - h;
        else y = area.Bottom - gap - h; // the area fills the screen: the bar covers its bottom edge, out of the frames
        x = Math.Max(screen.Left + gap, Math.Min(x, screen.Right - gap - w));
        Bounds = new Rectangle(x, y, w, h);

        timer.Tick += delegate { Invalidate(); };
        timer.Start();
    }

    int S(float v) { return (int)Math.Round(v * s); }

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
        Native.ExcludeFromCapture(Handle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = stop.Contains(e.Location) ? 1 : discard.Contains(e.Location) ? 2 : 0;
        if (h != hover) { hover = h; Cursor = h == 0 ? Cursors.Default : Cursors.Hand; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hover != 0) { hover = 0; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (stop.Contains(e.Location)) recorder.Stop(true);
        else if (discard.Contains(e.Location)) recorder.Stop(false);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int ms = recorder.ElapsedMs;
        using (var b = new SolidBrush((ms / 500) % 2 == 0 ? Red : Color.FromArgb(90, Red))) g.FillEllipse(b, dot);
        string clock = string.Format("{0}:{1:00}", ms / 60000, ms / 1000 % 60);
        TextRenderer.DrawText(g, clock, font, time, theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);

        using (var path = Ui.Round(stop, S(6)))
        using (var b = new SolidBrush(hover == 1 ? ControlPaint.Dark(Red, 0.05f) : Red))
            g.FillPath(b, path);
        int sq = S(9);
        var square = new Rectangle(stop.X + S(11), stop.Y + (stop.Height - sq) / 2, sq, sq);
        using (var path = Ui.Round(square, S(1.5f))) g.FillPath(Brushes.White, path);
        var label = Rectangle.FromLTRB(square.Right + S(6), stop.Top, stop.Right - S(8), stop.Bottom);
        TextRenderer.DrawText(g, "Stop", font, label, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

        if (hover == 2) using (var path = Ui.Round(discard, S(6))) using (var b = new SolidBrush(theme.Selected)) g.FillPath(b, path);
        float c = S(5);
        float cx = discard.X + discard.Width / 2f, cy = discard.Y + discard.Height / 2f;
        using (var p = new Pen(theme.Muted, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(p, cx - c, cy - c, cx + c, cy + c);
            g.DrawLine(p, cx - c, cy + c, cx + c, cy - c);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        font.Dispose();
        base.OnFormClosed(e);
    }
}