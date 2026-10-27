// Markup editor, opened from the screenshot card: crop, pen, highlighter, rectangle, arrow, pixelate.
// Every edit makes a new Doc, so undo and redo are stacks of Docs. The view keeps a cached
// bitmap of the image and its marks; a mouse move redraws only that cache plus the live mark.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;

enum Tool { Crop, Pen, Highlight, Rect, Arrow, Pixelate }

// One annotation, in image pixels.
sealed class Mark
{
    public readonly Tool Tool;
    public readonly Color Color;
    public readonly float Width;
    public readonly PointF[] Points;
    public readonly Bitmap Pixels; // pixelate only: the coarse copy of the whole image, shared by all pixelate marks

    public Mark(Tool tool, Color color, float width, PointF[] points, Bitmap pixels)
    {
        Tool = tool;
        Color = color;
        Width = width;
        Points = points;
        Pixels = pixels;
    }
}

sealed class Doc
{
    public readonly Bitmap Image;
    public readonly Mark[] Marks;

    public Doc(Bitmap image, Mark[] marks)
    {
        Image = image;
        Marks = marks;
    }

    public Doc With(Mark m) { return new Doc(Image, Marks.Concat(new[] { m }).ToArray()); }
}

// Drawing and document transforms. No window state.
static class Markup
{
    public const int Cell = 14; // pixelate block, in image pixels

    public static RectangleF Box(PointF a, PointF b)
    {
        return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    // One pixel per Cell x Cell block of the image, averaged; the last row and column may be partial.
    public static Bitmap Pixelate(Bitmap src)
    {
        var small = new Bitmap((src.Width + Cell - 1) / Cell, (src.Height + Cell - 1) / Cell, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new RectangleF(0, 0, src.Width / (float)Cell, src.Height / (float)Cell));
        }
        return small;
    }

    // a minus b, as up to four rectangles (bands above, below, left and right of the overlap).
    public static Rectangle[] Subtract(Rectangle a, Rectangle b)
    {
        if (a.Width <= 0 || a.Height <= 0) return new Rectangle[0];
        Rectangle i = Rectangle.Intersect(a, b);
        if (i.Width <= 0 || i.Height <= 0) return new[] { a };
        return new[]
        {
            Rectangle.FromLTRB(a.Left, a.Top, a.Right, i.Top),
            Rectangle.FromLTRB(a.Left, i.Bottom, a.Right, a.Bottom),
            Rectangle.FromLTRB(a.Left, i.Top, i.Left, i.Bottom),
            Rectangle.FromLTRB(i.Right, i.Top, a.Right, i.Bottom)
        }.Where(r => r.Width > 0 && r.Height > 0).ToArray();
    }

    // The box grown to whole cells, so a pixelated area never shows half cells.
    public static Rectangle SnapToCells(RectangleF box)
    {
        int x0 = (int)Math.Floor(box.Left / Cell) * Cell, y0 = (int)Math.Floor(box.Top / Cell) * Cell;
        int x1 = (int)Math.Ceiling(box.Right / Cell) * Cell, y1 = (int)Math.Ceiling(box.Bottom / Cell) * Cell;
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    static Pen Stroke(Mark m)
    {
        Color c = m.Tool == Tool.Highlight ? Color.FromArgb(110, m.Color) : m.Color;
        return new Pen(c, m.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
    }

    public static void Draw(Graphics g, Mark m)
    {
        PointF[] p = m.Points;
        switch (m.Tool)
        {
            case Tool.Pen:
            case Tool.Highlight:
                if (p.Length < 2) p = new[] { p[0], new PointF(p[0].X + 0.1f, p[0].Y) }; // a click is a dot
                using (var pen = Stroke(m)) g.DrawLines(pen, p); // one call, so a highlighter stroke never darkens where it crosses itself
                break;
            case Tool.Rect:
                using (var pen = Stroke(m))
                {
                    RectangleF r = Box(p[0], p[1]);
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                }
                break;
            case Tool.Arrow:
                using (var pen = Stroke(m))
                using (var head = new AdjustableArrowCap(3.2f, 3.6f, true))
                {
                    pen.CustomEndCap = head;
                    g.DrawLine(pen, p[0], p[1]);
                }
                break;
            case Tool.Pixelate:
                Rectangle cells = SnapToCells(Box(p[0], p[1]));
                if (cells.Width == 0 || cells.Height == 0) break;
                InterpolationMode im = g.InterpolationMode;
                PixelOffsetMode po = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(m.Pixels, cells, new RectangleF(cells.X / (float)Cell, cells.Y / (float)Cell, cells.Width / (float)Cell, cells.Height / (float)Cell), GraphicsUnit.Pixel);
                g.InterpolationMode = im;
                g.PixelOffsetMode = po;
                break;
        }
    }

    public static void Paint(Graphics g, IEnumerable<Mark> marks)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (Mark m in marks) Draw(g, m);
    }

    // The image with its marks burned in, at full resolution.
    public static Bitmap Flatten(Doc d)
    {
        var bmp = new Bitmap(d.Image.Width, d.Image.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(d.Image, new Rectangle(0, 0, d.Image.Width, d.Image.Height));
            g.CompositingMode = CompositingMode.SourceOver;
            Paint(g, d.Marks);
        }
        return bmp;
    }

    // Crop burns the marks in, so the new Doc has none.
    public static Doc Crop(Doc d, Rectangle r)
    {
        using (Bitmap flat = Flatten(d))
            return new Doc(flat.Clone(r, PixelFormat.Format32bppPArgb), new Mark[0]);
    }
}

sealed class Editor : Form
{
    delegate void Glyph(Graphics g, Rectangle r, Color c);

    sealed class Btn
    {
        public string Tip;
        public int W;
        public bool Right, GapBefore, Primary;
        public Action Click;
        public Func<bool> On, Enabled;
        public Glyph Glyph;
        public Rectangle R;
    }

    static readonly Color[] Palette =
    {
        Color.FromArgb(255, 59, 48), Color.FromArgb(255, 149, 0), Color.FromArgb(255, 204, 0), Color.FromArgb(52, 199, 89),
        Color.FromArgb(0, 122, 255), Color.FromArgb(29, 29, 31), Color.White
    };
    static readonly string[] PaletteNames = { "Red", "Orange", "Yellow", "Green", "Blue", "Black", "White" };
    static readonly float[] Widths = { 3, 6, 12 }; // screen pixels at any zoom

    readonly Theme theme = Theme.Current();
    readonly float s;
    readonly Font label;
    readonly Stack<Doc> undo = new Stack<Doc>(), redo = new Stack<Doc>();
    readonly List<Bitmap> owned = new List<Bitmap>(); // every bitmap any Doc uses; freed on close
    readonly List<Btn> buttons = new List<Btn>();
    readonly List<int> seps = new List<int>();
    readonly System.Windows.Forms.Timer tipDelay = new System.Windows.Forms.Timer { Interval = 450 };
    readonly Font tipFont;
    Btn tipBtn; // the button whose tooltip is drawn under the toolbar
    Bitmap pixelMap, pixelMapFor, pixelView, pixelViewFor;
    Doc doc;
    Tool tool = Tool.Pen;
    Color color = Palette[0];
    int width = 1;
    Rectangle bar, canvas;
    RectangleF view;
    float zoom = 1;
    Bitmap cache;
    bool drawing;
    PointF start;
    List<PointF> live;
    Mark liveMark;
    Rectangle lastDirty, lastCells; // lastCells: the live pixelate box on screen, in cache pixels
    Btn hot;

    public Editor(Bitmap image)
    {
        doc = new Doc(image, new Mark[0]);
        owned.Add(image);
        Point c = Cursor.Position;
        s = Native.DpiAt(c) / 96f;
        label = new Font(Ui.TextBold, 13 * s, GraphicsUnit.Pixel);
        tipFont = new Font(Ui.Text, 12 * s, GraphicsUnit.Pixel);
        tipDelay.Tick += delegate { tipDelay.Stop(); tipBtn = hot; if (tipBtn != null) Invalidate(TipRect(tipBtn)); };
        Text = "Snip";
        Icon = Ui.AppIcon;
        KeyPreview = true;
        BackColor = theme.Canvas;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        BuildButtons();
        // Image at 1:1 plus a margin, inside 88% of the work area.
        Rectangle wa = Screen.FromPoint(c).WorkingArea;
        int minW = Px(800), barH = Px(56), pad = Px(28);
        int cw = Math.Max(minW, Math.Min((int)(wa.Width * 0.88), image.Width + 2 * pad));
        int ch = Math.Max(Px(420), Math.Min((int)(wa.Height * 0.88), image.Height + 2 * pad + barH));
        ClientSize = new Size(cw, ch);
        MinimumSize = SizeFromClientSize(new Size(minW, Px(360)));
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        LayoutAll();
    }

    int Px(float v) { return (int)Math.Round(v * s); }

    // ---- toolbar -----------------------------------------------------------

    Btn Add(string tip, int w, Action click, Func<bool> on, Glyph glyph)
    {
        var b = new Btn { Tip = tip, W = w, Click = click, On = on, Glyph = glyph };
        buttons.Add(b);
        return b;
    }

    void AddTool(Tool t, string tip, Glyph glyph)
    {
        Add(tip, 36, () => SetTool(t), () => tool == t, glyph);
    }

    void BuildButtons()
    {
        AddTool(Tool.Crop, "Crop (C)", (g, r, c) => Icon20(g, r, c, (x, p) => { x.DrawLines(p, P(6, 2, 6, 14, 18, 14)); x.DrawLines(p, P(2, 6, 14, 6, 14, 18)); }));
        AddTool(Tool.Pen, "Pen (P)", (g, r, c) => Icon20(g, r, c, (x, p) => { x.DrawPolygon(p, P(13, 3, 17, 7, 7, 17, 3, 17, 3, 13)); x.DrawLine(p, 11, 5, 15, 9); }));
        AddTool(Tool.Highlight, "Highlighter (H)", (g, r, c) => Icon20(g, r, c, (x, p) => { x.DrawPolygon(p, P(12, 2.5f, 17.5f, 8, 10, 15.5f, 4.5f, 10)); x.DrawLines(p, P(4.5f, 10, 3, 14.5f, 6, 17, 10, 15.5f)); }));
        AddTool(Tool.Rect, "Rectangle (R)", (g, r, c) => Icon20(g, r, c, (x, p) => { using (var path = Ui.Round(new RectangleF(3, 5, 14, 10), 2)) x.DrawPath(p, path); }));
        AddTool(Tool.Arrow, "Arrow (A)", (g, r, c) => Icon20(g, r, c, (x, p) => { x.DrawLine(p, 4, 16, 16, 4); x.DrawLines(p, P(8.5f, 4, 16, 4, 16, 11.5f)); }));
        AddTool(Tool.Pixelate, "Pixelate (B)", (g, r, c) => Icon20(g, r, c, (x, p) =>
        {
            using (var path = Ui.Round(new RectangleF(3, 3, 14, 14), 1.5f)) x.DrawPath(p, path);
            using (var b = new SolidBrush(p.Color))
                foreach (var cell in new[] { new[] { 0, 0 }, new[] { 2, 0 }, new[] { 1, 1 }, new[] { 0, 2 }, new[] { 2, 2 } })
                    x.FillRectangle(b, 3 + cell[0] * 14 / 3f, 3 + cell[1] * 14 / 3f, 14 / 3f, 14 / 3f);
        }));

        for (int i = 0; i < Palette.Length; i++)
        {
            Color swatch = Palette[i];
            Btn b = Add(PaletteNames[i], 28, () => { color = swatch; Invalidate(bar); }, () => color == swatch, (g, r, c) => DrawSwatch(g, r, swatch, color == swatch));
            b.GapBefore = i == 0;
        }
        string[] widthNames = { "Thin", "Medium", "Thick" };
        float[] dots = { 4, 7, 11 };
        for (int i = 0; i < Widths.Length; i++)
        {
            int index = i;
            float d = dots[i];
            Btn b = Add(widthNames[i], 28, () => { width = index; Invalidate(bar); }, () => width == index, (g, r, c) =>
            {
                using (var fill = new SolidBrush(c))
                    g.FillEllipse(fill, r.X + (r.Width - d * s) / 2, r.Y + (r.Height - d * s) / 2, d * s, d * s);
            });
            b.GapBefore = i == 0;
        }

        Add("Undo (Ctrl+Z)", 36, Undo, null, (g, r, c) => Icon20(g, r, c, (x, p) => { x.DrawBezier(p, 15.5f, 15, 15.5f, 9, 12, 7, 5, 7); x.DrawLines(p, P(8.5f, 3.5f, 5, 7, 8.5f, 10.5f)); })).Enabled = () => undo.Count > 0;
        Add("Redo (Ctrl+Y)", 36, Redo, null, (g, r, c) => Icon20(g, r, c, (x, p) => { x.DrawBezier(p, 4.5f, 15, 4.5f, 9, 8, 7, 15, 7); x.DrawLines(p, P(11.5f, 3.5f, 15, 7, 11.5f, 10.5f)); })).Enabled = () => redo.Count > 0;
        Add("Save as file (Ctrl+S)", 36, SaveFile, null, (g, r, c) => Icon20(g, r, c, (x, p) => { x.DrawLine(p, 10, 3, 10, 13); x.DrawLines(p, P(6, 9, 10, 13, 14, 9)); x.DrawLines(p, P(4, 14, 4, 17, 16, 17, 16, 14)); }));
        Btn copy = Add("Copy and close (Enter)", 80, CopyAndClose, null, null);
        copy.Primary = true;
        foreach (Btn b in buttons.Skip(buttons.Count - 4)) b.Right = true;
    }

    static PointF[] P(params float[] xy)
    {
        var pts = new PointF[xy.Length / 2];
        for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(xy[2 * i], xy[2 * i + 1]);
        return pts;
    }

    // Icons are drawn on a 20 x 20 grid, centered in the button.
    void Icon20(Graphics g, Rectangle r, Color c, Action<Graphics, Pen> draw)
    {
        float size = 20 * s;
        GraphicsState state = g.Save();
        g.TranslateTransform(r.X + (r.Width - size) / 2f, r.Y + (r.Height - size) / 2f);
        g.ScaleTransform(s, s);
        using (var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            draw(g, pen);
        g.Restore(state);
    }

    void DrawSwatch(Graphics g, Rectangle r, Color c, bool on)
    {
        float d = 18 * s;
        var dot = new RectangleF(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        using (var fill = new SolidBrush(c)) g.FillEllipse(fill, dot);
        using (var edge = new Pen(Color.FromArgb(70, 128, 128, 128), 1)) g.DrawEllipse(edge, dot);
        if (!on) return;
        dot.Inflate(3.5f * s, 3.5f * s);
        using (var ring = new Pen(theme.Ink, 1.8f * s)) g.DrawEllipse(ring, dot);
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

    static bool IsEnabled(Btn b) { return b.Enabled == null || b.Enabled(); }

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
            bool enabled = IsEnabled(b), on = b.On != null && b.On();
            if (b.Primary)
            {
                using (var fill = new SolidBrush(b == hot ? ControlPaint.Light(theme.Accent, 0.15f) : theme.Accent))
                using (var path = Ui.Round(b.R, Px(9)))
                    g.FillPath(fill, path);
                g.DrawString("Copy", label, Brushes.White, b.R, Ui.Center);
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

    // ---- layout and view cache -----------------------------------------------

    void LayoutAll()
    {
        bar = new Rectangle(0, 0, ClientSize.Width, Px(56));
        canvas = Rectangle.FromLTRB(0, bar.Bottom, ClientSize.Width, Math.Max(bar.Bottom + 1, ClientSize.Height));
        Bitmap img = doc.Image;
        float pad = Px(28);
        zoom = Math.Max(0.02f, Math.Min(1f, Math.Min((canvas.Width - 2 * pad) / img.Width, (canvas.Height - 2 * pad) / img.Height)));
        float w = img.Width * zoom, h = img.Height * zoom;
        view = new RectangleF((float)Math.Round(canvas.X + (canvas.Width - w) / 2), (float)Math.Round(canvas.Y + (canvas.Height - h) / 2), w, h);
        PlaceButtons();
        RebuildCache();
        Invalidate();
    }

    void RebuildCache()
    {
        if (cache != null) cache.Dispose();
        int w = Math.Max(1, (int)Math.Round(view.Width)), h = Math.Max(1, (int)Math.Round(view.Height));
        cache = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(cache))
        {
            g.InterpolationMode = zoom < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(doc.Image, new Rectangle(0, 0, w, h));
            g.ScaleTransform(w / (float)doc.Image.Width, h / (float)doc.Image.Height);
            Markup.Paint(g, doc.Marks);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (doc != null && ClientSize.Width > 0 && ClientSize.Height > 0) LayoutAll();
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

    // ---- painting ------------------------------------------------------------

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        using (var bg = new SolidBrush(theme.Canvas)) g.FillRectangle(bg, canvas);
        g.DrawImageUnscaled(cache, (int)view.X, (int)view.Y);
        using (var edge = new Pen(theme.Hairline, 1)) g.DrawRectangle(edge, view.X - 1, view.Y - 1, view.Width + 1, view.Height + 1);
        if (liveMark != null && liveMark.Tool == Tool.Pixelate)
        {
            Rectangle src = PixelCellsOnScreen(liveMark);
            g.DrawImage(PixelView(), new Rectangle((int)view.X + src.X, (int)view.Y + src.Y, src.Width, src.Height), src, GraphicsUnit.Pixel);
        }
        else if (liveMark != null)
        {
            GraphicsState state = g.Save();
            g.SetClip(view);
            g.TranslateTransform(view.X, view.Y);
            g.ScaleTransform(zoom, zoom);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (liveMark.Tool == Tool.Crop) DrawCropLive(g, Markup.Box(liveMark.Points[0], liveMark.Points[1]));
            else Markup.Draw(g, liveMark);
            g.Restore(state);
            if (liveMark.Tool == Tool.Crop) DrawSizePill(g, Markup.Box(liveMark.Points[0], liveMark.Points[1]));
        }
        DrawBar(g);
        if (tipBtn != null) DrawTip(g);
    }

    void DrawCropLive(Graphics g, RectangleF box)
    {
        using (var shade = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
        using (var outside = new Region(new RectangleF(0, 0, doc.Image.Width, doc.Image.Height)))
        {
            outside.Exclude(box);
            g.FillRegion(shade, outside);
        }
        using (var pen = new Pen(Color.White, 1.5f / zoom)) g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
    }

    void DrawSizePill(Graphics g, RectangleF box)
    {
        string text = (int)Math.Round(box.Width) + " × " + (int)Math.Round(box.Height);
        int w = Px(84), h = Px(22);
        var p = new Rectangle((int)(view.X + box.Right * zoom) - w, (int)(view.Y + box.Bottom * zoom) + Px(8), w, h);
        if (p.Bottom > canvas.Bottom) p.Y = (int)(view.Y + box.Bottom * zoom) - h - Px(8);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Color.FromArgb(210, 29, 29, 31)))
        using (var path = Ui.Round(p, h / 2f))
            g.FillPath(bg, path);
        using (var f = new Font(Ui.Text, 11.5f * s, GraphicsUnit.Pixel))
            g.DrawString(text, f, Brushes.White, p, Ui.Center);
        g.SmoothingMode = SmoothingMode.None;
    }

    // ---- input -----------------------------------------------------------------

    PointF ToImage(Point p)
    {
        float x = (p.X - view.X) / zoom, y = (p.Y - view.Y) / zoom;
        return new PointF(Math.Max(0, Math.Min(doc.Image.Width, x)), Math.Max(0, Math.Min(doc.Image.Height, y)));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (bar.Contains(e.Location))
        {
            HideTip();
            Btn b = Hit(e.Location);
            if (b != null && IsEnabled(b)) b.Click();
            return;
        }
        drawing = true;
        start = ToImage(e.Location);
        live = new List<PointF> { start };
        lastDirty = lastCells = Rectangle.Empty;
        UpdateLive(start);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!drawing) { Hover(e.Location); return; }
        PointF p = ToImage(e.Location);
        if (tool == Tool.Pen || tool == Tool.Highlight)
        {
            PointF last = live[live.Count - 1];
            if (Math.Abs(p.X - last.X) * zoom < 1 && Math.Abs(p.Y - last.Y) * zoom < 1) return; // under one screen pixel
            live.Add(p);
        }
        UpdateLive(p);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!drawing || e.Button != MouseButtons.Left) return;
        drawing = false;
        Mark m = liveMark;
        liveMark = null;
        RectangleF box = Markup.Box(m.Points[0], m.Points[m.Points.Length - 1]);
        bool tiny = box.Width * zoom < 3 && box.Height * zoom < 3;
        if (m.Tool == Tool.Crop)
        {
            Rectangle r = Rectangle.Intersect(Rectangle.Round(box), new Rectangle(Point.Empty, doc.Image.Size));
            if (r.Width >= 4 && r.Height >= 4)
            {
                Doc cropped = Markup.Crop(doc, r);
                owned.Add(cropped.Image);
                Push(cropped);
                return;
            }
        }
        else if (!tiny || m.Tool == Tool.Pen || m.Tool == Tool.Highlight)
        {
            Push(doc.With(m));
            return;
        }
        Invalidate(canvas);
    }

    // The coarse copy of the current image, made once per image (a crop makes a new one).
    Bitmap PixelMap()
    {
        if (pixelMapFor != doc.Image)
        {
            pixelMap = Markup.Pixelate(doc.Image);
            pixelMapFor = doc.Image;
            owned.Add(pixelMap);
        }
        return pixelMap;
    }

    // The whole image pixelated at screen size, so a pixelate drag is a plain copy from it.
    Bitmap PixelView()
    {
        if (pixelView == null || pixelViewFor != pixelMapFor || pixelView.Size != cache.Size)
        {
            if (pixelView != null) pixelView.Dispose();
            Bitmap map = PixelMap();
            pixelView = new Bitmap(cache.Width, cache.Height, PixelFormat.Format32bppPArgb);
            pixelViewFor = pixelMapFor;
            using (var g = Graphics.FromImage(pixelView))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                float kx = cache.Width / (float)doc.Image.Width, ky = cache.Height / (float)doc.Image.Height;
                g.DrawImage(map, new RectangleF(0, 0, map.Width * Markup.Cell * kx, map.Height * Markup.Cell * ky));
            }
        }
        return pixelView;
    }

    // Copies r (cache pixels) of src to the same place on screen.
    void Blit(Graphics g, Bitmap src, Rectangle r)
    {
        g.DrawImage(src, new Rectangle((int)view.X + r.X, (int)view.Y + r.Y, r.Width, r.Height), r, GraphicsUnit.Pixel);
    }

    // The pixelate box grown to whole cells, in cache (screen) pixels.
    Rectangle PixelCellsOnScreen(Mark m)
    {
        Rectangle cells = Markup.SnapToCells(Markup.Box(m.Points[0], m.Points[1]));
        float kx = cache.Width / (float)doc.Image.Width, ky = cache.Height / (float)doc.Image.Height;
        return Rectangle.Intersect(Rectangle.Round(new RectangleF(cells.X * kx, cells.Y * ky, cells.Width * kx, cells.Height * ky)), new Rectangle(Point.Empty, cache.Size));
    }

    void UpdateLive(PointF end)
    {
        float w = Widths[width] / zoom * (tool == Tool.Highlight ? 3.5f : 1);
        PointF[] pts = tool == Tool.Pen || tool == Tool.Highlight ? live.ToArray() : new[] { start, end };
        liveMark = new Mark(tool, color, w, pts, tool == Tool.Pixelate ? PixelMap() : null);
        if (tool == Tool.Pixelate)
        {
            // The cell grid is fixed, so only the strips the box gained or lost change.
            // They are copied straight to the window: gained ones from the pixelated view,
            // lost ones from the cache. No repaint, so the cost stays flat at any box size.
            Rectangle now = PixelCellsOnScreen(liveMark);
            using (Graphics g = CreateGraphics())
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                foreach (Rectangle r in Markup.Subtract(now, lastCells)) Blit(g, PixelView(), r);
                foreach (Rectangle r in Markup.Subtract(lastCells, now)) Blit(g, cache, r);
            }
            lastCells = now;
            return;
        }
        Rectangle dirty = LiveBounds(liveMark);
        Invalidate(lastDirty.IsEmpty ? dirty : Rectangle.Union(dirty, lastDirty));
        lastDirty = dirty;
    }

    // Screen area the live mark changed since the last move: for a pen, only the newest segment.
    Rectangle LiveBounds(Mark m)
    {
        if (m.Tool == Tool.Crop) return canvas; // the shade outside the crop box moves everywhere
        PointF[] p = m.Points;
        bool stroke = m.Tool == Tool.Pen || m.Tool == Tool.Highlight;
        RectangleF box = stroke && p.Length > 1 ? Markup.Box(p[p.Length - 2], p[p.Length - 1]) : Markup.Box(p[0], p[p.Length - 1]);
        float grow = m.Width * zoom * (m.Tool == Tool.Arrow ? 2.5f : 0.5f) + 3; // the arrow head is wider than the line
        return Rectangle.Ceiling(new RectangleF(view.X + box.X * zoom - grow, view.Y + box.Y * zoom - grow, box.Width * zoom + 2 * grow, box.Height * zoom + 2 * grow));
    }

    void CancelLive()
    {
        drawing = false;
        liveMark = null;
        Invalidate(canvas);
    }

    void Hover(Point p)
    {
        Cursor want = canvas.Contains(p) ? Cursors.Cross : Cursors.Default;
        if (Cursor != want) Cursor = want;
        Btn b = bar.Contains(p) ? Hit(p) : null;
        if (b == hot) return;
        hot = b;
        Invalidate(bar);
        HideTip();
        if (b != null) tipDelay.Start();
    }

    void HideTip()
    {
        tipDelay.Stop();
        if (tipBtn == null) return;
        Invalidate(TipRect(tipBtn));
        tipBtn = null;
    }

    // Centered under the button, kept inside the window.
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

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hot = null;
        HideTip();
        Invalidate(bar);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y:
            case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
            case Keys.Control | Keys.C:
            case Keys.Enter: CopyAndClose(); return true;
            case Keys.Control | Keys.S: SaveFile(); return true;
            case Keys.Escape: if (drawing) CancelLive(); else Close(); return true;
            case Keys.C: SetTool(Tool.Crop); return true;
            case Keys.P: SetTool(Tool.Pen); return true;
            case Keys.H: SetTool(Tool.Highlight); return true;
            case Keys.R: SetTool(Tool.Rect); return true;
            case Keys.A: SetTool(Tool.Arrow); return true;
            case Keys.B: SetTool(Tool.Pixelate); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- commands ----------------------------------------------------------------

    void SetTool(Tool t)
    {
        tool = t;
        Invalidate(bar);
    }

    void Push(Doc next)
    {
        undo.Push(doc);
        redo.Clear();
        ShowDoc(next);
    }

    void Undo()
    {
        if (drawing || undo.Count == 0) return;
        redo.Push(doc);
        ShowDoc(undo.Pop());
    }

    void Redo()
    {
        if (drawing || redo.Count == 0) return;
        undo.Push(doc);
        ShowDoc(redo.Pop());
    }

    void ShowDoc(Doc next)
    {
        Doc prev = doc;
        doc = next;
        if (next.Image.Size != prev.Image.Size) { LayoutAll(); return; }
        // A new mark (draw or redo) goes on top of the cache; undo and crop rebuild it.
        if (next.Image == prev.Image && next.Marks.Length == prev.Marks.Length + 1)
        {
            using (var g = Graphics.FromImage(cache))
            {
                g.ScaleTransform(cache.Width / (float)doc.Image.Width, cache.Height / (float)doc.Image.Height);
                Markup.Paint(g, new[] { next.Marks[next.Marks.Length - 1] });
            }
        }
        else RebuildCache();
        Invalidate();
    }

    void CopyAndClose()
    {
        if (drawing) return;
        using (Bitmap flat = Markup.Flatten(doc))
        {
            Clip.SetImage(flat);
            Store.SaveInBackground(flat.Clone(new Rectangle(Point.Empty, flat.Size), PixelFormat.Format32bppArgb), " edited");
            Toast.Pop("Edited screenshot copied", "Click to edit again  ·  " + flat.Width + " × " + flat.Height, Toast.Kind.Image, flat);
        }
        Close();
    }

    void SaveFile()
    {
        if (drawing) return;
        string pictures = Store.Pictures;
        string shots = Path.Combine(pictures, "Screenshots");
        using (var dlg = new SaveFileDialog
        {
            Filter = "PNG image|*.png|JPEG image|*.jpg",
            FileName = "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss"),
            InitialDirectory = Directory.Exists(shots) ? shots : pictures, // the Windows Screenshots folder when it exists
            AddExtension = true
        })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            using (Bitmap flat = Markup.Flatten(doc))
            using (Bitmap rgb = flat.Clone(new Rectangle(Point.Empty, flat.Size), PixelFormat.Format24bppRgb))
                rgb.Save(dlg.FileName, dlg.FilterIndex == 2 ? ImageFormat.Jpeg : ImageFormat.Png);
            Toast.Pop("Saved", Path.GetFileName(dlg.FileName), Toast.Kind.Saved, null);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        CancelLive();
        if (cache != null) cache.Dispose();
        if (pixelView != null) pixelView.Dispose();
        foreach (Bitmap b in owned) b.Dispose();
        label.Dispose();
        tipDelay.Dispose();
        tipFont.Dispose();
        base.OnFormClosed(e);
    }
}
