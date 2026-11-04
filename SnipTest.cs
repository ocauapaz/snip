// Self-tests: OCR on small screen text, and the markup document logic.
// build-test.cmd compiles this with the app sources; exit code 0 = pass.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;

static class SnipTest
{
    static int failed;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        if (!ok) failed++;
    }

    static int Main()
    {
        OcrReadsSmallText();
        WithAddsAMarkAndKeepsTheOldDoc();
        FlattenBurnsMarksInAtFullSize();
        CropBurnsMarksInAndKeepsTheOriginal();
        PixelateAveragesEachCell();
        PixelateMarkHidesDetailInWholeCells();
        SubtractCoversExactlyTheDifference();
        StoreExpiresOnlyOldSnipFiles();
        GifPlanTrimsAndKeepsTheRealLength();
        GifPlanDropsFramesTooShortToShow();
        GifWriteRoundTripsThroughGdiPlus();
        return failed;
    }

    static Bitmap White(int w, int h)
    {
        var b = new Bitmap(w, h);
        using (var g = Graphics.FromImage(b)) g.Clear(Color.White);
        return b;
    }

    static Mark RedLine(float y)
    {
        return new Mark(Tool.Pen, Color.Red, 6, new[] { new PointF(10, y), new PointF(90, y) }, null);
    }

    static bool IsRed(Color c) { return c.R > 200 && c.G < 80 && c.B < 80; }
    static bool IsWhite(Color c) { return c.R > 240 && c.G > 240 && c.B > 240; }

    static void OcrReadsSmallText()
    {
        var engine = Ocr.CreateEngine();
        foreach (string expected in new[] { "Print Screen copia texto: ação 12345", "The quick brown fox jumps over the lazy dog" })
        {
            using (var bmp = White(340, 22))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var f = new Font("Segoe UI", 12, GraphicsUnit.Pixel)) // small UI text, the hard case
                {
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.DrawString(expected, f, Brushes.Black, 2, 2);
                }
                string got = Ocr.Read(engine, bmp).Result.Trim();
                Check("OCR (" + engine.RecognizerLanguage.LanguageTag + ") reads \"" + got + "\"", got == expected);
            }
        }
    }

    static void WithAddsAMarkAndKeepsTheOldDoc()
    {
        using (var img = White(100, 100))
        {
            var before = new Doc(img, new Mark[0]);
            Doc after = before.With(RedLine(50));
            Check("Doc.With adds one mark and leaves the old Doc as it was", after.Marks.Length == 1 && before.Marks.Length == 0 && after.Image == img);
        }
    }

    static void FlattenBurnsMarksInAtFullSize()
    {
        using (var img = White(100, 100))
        using (Bitmap flat = Markup.Flatten(new Doc(img, new[] { RedLine(50) })))
        {
            Check("Flatten keeps the image size", flat.Size == img.Size);
            Check("Flatten draws the mark", IsRed(flat.GetPixel(50, 50)) && IsWhite(flat.GetPixel(50, 10)));
            Check("Flatten leaves the source image unchanged", IsWhite(img.GetPixel(50, 50)));
        }
    }

    static void CropBurnsMarksInAndKeepsTheOriginal()
    {
        using (var img = White(100, 100))
        {
            var doc = new Doc(img, new[] { RedLine(50) });
            Doc cropped = Markup.Crop(doc, new Rectangle(40, 40, 20, 20));
            using (cropped.Image)
            {
                Check("Crop sets the new size", cropped.Image.Width == 20 && cropped.Image.Height == 20);
                Check("Crop burns the marks in and clears the list", cropped.Marks.Length == 0 && IsRed(cropped.Image.GetPixel(10, 10)));
                Check("Crop keeps the original Doc for undo", doc.Marks.Length == 1 && img.Width == 100 && IsWhite(img.GetPixel(50, 50)));
            }
        }
    }

    static void PixelateAveragesEachCell()
    {
        int c = Markup.Cell;
        using (var img = new Bitmap(2 * c, c))
        {
            using (var g = Graphics.FromImage(img))
            {
                g.FillRectangle(Brushes.Black, 0, 0, c, c);
                g.FillRectangle(Brushes.White, c, 0, c, c);
            }
            using (Bitmap px = Markup.Pixelate(img))
            {
                Color left = px.GetPixel(0, 0), right = px.GetPixel(1, 0);
                Check("Pixelate makes one pixel per cell", px.Width == 2 && px.Height == 1);
                Check("Pixelate keeps each cell's color", left.R < 40 && right.R > 215);
            }
        }
    }

    static void PixelateMarkHidesDetailInWholeCells()
    {
        int c = Markup.Cell;
        using (var img = new Bitmap(4 * c, 2 * c))
        {
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                    img.SetPixel(x, y, (x + y) % 2 == 0 ? Color.Black : Color.White); // fine detail, like small text
            using (Bitmap map = Markup.Pixelate(img))
            {
                // The box starts mid-cell; it must grow to whole cells.
                var mark = new Mark(Tool.Pixelate, Color.Red, 1, new[] { new PointF(c + 3, 2), new PointF(2 * c + 4, c - 2) }, map);
                using (Bitmap flat = Markup.Flatten(new Doc(img, new[] { mark })))
                {
                    Color inside = flat.GetPixel(c, 0), grown = flat.GetPixel(3 * c - 1, c - 1), outside = flat.GetPixel(3 * c, 0);
                    bool gray = inside.R > 90 && inside.R < 165 && grown.R > 90 && grown.R < 165;
                    Check("Pixelate turns detail into flat gray over whole cells", gray);
                    Check("Pixelate leaves pixels outside the cells as they were", outside.R == 0 || outside.R == 255);
                }
            }
        }
    }

    static void SubtractCoversExactlyTheDifference()
    {
        var a = new Rectangle(0, 0, 100, 60);
        var b = new Rectangle(20, 10, 100, 30); // overlaps a, sticks out on the right
        Rectangle[] parts = Markup.Subtract(a, b);
        long area = parts.Sum(r => (long)r.Width * r.Height);
        bool disjoint = parts.All(r => !r.IntersectsWith(b) && parts.All(o => o == r || !o.IntersectsWith(r)));
        Check("Subtract returns a minus b with no overlap", area == 100 * 60 - 80 * 30 && disjoint);
        Check("Subtract of nothing is nothing, and of a disjoint box is the box",
            Markup.Subtract(Rectangle.Empty, a).Length == 0 && Markup.Subtract(a, new Rectangle(500, 500, 5, 5)).Single() == a);
    }

    static void StoreExpiresOnlyOldSnipFiles()
    {
        DateTime now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan ttl = TimeSpan.FromDays(2);
        Check("Store deletes a Snip PNG older than the TTL", Store.IsExpired("Snip 2026-10-24 11.00.00.png", now.AddDays(-2.1), now, ttl));
        Check("Store keeps a Snip PNG inside the TTL", !Store.IsExpired("Snip 2026-10-25 11.00.00.png", now.AddDays(-1.9), now, ttl));
        Check("Store never deletes files it did not name", !Store.IsExpired("holiday.png", now.AddDays(-30), now, ttl) && !Store.IsExpired("Snip notes.txt", now.AddDays(-30), now, ttl));
        Check("Store deletes a Snip GIF older than the TTL", Store.IsExpired("Snip 2026-10-24 11.00.00.gif", now.AddDays(-2.1), now, ttl));
    }

    static bool Same(int[] a, int[] b) { return a.SequenceEqual(b); }

    static void GifPlanTrimsAndKeepsTheRealLength()
    {
        GifPlan p = Gif.Plan(new[] { 0, 100, 200, 300 }, 400, 150, 350, 1);
        Check("GIF plan keeps the frames inside the trim", Same(p.Frames, new[] { 1, 2, 3 }));
        Check("GIF plan cuts the end frames at the trim", Same(p.DelaysCs, new[] { 5, 10, 5 }));
        GifPlan fast = Gif.Plan(new[] { 0, 100 }, 200, 0, 200, 2);
        Check("GIF plan at 2x halves every delay", Same(fast.Frames, new[] { 0, 1 }) && Same(fast.DelaysCs, new[] { 5, 5 }));
    }

    static void GifPlanDropsFramesTooShortToShow()
    {
        GifPlan p = Gif.Plan(new[] { 0, 10, 1000 }, 2000, 0, 2000, 1);
        Check("GIF plan gives a 10 ms frame's place to the next frame", Same(p.Frames, new[] { 1, 2 }) && Same(p.DelaysCs, new[] { 100, 100 }));
    }

    // Colors on the 5-bit grid, at most 255 of them per frame, come back exactly.
    static void GifWriteRoundTripsThroughGdiPlus()
    {
        const int w = 120, h = 80; // 9600 noise pixels fill the LZW table, so the encoder must send clear codes
        var rnd = new Random(7);
        var colors = new int[200];
        for (int i = 0; i < colors.Length; i++) colors[i] = rnd.Next(32) << 19 | rnd.Next(32) << 11 | rnd.Next(32) << 3;
        var noise = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            int c = colors[rnd.Next(colors.Length)];
            noise[4 * p] = (byte)c; noise[4 * p + 1] = (byte)(c >> 8); noise[4 * p + 2] = (byte)(c >> 16);
        }
        var box = (byte[])noise.Clone(); // a changed box in the middle
        for (int y = 20; y < 50; y++)
            for (int x = 30; x < 70; x++) { int p = 4 * (y * w + x); box[p] = 248; box[p + 1] = 64; box[p + 2] = 16; }
        byte[][] frames = { noise, box, (byte[])box.Clone(), noise };
        int[] delays = { 10, 20, 30, 40 };
        byte[][] expected = { noise, box, noise };
        var ms = new MemoryStream();
        Gif.Write(ms, w, h, frames.Length, i => frames[i], delays, null);
        ms.Position = 0;
        using (var img = Image.FromStream(ms))
        {
            int n = img.GetFrameCount(FrameDimension.Time);
            Check("GIF has one frame per change (the same frame twice merges)", n == 3);
            byte[] d = img.GetPropertyItem(0x5100).Value;
            int[] got = Enumerable.Range(0, n).Select(i => BitConverter.ToInt32(d, 4 * i)).ToArray();
            Check("GIF delays add the merged frame's time", Same(got, new[] { 10, 50, 40 }));
            bool exact = n == 3;
            for (int f = 0; f < n && exact; f++)
            {
                img.SelectActiveFrame(FrameDimension.Time, f);
                using (var bmp = new Bitmap(img))
                {
                    var px = new byte[w * h * 4];
                    using (var rgb = bmp.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppRgb)) Gif.ReadPixels(rgb, px);
                    for (int p = 0; p < w * h && exact; p++)
                        exact = px[4 * p] == expected[f][4 * p] && px[4 * p + 1] == expected[f][4 * p + 1] && px[4 * p + 2] == expected[f][4 * p + 2];
                }
            }
            Check("GIF frames decode to the exact pixels", exact);
        }
    }
}