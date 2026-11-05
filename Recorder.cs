// Screen recording: a capture thread copies the chosen screen area (15 times a second for a GIF, 30 for a video),
// keeps only frames that changed, and hands the result to App.Deliver. A video recording also records what the
// speakers play and keeps its frames as JPEG, compressed on the thread pool. A red ring marks the area and a small bar
// holds the time and the Stop button; both stay out of the frames. Print Screen also stops a recording (see App.OnKey).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class Recorder
{
    const int GifFps = 15, VideoFps = Mp4.Fps;
    const long BudgetBytes = 1L << 30;  // GIF frames are kept raw until the export, so a large area fills memory fast
    const int GifMaxMs = 2 * 60 * 1000, VideoMaxMs = 5 * 60 * 1000;
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
    bool video;
    List<byte[]> frames;      // a GIF recording: raw frames
    List<Task<byte[]>> jpegs; // a video recording: frames on their way to JPEG
    SemaphoreSlim inFlight;   // caps the raw frames that wait for the JPEG step
    long jpegBytes;
    List<int> starts;
    int endMs;
    long startHns;            // the time of the first frame, on the clock of the sound packets
    Loopback loopback;
    string stopReason;        // set by the capture thread when it stops itself
    string soundProblem;

    public Recorder(Control ui) { this.ui = ui; }

    public int ElapsedMs { get { return (int)clock.ElapsedMilliseconds; } }

    // withSound: a video recording (MP4 with what the speakers play); else a GIF recording.
    public void Start(Rectangle r, bool withSound)
    {
        if (Active || r.Width < 1 || r.Height < 1) return;
        area = r;
        video = withSound;
        frames = new List<byte[]>();
        jpegs = new List<Task<byte[]>>();
        inFlight = new SemaphoreSlim(2 * Environment.ProcessorCount);
        jpegBytes = 0;
        starts = new List<int>();
        stopReason = soundProblem = null;
        loopback = null;
        if (video)
        {
            try { loopback = Loopback.Start(); }
            catch (Exception e) { soundProblem = "The sound could not be recorded: " + e.Message; }
        }
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
        short[] audio = null;
        string soundNote = soundProblem;
        if (loopback != null)
        {
            if (keep)
            {
                string problem;
                audio = loopback.Finish(startHns, endMs, out problem);
                if (problem != null) soundNote = problem;
            }
            else loopback.Stop();
            loopback = null;
        }
        List<Task<byte[]>> pending = jpegs;
        List<byte[]> raw = frames;
        frames = null;
        jpegs = null;
        if (!keep) return;
        int count = video ? pending.Count : raw.Count;
        if (count == 0)
        {
            Toast.Pop(stopReason != null ? "Recording stopped" : "Recording too short", stopReason ?? "Record for a moment before you stop.", Toast.Kind.Warn, null);
            return;
        }
        byte[][] kept;
        try { kept = video ? pending.Select(t => t.Result).ToArray() : raw.ToArray(); } // the JPEG step is a few frames behind at most
        catch (AggregateException e)
        {
            Toast.Pop("Could not keep the recording", e.InnerException.Message, Toast.Kind.Warn, null);
            return;
        }
        string note = stopReason == null ? soundNote : soundNote == null ? stopReason : stopReason + " " + soundNote;
        var rec = new Recording(area.Size, kept, starts.ToArray(), endMs, video, audio, note);
        if (Finished != null) Finished(rec);
    }

    void Run()
    {
        Thread.Sleep(StartDelayMs);
        startHns = Pcm.Hns(Stopwatch.GetTimestamp());
        clock.Start();
        int interval = 1000 / (video ? VideoFps : GifFps), maxMs = video ? VideoMaxMs : GifMaxMs;
        Size size = area.Size;
        SemaphoreSlim gate = inFlight; // the JPEG tasks can outlive this recording
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
                    if (t >= maxMs) { stopReason = video ? "Videos can be at most 5 minutes long." : "GIFs can be at most 2 minutes long."; break; }
                    g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
                    Native.DrawCursor(g, area.Location);
                    var px = new byte[area.Width * area.Height * 4];
                    Gif.ReadPixels(bmp, px);
                    if (last == null || !Gif.Same(px, last))
                    {
                        bytes = video ? Interlocked.Read(ref jpegBytes) : bytes + px.Length;
                        if (bytes > BudgetBytes) { stopReason = "The recording used all the memory it can have. Record a smaller area for a longer one."; break; }
                        if (video)
                        {
                            gate.Wait();
                            byte[] shot = px;
                            jpegs.Add(Task.Run(() =>
                            {
                                try
                                {
                                    byte[] jpeg = Jpeg.Encode(shot, size);
                                    Interlocked.Add(ref jpegBytes, jpeg.Length);
                                    return jpeg;
                                }
                                finally { gate.Release(); }
                            }));
                        }
                        else frames.Add(px);
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