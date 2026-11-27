// The quick recording editor, opened from the card of a recording: play it (a video with its sound), trim its start
// and end, pick a speed (a GIF only) and a size, then copy the file (which pastes into chats and folders) or save it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class GifEditor : Form
{
    delegate void Glyph(Graphics g, Rectangle r, Color c);

    sealed class Btn
    {
        public string Tip;
        public int W;
        public bool Right, GapBefore, Primary;
        public Action Click;
        public Func<bool> On;
        public Glyph Glyph;
        public Rectangle R;
    }

    static readonly double[] Speeds = { 0.5, 1, 2 };
    static readonly int[] Sizes = { 100, 75, 50 };
    const int MinTrimMs = 100;
    const int SoundChunkMs = 20000; // the preview plays the sound in parts of this length

    readonly Recording rec;
    readonly Task<string> made; // the whole recording at full size, made by App.Deliver
    readonly Dictionary<int, Bitmap> thumbs = new Dictionary<int, Bitmap>(); // timeline cells by frame
    SoundPlayer player;
    bool soundOn, soundDirty = true;
    int soundUntilMs;
    readonly Theme theme = Theme.Current();
    readonly float s;
    readonly Font label, small, tipFont;
    readonly List<Btn> buttons = new List<Btn>();
    readonly List<int> seps = new List<int>();
    readonly System.Windows.Forms.Timer tipDelay = new System.Windows.Forms.Timer { Interval = 450 };
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 15 };
    readonly Stopwatch clock = new Stopwatch();
    readonly Bitmap frameBmp;
    Bitmap strip;
    Rectangle bar, stage, timeline, status;
    RectangleF view;
    int shownFrame = -1;
    int fromMs, toMs;
    double playMs;
    bool playing = true;
    int speed = 1, size;
    int drag; // 0 none, 1 start handle, 2 end handle, 3 playhead
    bool exporting;
    int progress;
    Btn hot, tipBtn;

    public GifEditor(Recording rec, Task<string> made)
    {
        this.rec = rec;
        this.made = made;
        toMs = rec.EndMs;
        frameBmp = new Bitmap(rec.Size.Width, rec.Size.Height, PixelFormat.Format32bppRgb);
        Point c = Cursor.Position;
        s = Native.DpiAt(c) / 96f;
        label = new Font(Ui.TextBold, 13 * s, GraphicsUnit.Pixel);
        small = new Font(Ui.Text, 12.5f * s, GraphicsUnit.Pixel);
        tipFont = new Font(Ui.Text, 12 * s, GraphicsUnit.Pixel);
        tipDelay.Tick += delegate { tipDelay.Stop(); tipBtn = hot; if (tipBtn != null) Invalidate(TipRect(tipBtn)); };
        tick.Tick += delegate { Advance(); };
        Text = rec.Video ? "Snip video" : "Snip GIF";
        Icon = Ui.AppIcon;
        KeyPreview = true;
        BackColor = theme.Canvas;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        BuildButtons();
        // The recording at 1:1 plus a margin, inside 88% of the work area.
        Rectangle wa = Screen.FromPoint(c).WorkingArea;
        int minW = Px(760), chrome = Px(56) + Px(112), pad = Px(28);
        int cw = Math.Max(minW, Math.Min((int)(wa.Width * 0.88), rec.Size.Width + 2 * pad));
        int ch = Math.Max(Px(460), Math.Min((int)(wa.Height * 0.88), rec.Size.Height + 2 * pad + chrome));
        ClientSize = new Size(cw, ch);
        MinimumSize = SizeFromClientSize(new Size(minW, Px(400)));
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        LayoutAll();
        clock.Start();
        tick.Start();
    }

    int Px(float v) { return (int)Math.Round(v * s); }

    // ---- toolbar -----------------------------------------------------------------

    Btn Add(string tip, int w, Action click, Func<bool> on, Glyph glyph)
    {
        var b = new Btn { Tip = tip, W = w, Click = click, On = on, Glyph = glyph };
        buttons.Add(b);
        return b;
    }

    Glyph Word(string text)
    {
        return (g, r, c) => TextRenderer.DrawText(g, text, label, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    void BuildButtons()
    {
        Add("Play or pause (Space)", 36, TogglePlay, null, (g, r, c) => Icon20(g, r, c, (x, p) =>
        {
            using (var fill = new SolidBrush(c))
            {
                if (playing) { x.FillRectangle(fill, 5.5f, 4, 3, 12); x.FillRectangle(fill, 11.5f, 4, 3, 12); }
                else x.FillPolygon(fill, P(6, 3.5f, 16, 10, 6, 16.5f));
            }
        }));
        string[] speedNames = { "0.5×", "1×", "2×" };
        for (int i = 0; i < Speeds.Length && !rec.Video; i++)
        {
            int index = i;
            Btn b = Add("Speed " + speedNames[i], 44, () => { speed = index; Invalidate(); }, () => speed == index, Word(speedNames[i]));
            b.GapBefore = i == 0;
        }
        for (int i = 0; i < Sizes.Length; i++)
        {
            int index = i;
            Btn b = Add("Size " + Sizes[i] + "%  ·  " + SizeText(Exporter.OutputSize(rec, Sizes[i])), 52, () => { size = index; Invalidate(); }, () => size == index, Word(Sizes[i] + "%"));
            b.GapBefore = i == 0;
        }
        Add("Save as file (Ctrl+S)", 36, SaveFile, null, (g, r, c) => Icon20(g, r, c, (x, p) => { x.DrawLine(p, 10, 3, 10, 13); x.DrawLines(p, P(6, 9, 10, 13, 14, 9)); x.DrawLines(p, P(4, 14, 4, 17, 16, 17, 16, 14)); })).Right = true;
        Btn copy = Add("Copy " + Exporter.Noun(rec) + " and close (Enter)", 88, CopyAndClose, null, null);
        copy.Primary = copy.Right = true;
    }

    static string SizeText(Size z) { return z.Width + " × " + z.Height; }
    static PointF[] P(params float[] xy)
    {
        var pts = new PointF[xy.Length / 2];
        for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(xy[2 * i], xy[2 * i + 1]);
        return pts;
    }

    // Icons are drawn on a 20 x 20 grid, centered in the button.
    void Icon20(Graphics g, Rectangle r, Color c, Action<Graphics, Pen> draw)
    {
        float z = 20 * s;
        GraphicsState state = g.Save();
        g.TranslateTransform(r.X + (r.Width - z) / 2f, r.Y + (r.Height - z) / 2f);
        g.ScaleTransform(s, s);
        using (var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            draw(g, pen);
        g.Restore(state);
    }

    void PlaceButtons()
    {
        int h = Px(36), y = (bar.Height - h) / 2, x = Px(12);
        seps.Clear();
        foreach (Btn b in buttons.Where(b => !b.Right))
        {
            if (b.GapBefore) { seps.Add(x + Px(7)); x += Px(16); }
            b.R = new Rectangle(x, y, Px(b.W), h);
            x += Px(b.W) + Px(2);
        }
        x = bar.Right - Px(12);
        foreach (Btn b in buttons.Where(b => b.Right).Reverse())
        {
            x -= Px(b.W);
            b.R = new Rectangle(x, y, Px(b.W), h);
            x -= b.Primary ? Px(10) : Px(2);
        }
    }

    Btn Hit(Point p) { return buttons.FirstOrDefault(b => b.R.Contains(p)); }

    void DrawBar(Graphics g)
    {
        using (var bg = new SolidBrush(theme.Surface)) g.FillRectangle(bg, bar);
        using (var line = new Pen(theme.Hairline, 1))
        {
            g.DrawLine(line, 0, bar.Bottom - 1, bar.Right, bar.Bottom - 1);
            foreach (int x in seps) g.DrawLine(line, x, bar.Y + Px(17), x, bar.Bottom - Px(17));
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        foreach (Btn b in buttons)
        {
            bool enabled = !exporting, on = b.On != null && b.On();
            if (b.Primary)
            {
                using (var fill = new SolidBrush(b == hot && enabled ? ControlPaint.Light(theme.Accent, 0.15f) : theme.Accent))
                using (var path = Ui.Round(b.R, Px(9)))
                    g.FillPath(fill, path);
                g.DrawString(exporting ? progress + "%" : "Copy", label, Brushes.White, b.R, Ui.Center);
                continue;
            }
            if (on || (b == hot && enabled))
            {
                using (var fill = new SolidBrush(on ? theme.Selected : theme.Hover))
                using (var path = Ui.Round(b.R, Px(8)))
                    g.FillPath(fill, path);
            }
            Color fg = !enabled ? Color.FromArgb(80, theme.Muted) : on ? theme.Ink : theme.Muted;
            b.Glyph(g, b.R, fg);
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    // ---- layout ------------------------------------------------------------------

    void LayoutAll()
    {
        bar = new Rectangle(0, 0, ClientSize.Width, Px(56));
        int pad = Px(20);
        status = new Rectangle(pad, ClientSize.Height - Px(36), ClientSize.Width - 2 * pad, Px(24));
        timeline = new Rectangle(pad + Px(8), status.Top - Px(8) - Px(48), ClientSize.Width - 2 * pad - Px(16), Px(48));
        stage = Rectangle.FromLTRB(0, bar.Bottom, ClientSize.Width, Math.Max(bar.Bottom + 1, timeline.Top - Px(16)));
        float p = Px(24);
        float zoom = Math.Max(0.02f, Math.Min(1f, Math.Min((stage.Width - 2 * p) / rec.Size.Width, (stage.Height - 2 * p) / rec.Size.Height)));
        float w = rec.Size.Width * zoom, h = rec.Size.Height * zoom;
        view = new RectangleF((float)Math.Round(stage.X + (stage.Width - w) / 2), (float)Math.Round(stage.Y + (stage.Height - h) / 2), w, h);
        PlaceButtons();
        BuildStrip();
        Invalidate();
    }

    // Thumbnails along the timeline, each one the frame on show at its middle.
    void BuildStrip()
    {
        if (strip != null) strip.Dispose();
        strip = null;
        if (timeline.Width < 1 || timeline.Height < 1) return;
        strip = new Bitmap(timeline.Width, timeline.Height, PixelFormat.Format32bppRgb);
        var cell = new Size(Math.Max(Px(16), (int)Math.Round(timeline.Height * rec.Size.Width / (double)rec.Size.Height)), timeline.Height);
        using (var g = Graphics.FromImage(strip))
        {
            g.Clear(theme.Surface);
            for (int x = 0; x < strip.Width; x += cell.Width)
            {
                int t = (int)((x + cell.Width / 2.0) / strip.Width * rec.EndMs);
                g.DrawImageUnscaled(Thumb(Gif.FrameAt(rec.StartMs, t), cell), x, 0);
            }
        }
    }

    // Frame i covering a timeline cell. A video frame is slow to decode, so cells are kept until the size changes.
    Bitmap Thumb(int i, Size cell)
    {
        Bitmap t;
        if (thumbs.TryGetValue(i, out t) && t.Size == cell) return t;
        if (t != null) t.Dispose();
        t = new Bitmap(cell.Width, cell.Height, PixelFormat.Format32bppRgb);
        using (Bitmap one = Gif.ToBitmap(rec.Pixels(i), rec.Size))
        using (var g = Graphics.FromImage(t))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            float k = Math.Max(cell.Width / (float)one.Width, cell.Height / (float)one.Height);
            float w = one.Width * k, h = one.Height * k;
            g.DrawImage(one, (cell.Width - w) / 2, (cell.Height - h) / 2, w, h);
        }
        thumbs[i] = t;
        return t;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (frameBmp != null && ClientSize.Width > 0 && ClientSize.Height > 0) LayoutAll();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // The title bar takes the toolbar color.
        int dark = theme.Dark ? 1 : 0, caption = ColorTranslator.ToWin32(theme.Surface), text = ColorTranslator.ToWin32(theme.Ink), border = ColorTranslator.ToWin32(theme.Edge);
        Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
        Native.DwmSetWindowAttribute(Handle, 35, ref caption, 4); // DWMWA_CAPTION_COLOR
        Native.DwmSetWindowAttribute(Handle, 36, ref text, 4);    // DWMWA_TEXT_COLOR
        Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);  // DWMWA_BORDER_COLOR
    }
    // ---- painting ----------------------------------------------------------------

    int MinSpanMs { get { return Math.Min(MinTrimMs, rec.EndMs); } }

    int X(double t) { return timeline.X + (int)Math.Round(t * timeline.Width / rec.EndMs); }

    int T(int x) { return (int)Math.Round((x - timeline.X) * (double)rec.EndMs / Math.Max(1, timeline.Width)); }

    static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

    Rectangle TimelineBand { get { return Rectangle.Inflate(timeline, Px(10), Px(10)); } }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        using (var bg = new SolidBrush(theme.Canvas)) g.FillRectangle(bg, ClientRectangle);
        int frame = Gif.FrameAt(rec.StartMs, (int)playMs);
        if (frame != shownFrame) { Gif.CopyInto(rec.Pixels(frame), frameBmp); shownFrame = frame; }
        g.InterpolationMode = view.Width < rec.Size.Width ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(frameBmp, view);
        g.PixelOffsetMode = PixelOffsetMode.Default;
        using (var edge = new Pen(theme.Hairline, 1)) g.DrawRectangle(edge, view.X - 1, view.Y - 1, view.Width + 1, view.Height + 1);
        DrawTimeline(g);
        DrawStatus(g);
        DrawBar(g);
        if (tipBtn != null) DrawTip(g);
    }

    void DrawTimeline(Graphics g)
    {
        if (strip == null) return;
        g.DrawImageUnscaled(strip, timeline.X, timeline.Y);
        int a = X(fromMs), b = X(toMs);
        using (var shade = new SolidBrush(Color.FromArgb(theme.Dark ? 170 : 150, theme.Canvas)))
        {
            g.FillRectangle(shade, Rectangle.FromLTRB(timeline.Left, timeline.Top, a, timeline.Bottom));
            g.FillRectangle(shade, Rectangle.FromLTRB(b, timeline.Top, timeline.Right, timeline.Bottom));
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var line = new Pen(theme.Accent, 2 * s)) g.DrawRectangle(line, a, timeline.Y, b - a, timeline.Height);
        int hw = Px(8);
        foreach (int x in new[] { a - hw, b })
        {
            var r = new Rectangle(x, timeline.Y - Px(3), hw, timeline.Height + Px(6));
            using (var fill = new SolidBrush(theme.Accent)) using (var path = Ui.Round(r, Px(3))) g.FillPath(fill, path);
            using (var grip = new Pen(Color.White, 1.5f * s)) g.DrawLine(grip, r.X + hw / 2f, r.Y + r.Height * 0.35f, r.X + hw / 2f, r.Y + r.Height * 0.65f);
        }
        float px = X(playMs);
        using (var head = new Pen(theme.Ink, 2 * s)) g.DrawLine(head, px, timeline.Top - Px(6), px, timeline.Bottom + Px(6));
        float d = Px(9);
        using (var dot = new SolidBrush(theme.Ink)) g.FillEllipse(dot, px - d / 2, timeline.Top - Px(6) - d / 2, d, d);
        g.SmoothingMode = SmoothingMode.None;
    }

    void DrawStatus(Graphics g)
    {
        string left;
        if (rec.Video)
            left = ((toMs - fromMs) / 1000.0).ToString("0.0") + " s  ·  " + SizeText(Exporter.OutputSize(rec, Sizes[size])) + "  ·  " + (rec.Audio != null ? "with sound" : "no sound");
        else
        {
            GifPlan plan = CurrentPlan();
            double seconds = plan.DelaysCs.Sum() / 100.0;
            left = seconds.ToString("0.0") + " s  \u00B7  " + plan.Frames.Length + " frames  \u00B7  " + SizeText(Gif.Scaled(rec.Size, Sizes[size]));
        }
        string right = "Space play  \u00B7  \u2190 \u2192 frame  \u00B7  I / O trim";
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, left, small, status, theme.Ink, flags | TextFormatFlags.Left);
        TextRenderer.DrawText(g, right, small, status, theme.Muted, flags | TextFormatFlags.Right);
    }

    GifPlan CurrentPlan() { return Gif.Plan(rec.StartMs, rec.EndMs, fromMs, toMs, Speeds[speed]); }

    // ---- tooltips ----------------------------------------------------------------

    void HideTip()
    {
        tipDelay.Stop();
        if (tipBtn == null) return;
        Invalidate(TipRect(tipBtn));
        tipBtn = null;
    }

    Rectangle TipRect(Btn b)
    {
        Size t = TextRenderer.MeasureText(b.Tip, tipFont);
        int w = t.Width + Px(14), h = t.Height + Px(8);
        int x = Math.Max(Px(6), Math.Min(ClientSize.Width - w - Px(6), b.R.X + (b.R.Width - w) / 2));
        return new Rectangle(x, bar.Bottom + Px(6), w, h);
    }

    void DrawTip(Graphics g)
    {
        Rectangle r = TipRect(tipBtn);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(theme.Dark ? Color.FromArgb(58, 58, 60) : Color.White))
        using (var edge = new Pen(theme.Edge, 1))
        using (var path = Ui.Round(r, Px(6)))
        {
            g.FillPath(bg, path);
            g.DrawPath(edge, path);
        }
        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, tipBtn.Tip, tipFont, r, theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
    // ---- playback and input ------------------------------------------------------

    void Advance()
    {
        double dt = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        if (!playing || drag != 0 || exporting) { StopSound(); return; }
        playMs += dt * Speeds[speed];
        if (playMs >= toMs || playMs < fromMs) { playMs = fromMs; soundDirty = true; }
        PlaySound();
        Invalidate(Rectangle.Ceiling(view));
        Invalidate(TimelineBand);
    }

    // The sound plays from playMs in parts; a seek, a trim or a loop starts a new part there.
    void PlaySound()
    {
        if (rec.Audio == null || (!soundDirty && playMs < soundUntilMs)) return;
        int from = (int)playMs, until = Math.Min(toMs, from + SoundChunkMs);
        if (player == null) player = new SoundPlayer();
        player.Stream = new MemoryStream(Pcm.Wav(rec.Audio, (int)((long)from * Pcm.Rate / 1000), (int)((long)until * Pcm.Rate / 1000)));
        player.Play();
        soundOn = true;
        soundDirty = false;
        soundUntilMs = until;
    }

    void StopSound()
    {
        soundDirty = true;
        if (!soundOn) return;
        player.Stop();
        soundOn = false;
    }

    void TogglePlay()
    {
        playing = !playing;
        if (playing && playMs >= toMs - 1) playMs = fromMs;
        Invalidate();
    }

    void Seek(double t)
    {
        playMs = Math.Max(fromMs, Math.Min(toMs - 1, t));
        soundDirty = true;
        Invalidate();
    }

    void Step(int by)
    {
        playing = false;
        int i = Clamp(Gif.FrameAt(rec.StartMs, (int)playMs) + by, 0, rec.StartMs.Length - 1);
        Seek(rec.StartMs[i]);
    }

    void TrimStart(int t) { fromMs = Clamp(t, 0, toMs - MinSpanMs); Seek(playMs); }

    void TrimEnd(int t) { toMs = Clamp(t, fromMs + MinSpanMs, rec.EndMs); Seek(playMs); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || exporting) return;
        HideTip();
        Btn b = bar.Contains(e.Location) ? Hit(e.Location) : null;
        if (b != null) { b.Click(); Invalidate(bar); return; }
        if (!TimelineBand.Contains(e.Location)) return;
        int grab = Px(10);
        if (Math.Abs(e.X - (X(fromMs) - Px(4))) <= grab) drag = 1;
        else if (Math.Abs(e.X - (X(toMs) + Px(4))) <= grab) drag = 2;
        else { drag = 3; Seek(T(e.X)); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag == 1) { TrimStart(T(e.X)); Seek(fromMs); return; }
        if (drag == 2) { TrimEnd(T(e.X)); Seek(toMs - 1); return; }
        if (drag == 3) { Seek(T(e.X)); return; }
        int grab = Px(10);
        bool onHandle = TimelineBand.Contains(e.Location) && (Math.Abs(e.X - (X(fromMs) - Px(4))) <= grab || Math.Abs(e.X - (X(toMs) + Px(4))) <= grab);
        Btn b = bar.Contains(e.Location) ? Hit(e.Location) : null;
        Cursor want = onHandle ? Cursors.SizeWE : TimelineBand.Contains(e.Location) || b != null ? Cursors.Hand : Cursors.Default;
        if (Cursor != want) Cursor = want;
        if (b == hot) return;
        hot = b;
        Invalidate(bar);
        HideTip();
        if (b != null) tipDelay.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        drag = 0;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hot = null;
        HideTip();
        Invalidate(bar);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (exporting) return true;
        switch (keyData)
        {
            case Keys.Space: TogglePlay(); return true;
            case Keys.Left: Step(-1); return true;
            case Keys.Right: Step(1); return true;
            case Keys.I: TrimStart((int)playMs); return true;
            case Keys.O: TrimEnd((int)Math.Ceiling(playMs) + 1); return true;
            case Keys.Control | Keys.C:
            case Keys.Enter: CopyAndClose(); return true;
            case Keys.Control | Keys.S: SaveFile(); return true;
            case Keys.Escape: Close(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- export ------------------------------------------------------------------

    // Encodes the file off the UI thread. path null: a new file in Pictures\Snip. Returns the file, or null on a failure (shown on a card).
    async Task<string> Export(string path)
    {
        int from = fromMs, to = toMs, percent = Sizes[size];
        double rate = Speeds[speed];
        exporting = true;
        progress = 0;
        playing = false;
        Invalidate();
        IProgress<int> report = new Progress<int>(n => { progress = n; Invalidate(bar); });
        try
        {
            return await Task.Run(() =>
            {
                if (path == null) return Store.NewFileAt("", Exporter.Extension(rec), p => Exporter.Write(rec, p, from, to, rate, percent, report.Report));
                Exporter.Write(rec, path, from, to, rate, percent, report.Report);
                return path;
            });
        }
        catch (Exception ex)
        {
            Toast.Pop("Could not make the " + Exporter.Noun(rec), ex.Message, Toast.Kind.Warn, null);
            return null;
        }
        finally
        {
            exporting = false;
            Invalidate();
        }
    }

    bool Untouched { get { return fromMs == 0 && toMs == rec.EndMs && Speeds[speed] == 1 && Sizes[size] == 100; } }

    // The file App.Deliver makes, when it is done and still there; else null.
    async Task<string> Made()
    {
        exporting = true;
        playing = false;
        Invalidate();
        await Task.WhenAny(made); // a failure already showed on its own card
        exporting = false;
        Invalidate();
        return made.Status == TaskStatus.RanToCompletion && File.Exists(made.Result) ? made.Result : null;
    }

    async void CopyAndClose()
    {
        if (exporting) return;
        int first = Gif.FrameAt(rec.StartMs, fromMs);
        string file = Untouched ? await Made() : null;
        if (file == null) file = await Export(null);
        if (file == null) return;
        string problem = Clip.SetFile(file);
        if (problem != null)
        {
            Toast.Pop("Could not copy the " + Exporter.Noun(rec), problem + " Try again.", Toast.Kind.Warn, null);
            return; // the editor stays open, so nothing is lost
        }
        using (Bitmap thumb = Gif.ToBitmap(rec.Pixels(first), rec.Size))
            Toast.Pop(rec.Video ? "Video copied" : "GIF copied", "Paste it in a chat or a folder. Click to show the file.", Toast.Kind.Gif, thumb,
                () => Process.Start("explorer.exe", "/select,\"" + file + "\""));
        Store.SweepInBackground();
        Close();
    }

    async void SaveFile()
    {
        if (exporting) return;
        string pictures = Store.Pictures;
        string shots = Path.Combine(pictures, "Screenshots");
        string path;
        using (var dlg = new SaveFileDialog
        {
            Filter = rec.Video ? "MP4 video|*.mp4" : "GIF image|*.gif",
            FileName = "Recording " + DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss"),
            InitialDirectory = Directory.Exists(shots) ? shots : pictures,
            AddExtension = true
        })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        if (await Export(path) == null) return;
        Toast.Pop("Saved", Path.GetFileName(path), Toast.Kind.Saved, null);
    }

    // The editor opens from a card click; Windows can leave a new window under the app in front.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.Activate(Handle);
    }

    // The recording is already in Pictures and on the clipboard, so a close loses nothing.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (exporting) e.Cancel = true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (player != null) { player.Stop(); player.Dispose(); }
        foreach (Bitmap t in thumbs.Values) t.Dispose();
        tick.Dispose();
        tipDelay.Dispose();
        frameBmp.Dispose();
        if (strip != null) strip.Dispose();
        label.Dispose();
        small.Dispose();
        tipFont.Dispose();
        base.OnFormClosed(e);
    }
}