// GIF recordings: the pure parts. Plan picks the frames and delays of an export (trim, speed);
// Write encodes an animated GIF (median-cut palette per frame, only the changed box, LZW).
// The recorder and the GIF editor (Recorder.cs, GifEditor.cs) call these; Media.cs has the video and sound parts.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// A finished recording: unique frames; frame i shows from StartMs[i] to the next start (the last one to EndMs).
// A GIF recording keeps its frames as 32-bit BGRX rows. A video recording keeps them as JPEG files, so minutes
// fit in memory, exports as MP4, and has its sound as 48 kHz stereo PCM from 0 to EndMs (null: no sound).
sealed class Recording
{
    public readonly Size Size;
    public readonly byte[][] Frames;
    public readonly int[] StartMs;
    public readonly int EndMs;
    public readonly bool Video;
    public readonly short[] Audio;
    public readonly string Note; // why the recording stopped early or has no sound; null when all went well

    public Recording(Size size, byte[][] frames, int[] startMs, int endMs)
        : this(size, frames, startMs, endMs, false, null, null) { }

    public Recording(Size size, byte[][] frames, int[] startMs, int endMs, bool video, short[] audio, string note)
    {
        Size = size;
        Frames = frames;
        StartMs = startMs;
        EndMs = endMs;
        Video = video;
        Audio = audio;
        Note = note;
    }

    // Frame i as BGRX rows.
    public byte[] Pixels(int i) { return Video ? Jpeg.Decode(Frames[i], Size) : Frames[i]; }
}

// The recorded frames an export shows, in order, and how long each one shows, in GIF centiseconds.
sealed class GifPlan
{
    public readonly int[] Frames;
    public readonly int[] DelaysCs;

    public GifPlan(int[] frames, int[] delaysCs)
    {
        Frames = frames;
        DelaysCs = delaysCs;
    }
}

static class Gif
{
    public const int MinDelayCs = 2; // browsers show a shorter delay as 10 cs, so the GIF would play slow
    const int Transparent = 255;     // palette index for "same as the frame before"

    // The frame on show at t ms.
    public static int FrameAt(int[] startMs, int t)
    {
        int i = Array.BinarySearch(startMs, t);
        if (i < 0) i = ~i - 1;
        return Math.Max(0, Math.Min(startMs.Length - 1, i));
    }

    // The frames from fromMs to toMs, played at speed. Frame ends are rounded on the output clock, so the
    // delays add up to the real length. A frame that would show under MinDelayCs gives its place to the next one.
    public static GifPlan Plan(int[] startMs, int endMs, int fromMs, int toMs, double speed)
    {
        var frames = new List<int>();
        var delays = new List<int>();
        int shownCs = 0;
        for (int i = FrameAt(startMs, fromMs); i < startMs.Length && startMs[i] < toMs; i++)
        {
            int end = Math.Min(i + 1 < startMs.Length ? startMs[i + 1] : endMs, toMs);
            if (end <= fromMs) continue;
            int endCs = (int)Math.Round((end - fromMs) / speed / 10.0, MidpointRounding.AwayFromZero);
            int delay = endCs - shownCs;
            shownCs = endCs;
            int last = frames.Count - 1;
            if (last >= 0 && delays[last] < MinDelayCs) { frames[last] = i; delays[last] += delay; }
            else { frames.Add(i); delays.Add(delay); }
        }
        int n = frames.Count;
        if (n > 1 && delays[n - 1] < MinDelayCs)
        {
            delays[n - 2] += delays[n - 1];
            frames.RemoveAt(n - 1);
            delays.RemoveAt(n - 1);
        }
        else if (n == 1) delays[0] = Math.Max(MinDelayCs, delays[0]);
        return new GifPlan(frames.ToArray(), delays.ToArray());
    }

    public static Size Scaled(Size size, int percent)
    {
        return new Size(Math.Max(1, (int)Math.Round(size.Width * percent / 100.0)), Math.Max(1, (int)Math.Round(size.Height * percent / 100.0)));
    }

    // ---- pixels ------------------------------------------------------------------

    public static void ReadPixels(Bitmap bmp, byte[] into)
    {
        BitmapData d = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try { Marshal.Copy(d.Scan0, into, 0, into.Length); }
        finally { bmp.UnlockBits(d); }
    }

    public static void CopyInto(byte[] pixels, Bitmap bmp)
    {
        BitmapData d = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try { Marshal.Copy(pixels, 0, d.Scan0, pixels.Length); }
        finally { bmp.UnlockBits(d); }
    }

    public static Bitmap ToBitmap(byte[] pixels, Size size)
    {
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        CopyInto(pixels, bmp);
        return bmp;
    }

    public static byte[] Scale(byte[] pixels, Size from, Size to)
    {
        if (from == to) return pixels;
        using (Bitmap src = ToBitmap(pixels, from))
        using (var dst = new Bitmap(to.Width, to.Height, PixelFormat.Format32bppRgb))
        using (var wrap = new ImageAttributes())
        {
            wrap.SetWrapMode(WrapMode.TileFlipXY); // no dark seam along the edges
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(Point.Empty, to), 0, 0, from.Width, from.Height, GraphicsUnit.Pixel, wrap);
            }
            var out_ = new byte[to.Width * to.Height * 4];
            ReadPixels(dst, out_);
            return out_;
        }
    }

    public static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    // ---- encoder -------------------------------------------------------------------

    sealed class Pending
    {
        public Rectangle Box;
        public byte[] Palette, Data; // Data: the LZW sub-blocks
        public int DelayCs;
    }

    // Writes an animated GIF that loops forever. frameAt(i) gives frame i as BGRX rows of width x height;
    // it is called from several threads at once. Each frame stores only the box of pixels that changed from the
    // frame before, with its own palette; the rest is transparent. A frame with no change adds its delay to the
    // one before. Frames depend only on the frame before them, so a batch of frames encodes in parallel, one per core.
    // progress(n) after n frames.
    public static void Write(Stream output, int width, int height, int count, Func<int, byte[]> frameAt, int[] delaysCs, Action<int> progress)
    {
        var o = new BinaryWriter(output);
        o.Write(Encoding.ASCII.GetBytes("GIF89a"));
        o.Write((ushort)width);
        o.Write((ushort)height);
        o.Write(new byte[] { 0, 0, 0 }); // no global palette; every frame has its own
        o.Write(new byte[] { 0x21, 0xFF, 0x0B });
        o.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        o.Write(new byte[] { 3, 1, 0, 0, 0 }); // loop forever

        int batch = Math.Max(2, Environment.ProcessorCount); // frames in memory at once: pixels and keys of each
        Pending pending = null;
        ushort[] before = null; // the keys of the frame before the batch
        using (var quants = new ThreadLocal<Quantizer>(() => new Quantizer()))
        using (var lzws = new ThreadLocal<Lzw>(() => new Lzw()))
        {
            for (int first = 0; first < count; first += batch)
            {
                int size = Math.Min(batch, count - first);
                var pixels = new byte[size][];
                var keys = new ushort[size][];
                Parallel.For(0, size, i => { pixels[i] = frameAt(first + i); keys[i] = Keys(pixels[i]); });
                var done = new Pending[size];
                ushort[] head = before;
                Parallel.For(0, size, i => done[i] = EncodeFrame(pixels[i], keys[i], i == 0 ? head : keys[i - 1], width, height, quants.Value, lzws.Value));
                for (int i = 0; i < size; i++)
                {
                    int f = first + i;
                    if (done[i] == null) pending.DelayCs += delaysCs[f]; // the first frame always changes every pixel
                    else
                    {
                        if (pending != null) WriteFrame(o, pending);
                        pending = done[i];
                        pending.DelayCs = delaysCs[f];
                    }
                    if (progress != null) progress(f + 1);
                }
                before = keys[size - 1];
            }
        }
        if (pending != null) WriteFrame(o, pending);
        o.Write((byte)0x3B);
        o.Flush();
    }

    // The 15-bit color of each pixel; two pixels with the same key get the same palette entry.
    static ushort[] Keys(byte[] px)
    {
        var keys = new ushort[px.Length / 4];
        for (int p = 0, b = 0; p < keys.Length; p++, b += 4)
            keys[p] = (ushort)((px[b + 2] >> 3) << 10 | (px[b + 1] >> 3) << 5 | px[b] >> 3);
        return keys;
    }

    // One frame against the frame before it (prev null: the first frame, where every pixel changes).
    // Returns null when no pixel changed.
    static Pending EncodeFrame(byte[] px, ushort[] keys, ushort[] prev, int width, int height, Quantizer quant, Lzw lzw)
    {
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                if (prev != null && keys[p] == prev[p]) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                y1 = y;
            }
        }
        if (x1 < 0) return null;
        Rectangle box = Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        quant.Begin();
        for (int y = box.Top; y < box.Bottom; y++)
            for (int p = y * width + box.Left, e = y * width + box.Right; p < e; p++)
                if (prev == null || keys[p] != prev[p]) quant.Add(keys[p], px[4 * p + 2], px[4 * p + 1], px[4 * p]);
        byte[] palette = quant.Build(Transparent);
        var indices = new byte[box.Width * box.Height];
        int k = 0;
        for (int y = box.Top; y < box.Bottom; y++)
            for (int p = y * width + box.Left, e = y * width + box.Right; p < e; p++, k++)
                indices[k] = prev != null && keys[p] == prev[p] ? (byte)Transparent : quant.Index(keys[p]);
        using (var data = new MemoryStream())
        {
            var w = new BinaryWriter(data);
            lzw.Write(w, indices);
            w.Flush();
            return new Pending { Box = box, Palette = palette, Data = data.ToArray() };
        }
    }

    static void WriteFrame(BinaryWriter o, Pending f)
    {
        o.Write(new byte[] { 0x21, 0xF9, 4, 1 << 2 | 1 }); // keep the frame under the next one; index 255 is transparent
        o.Write((ushort)Math.Min(ushort.MaxValue, f.DelayCs));
        o.Write((byte)Transparent);
        o.Write((byte)0);
        o.Write((byte)0x2C);
        o.Write((ushort)f.Box.X);
        o.Write((ushort)f.Box.Y);
        o.Write((ushort)f.Box.Width);
        o.Write((ushort)f.Box.Height);
        o.Write((byte)0x87); // a local palette of 256 colors
        o.Write(f.Palette);
        o.Write(f.Data);
    }

    // Median cut over 15-bit colors: the box with the most pixels times the widest channel splits at its median,
    // until there are maxColors boxes. Each palette color is the true average of its box.
    sealed class Quantizer
    {
        sealed class Box
        {
            public int Start, End, Channel, Range;
            public long Pixels;
        }

        readonly int[] count = new int[32768], index = new int[32768];
        readonly long[] sumR = new long[32768], sumG = new long[32768], sumB = new long[32768];
        readonly List<int> present = new List<int>();

        public void Begin()
        {
            foreach (int key in present) { count[key] = 0; sumR[key] = sumG[key] = sumB[key] = 0; }
            present.Clear();
        }

        public void Add(int key, int r, int g, int b)
        {
            if (count[key]++ == 0) present.Add(key);
            sumR[key] += r;
            sumG[key] += g;
            sumB[key] += b;
        }

        public byte Index(int key) { return (byte)index[key]; }

        static int Channel(int key, int c) { return (key >> (10 - 5 * c)) & 31; }

        Box Measure(int[] order, int start, int end)
        {
            var box = new Box { Start = start, End = end };
            for (int c = 0; c < 3; c++)
            {
                int lo = 31, hi = 0;
                for (int i = start; i < end; i++) { int v = Channel(order[i], c); if (v < lo) lo = v; if (v > hi) hi = v; }
                if (hi - lo > box.Range) { box.Range = hi - lo; box.Channel = c; }
            }
            for (int i = start; i < end; i++) box.Pixels += count[order[i]];
            return box;
        }

        public byte[] Build(int maxColors)
        {
            int[] order = present.ToArray();
            var values = new int[order.Length];
            var boxes = new List<Box> { Measure(order, 0, order.Length) };
            while (boxes.Count < maxColors)
            {
                Box best = null;
                foreach (Box b in boxes)
                    if (b.Range > 0 && (best == null || b.Pixels * b.Range > best.Pixels * best.Range)) best = b;
                if (best == null) break; // every box is one color
                for (int i = best.Start; i < best.End; i++) values[i] = Channel(order[i], best.Channel);
                Array.Sort(values, order, best.Start, best.End - best.Start);
                long half = best.Pixels / 2, seen = 0;
                int cut = best.Start;
                while (cut < best.End - 1 && seen + count[order[cut]] <= half) seen += count[order[cut++]];
                cut = Math.Max(best.Start + 1, cut);
                boxes.Remove(best);
                boxes.Add(Measure(order, best.Start, cut));
                boxes.Add(Measure(order, cut, best.End));
            }
            var palette = new byte[768];
            for (int i = 0; i < boxes.Count; i++)
            {
                Box b = boxes[i];
                long r = 0, g = 0, bl = 0;
                for (int j = b.Start; j < b.End; j++) { int key = order[j]; r += sumR[key]; g += sumG[key]; bl += sumB[key]; index[key] = i; }
                palette[3 * i] = (byte)((r + b.Pixels / 2) / b.Pixels);
                palette[3 * i + 1] = (byte)((g + b.Pixels / 2) / b.Pixels);
                palette[3 * i + 2] = (byte)((bl + b.Pixels / 2) / b.Pixels);
            }
            return palette;
        }
    }
    // GIF LZW for 8-bit indices: codes grow from 9 to 12 bits, and a clear code restarts a full table.
    sealed class Lzw
    {
        const int Clear = 256, End = 257, Max = 4096;
        readonly int[] code = new int[Max * 256], stamp = new int[Max * 256];
        int generation;
        readonly byte[] block = new byte[255];
        int blockLength, bits, bitCount, size;
        BinaryWriter o;

        public void Write(BinaryWriter output, byte[] indices)
        {
            o = output;
            o.Write((byte)8);
            blockLength = bits = bitCount = 0;
            size = 9;
            int next = 258;
            generation++;
            Emit(Clear);
            int prefix = indices[0];
            for (int i = 1; i < indices.Length; i++)
            {
                int c = indices[i], slot = prefix << 8 | c;
                if (stamp[slot] == generation) { prefix = code[slot]; continue; }
                Emit(prefix);
                if (next < Max)
                {
                    code[slot] = next;
                    stamp[slot] = generation;
                    if (next == 1 << size && size < 12) size++;
                    next++;
                }
                else
                {
                    Emit(Clear);
                    generation++;
                    size = 9;
                    next = 258;
                }
                prefix = c;
            }
            Emit(prefix);
            if (next == 1 << size && size < 12) size++; // the decoder adds an entry before it reads the end code
            Emit(End);
            if (bitCount > 0) Put((byte)bits);
            if (blockLength > 0) Flush();
            o.Write((byte)0);
        }

        void Emit(int value)
        {
            bits |= value << bitCount;
            bitCount += size;
            while (bitCount >= 8)
            {
                Put((byte)bits);
                bits >>= 8;
                bitCount -= 8;
            }
        }

        void Put(byte b)
        {
            block[blockLength++] = b;
            if (blockLength == 255) Flush();
        }

        void Flush()
        {
            o.Write((byte)blockLength);
            o.Write(block, 0, blockLength);
            blockLength = 0;
        }
    }
}