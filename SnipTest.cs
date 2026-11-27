// Self-tests: OCR on small screen text, the markup document logic, GIF and MP4 files, and the sound timeline.
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
        PcmAssembleJoinsCloseAndGapsLatePackets();
        PcmTo16ResamplesAndClips();
        PcmWavHasAHeaderAndTheFrames();
        Mp4FitKeepsEvenSizes();
        Mp4WriteMakesAVideoWithSound();
        KeyRepeatTellsPressesFromRepeats();
        return failed;
    }

    static void KeyRepeatTellsPressesFromRepeats()
    {
        uint gap = KeyRepeat.GapMs(1);
        Check("Key: the first keydown is a press", KeyRepeat.IsPress(true, false, 1000, 0, gap));
        Check("Key: a repeat while held is no press", !KeyRepeat.IsPress(true, true, 1500, 1000, gap));
        Check("Key: a keydown after a lost keyup is a press", KeyRepeat.IsPress(true, true, 5000, 1000, gap));
        Check("Key: a keyup is no press", !KeyRepeat.IsPress(false, true, 1100, 1000, gap));
        Check("Key: the tick count wrap keeps a repeat a repeat", !KeyRepeat.IsPress(true, true, 100, uint.MaxValue - 100, gap));
        Check("Key: every gap is longer than the slowest Windows repeat",
            Enumerable.Range(0, 4).All(d => KeyRepeat.GapMs(d) > Math.Max(400, (d + 1) * 250)));
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
        Check("Store deletes a Snip MP4 older than the TTL", Store.IsExpired("Snip 2026-10-24 11.00.00.mp4", now.AddDays(-2.1), now, ttl));
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

    static Pcm.Packet Packet(long hns, int frames, float value, int channels)
    {
        var s = new float[frames * channels];
        for (int i = 0; i < s.Length; i++) s[i] = value;
        return new Pcm.Packet { Hns = hns, Frames = frames, Samples = s };
    }

    // At 1000 frames a second one frame is 10000 ticks, and packets within 20 frames of the expected place join.
    static void PcmAssembleJoinsCloseAndGapsLatePackets()
    {
        const long start = 5000000000L, frame = 10000;
        Pcm.Packet[] packets =
        {
            Packet(start, 4, 0.25f, 2),
            Packet(start + 5 * frame, 4, 0.5f, 2),  // 1 frame late: joins at frame 4
            new Pcm.Packet { Hns = long.MinValue, Frames = 2, Samples = null }, // silence with no time: frames 8 and 9
            Packet(start + 50 * frame, 4, 0.75f, 2), // far later: silence before it
        };
        float[] st = Pcm.Assemble(packets, 2, 1000, start, 52);
        Check("Sound timeline has the asked length", st.Length == 104);
        Check("Sound timeline joins a packet that is a little late", st[2 * 3] == 0.25f && st[2 * 4] == 0.5f && st[2 * 7 + 1] == 0.5f);
        Check("Sound timeline leaves silence before a late packet", st[2 * 8] == 0 && st[2 * 49] == 0 && st[2 * 50] == 0.75f);
        Check("Sound timeline cuts what is past its end", st[2 * 51] == 0.75f);
        float[] surround = Pcm.Assemble(new[] { new Pcm.Packet { Hns = start, Frames = 1, Samples = new[] { 0.1f, 0.2f, 0.4f } } }, 3, 1000, start, 1);
        Check("Sound timeline puts the center channel in both sides", Math.Abs(surround[0] - (0.1f + 0.7071f * 0.4f)) < 1e-5 && Math.Abs(surround[1] - (0.2f + 0.7071f * 0.4f)) < 1e-5);
    }

    static void PcmTo16ResamplesAndClips()
    {
        short[] same = Pcm.To16(new[] { 1f, -2f, 0f, 0.5f }, 48000, 48000);
        Check("Sound to 16 bits keeps full scale and clips louder samples", same.Length == 4 && same[0] == 32767 && same[1] == -32767 && same[2] == 0 && same[3] == 16384);
        short[] half = Pcm.To16(new[] { 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f }, 96000, 48000);
        Check("Sound at half the rate keeps every second frame", half.Length == 4 && half[0] == 0 && half[2] == 0);
        short[] twice = Pcm.To16(new[] { 0f, 0f, 1f, 1f }, 24000, 48000);
        Check("Sound at twice the rate puts the mean between frames", twice.Length == 8 && twice[2] == 16384);
    }

    static void PcmWavHasAHeaderAndTheFrames()
    {
        var stereo = new short[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] wav = Pcm.Wav(stereo, 1, 3);
        Check("WAV has a 44-byte header and 4 bytes a frame", wav.Length == 44 + 8 && System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 40) == 8);
        Check("WAV holds the asked frames", BitConverter.ToInt16(wav, 44) == 3 && BitConverter.ToInt16(wav, 50) == 6);
        Check("WAV of a range past the end is empty", Pcm.Wav(stereo, 9, 20).Length == 44);
    }

    static void Mp4FitKeepsEvenSizes()
    {
        Size a = Mp4.Fit(new Size(101, 61), 100), b = Mp4.Fit(new Size(1920, 1080), 50), c = Mp4.Fit(new Size(3, 1), 50);
        Check("MP4 size is even (H.264 needs it)", a == new Size(100, 60) && b == new Size(960, 540) && c.Width >= 2 && c.Height >= 2 && c.Width % 2 == 0 && c.Height % 2 == 0);
    }

    // Red over blue frames and a tone; ffprobe can show more, the test checks what Windows can.
    static void Mp4WriteMakesAVideoWithSound()
    {
        var size = new Size(161, 90);
        var px = new byte[size.Width * size.Height * 4];
        for (int y = 0; y < size.Height; y++)
            for (int x = 0; x < size.Width; x++)
            {
                int p = 4 * (y * size.Width + x);
                if (y < size.Height / 2) px[p + 2] = 230; else px[p] = 230;
            }
        byte[] jpeg = Jpeg.Encode(px, size);
        byte[] back = Jpeg.Decode(jpeg, size);
        Check("JPEG frame decodes to the same size and colors", back.Length == px.Length && back[2] > 200 && back[back.Length - 4] > 200);
        var frames = new byte[10][];
        var starts = new int[10];
        for (int i = 0; i < 10; i++) { frames[i] = jpeg; starts[i] = i * 100; }
        var audio = new short[2 * Pcm.Rate];
        for (int i = 0; i < Pcm.Rate; i++) audio[2 * i] = audio[2 * i + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / Pcm.Rate));
        var rec = new Recording(size, frames, starts, 1000, true, audio, null);
        string path = Path.Combine(Path.GetTempPath(), "SnipTest.mp4");
        int last = -1;
        try
        {
            Exporter.Write(rec, path, 0, 1000, 1, 100, n => last = n);
            byte[] file = File.ReadAllBytes(path);
            string text = System.Text.Encoding.ASCII.GetString(file);
            Check("MP4 file starts with an ftyp box", file.Length > 1000 && text.Substring(4, 4) == "ftyp");
            Check("MP4 has an H.264 track and an AAC track", text.Contains("avc1") && text.Contains("mp4a"));
            Check("MP4 export reports 100% at the end", last == 100);
        }
        catch (Exception ex)
        {
            Check("MP4 export works (" + ex.Message + ")", false);
        }
    }
}