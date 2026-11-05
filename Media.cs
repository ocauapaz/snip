// Video recordings: what the speakers play (WASAPI loopback), frames kept as JPEG, and the MP4 file
// (H.264 video and AAC sound, written with Media Foundation). Pcm and Exporter decide; Loopback and Mp4 wrap Windows.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// What an export writes for a recording: a GIF, or an MP4 for a video recording.
static class Exporter
{
    public static string Extension(Recording rec) { return rec.Video ? ".mp4" : ".gif"; }

    public static string Noun(Recording rec) { return rec.Video ? "video" : "GIF"; }

    public static Size OutputSize(Recording rec, int percent) { return rec.Video ? Mp4.Fit(rec.Size, percent) : Gif.Scaled(rec.Size, percent); }

    // Writes fromMs to toMs of rec at path, at speed (a GIF only) and at percent of its size; progress(0 to 100).
    // A failed write leaves no half file behind.
    public static void Write(Recording rec, string path, int fromMs, int toMs, double speed, int percent, Action<int> progress)
    {
        try
        {
            if (rec.Video) { Mp4.Write(path, rec, fromMs, toMs, percent, progress); return; }
            GifPlan plan = Gif.Plan(rec.StartMs, rec.EndMs, fromMs, toMs, speed);
            Size to = Gif.Scaled(rec.Size, percent);
            using (var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                Gif.Write(f, to.Width, to.Height, plan.Frames.Length, i => Gif.Scale(rec.Pixels(plan.Frames[i]), rec.Size, to), plan.DelaysCs,
                    n => { if (progress != null) progress(n * 100 / plan.Frames.Length); });
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    // The whole recording at its full size, as a new file in Pictures\Snip.
    public static string Default(Recording rec)
    {
        return Store.NewFileAt("", Extension(rec), path => Write(rec, path, 0, rec.EndMs, 1, 100, null));
    }
}

static class Jpeg
{
    const long Quality = 90; // screen text stays sharp; the H.264 step loses more detail than this
    static readonly ImageCodecInfo Codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] Encode(byte[] pixels, Size size)
    {
        using (Bitmap bmp = Gif.ToBitmap(pixels, size))
        using (var ms = new MemoryStream())
        using (var args = new EncoderParameters(1))
        {
            args.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Quality);
            bmp.Save(ms, Codec, args);
            return ms.ToArray();
        }
    }

    // Back to BGRX rows of size.
    public static byte[] Decode(byte[] jpeg, Size size)
    {
        using (var ms = new MemoryStream(jpeg))
        using (var bmp = new Bitmap(ms))
        {
            if (bmp.Size != size) throw new InvalidDataException("A video frame is " + bmp.Width + " x " + bmp.Height + ", not " + size.Width + " x " + size.Height + ".");
            var px = new byte[size.Width * size.Height * 4];
            Gif.ReadPixels(bmp, px);
            return px;
        }
    }
}

// Sound as plain numbers: captured packets on a timeline, rate changes, WAV files.
static class Pcm
{
    public const int Rate = 48000; // the sound of a recording: 48 kHz, 2 channels, 16 bits
    const long Second = 10000000;  // 100 ns ticks

    public sealed class Packet
    {
        public long Hns;        // the time of its first frame in 100 ns ticks; long.MinValue: Windows gave no time
        public int Frames;
        public float[] Samples; // Frames x channels, interleaved; null: silence
    }

    // A QueryPerformanceCounter value (Stopwatch.GetTimestamp) in 100 ns ticks, the clock of a packet's Hns.
    public static long Hns(long ticks)
    {
        long f = Stopwatch.Frequency;
        return ticks / f * Second + ticks % f * Second / f;
    }

    // Puts the packets on a stereo timeline of length frames that starts at startHns. A packet that starts
    // within 20 ms of the end of the one before joins it with no gap or overlap (the times jitter a little);
    // a later one leaves silence before it (Windows sends nothing while nothing plays).
    public static float[] Assemble(IList<Packet> packets, int channels, int rate, long startHns, int length)
    {
        var stereo = new float[2 * length];
        long expected = long.MinValue;
        int join = rate / 50;
        foreach (Packet p in packets)
        {
            long at;
            if (p.Hns == long.MinValue)
            {
                if (expected == long.MinValue) continue; // no time and nothing before it: no place for it
                at = expected;
            }
            else
            {
                at = (long)Math.Floor((p.Hns - startHns) * (double)rate / Second);
                if (expected != long.MinValue && Math.Abs(at - expected) <= join) at = expected;
            }
            expected = at + p.Frames;
            if (p.Samples == null) continue;
            for (int i = 0; i < p.Frames; i++)
            {
                long o = at + i;
                if (o < 0 || o >= length) continue;
                int s = i * channels;
                float l = p.Samples[s], r = channels > 1 ? p.Samples[s + 1] : l;
                if (channels > 2) { float c = 0.7071f * p.Samples[s + 2]; l += c; r += c; } // surround: the center goes to both sides
                stereo[2 * o] = l;
                stereo[2 * o + 1] = r;
            }
        }
        return stereo;
    }

    // Stereo from rate `from` to rate `to` (linear), as 16-bit samples; louder than full scale is clipped.
    public static short[] To16(float[] stereo, int from, int to)
    {
        int frames = stereo.Length / 2;
        int n = (int)((long)frames * to / from);
        var o = new short[2 * n];
        for (int j = 0; j < n; j++)
        {
            double pos = (double)j * from / to;
            int i = Math.Min(frames - 1, (int)pos), i2 = Math.Min(frames - 1, i + 1);
            double f = pos - i;
            o[2 * j] = Clip(stereo[2 * i] * (1 - f) + stereo[2 * i2] * f);
            o[2 * j + 1] = Clip(stereo[2 * i + 1] * (1 - f) + stereo[2 * i2 + 1] * f);
        }
        return o;
    }

    static short Clip(double v) { return (short)Math.Round(Math.Max(-1, Math.Min(1, v)) * 32767); }

    // Interleaved samples of a WASAPI buffer as floats. bits: the size of each sample in the buffer.
    public static float[] ToFloat(byte[] raw, int samples, int bits, bool isFloat)
    {
        var o = new float[samples];
        if (isFloat && bits == 32) { Buffer.BlockCopy(raw, 0, o, 0, samples * 4); return o; }
        if (isFloat) throw new NotSupportedException("Sound with " + bits + "-bit float samples is not supported.");
        for (int i = 0; i < samples; i++)
        {
            if (bits == 16) o[i] = BitConverter.ToInt16(raw, 2 * i) / 32768f;
            else if (bits == 24) o[i] = ((raw[3 * i] | raw[3 * i + 1] << 8 | raw[3 * i + 2] << 16) << 8 >> 8) / 8388608f;
            else if (bits == 32) o[i] = BitConverter.ToInt32(raw, 4 * i) / 2147483648f;
            else throw new NotSupportedException("Sound with " + bits + "-bit samples is not supported.");
        }
        return o;
    }

    // A WAV file of frames first to end of 48 kHz stereo.
    public static byte[] Wav(short[] stereo, int first, int end)
    {
        first = Math.Max(0, Math.Min(first, stereo.Length / 2));
        int bytes = 4 * (Math.Max(first, Math.Min(end, stereo.Length / 2)) - first);
        var o = new byte[44 + bytes];
        using (var w = new BinaryWriter(new MemoryStream(o)))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + bytes);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)2);
            w.Write(Rate);
            w.Write(Rate * 4);
            w.Write((short)4);
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(bytes);
        }
        Buffer.BlockCopy(stereo, 4 * first, o, 44, bytes);
        return o;
    }
}

// Records what the default playback device plays (WASAPI loopback, shared mode) on its own thread.
// The microphone is not recorded.
sealed class Loopback
{
    const int LoopbackFlag = 0x20000;   // AUDCLNT_STREAMFLAGS_LOOPBACK
    const int SilentFlag = 2, TimeErrorFlag = 4;
    const int DeviceGone = unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED
    static readonly Guid FloatFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");

    readonly List<Pcm.Packet> packets = new List<Pcm.Packet>();
    Thread thread;
    volatile bool stopping;
    int channels, rate, blockAlign, bits;
    bool isFloat;
    string failure; // why the capture stopped before Finish

    Loopback() { }

    // Starts the capture; throws when Windows refuses it (no playback device, an unknown sound format).
    public static Loopback Start()
    {
        var lb = new Loopback();
        Exception failed = null;
        using (var ready = new ManualResetEvent(false))
        {
            lb.thread = new Thread(() => lb.Run(ready, e => failed = e)) { IsBackground = true, Name = "Snip sound" };
            lb.thread.SetApartmentState(ApartmentState.MTA);
            lb.thread.Start();
            ready.WaitOne();
        }
        if (failed != null)
        {
            lb.thread.Join();
            throw new InvalidOperationException(failed.Message, failed);
        }
        return lb;
    }

    public void Stop()
    {
        stopping = true;
        thread.Join();
    }

    // Stops the capture and returns the sound from startHns for lengthMs as 48 kHz stereo.
    // problem: why the sound ends early, or null.
    public short[] Finish(long startHns, int lengthMs, out string problem)
    {
        Stop();
        problem = failure;
        int length = (int)((long)lengthMs * rate / 1000);
        return Pcm.To16(Pcm.Assemble(packets, channels, rate, startHns, length), rate, Pcm.Rate);
    }

    void Run(ManualResetEvent ready, Action<Exception> fail)
    {
        var held = new List<object>();
        try
        {
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            try
            {
                var enumerator = new MMDeviceEnumeratorCo();
                held.Add(enumerator);
                IMMDevice device = ((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(0, 0); // eRender, eConsole
                held.Add(device);
                client = (IAudioClient)device.Activate(typeof(IAudioClient).GUID, 23, IntPtr.Zero); // CLSCTX_ALL
                held.Add(client);
                IntPtr format;
                client.GetMixFormat(out format);
                try
                {
                    ReadFormat(format);
                    client.Initialize(0, LoopbackFlag, 20000000, 0, format, IntPtr.Zero); // shared, a 2 s buffer
                }
                finally { Marshal.FreeCoTaskMem(format); }
                capture = (IAudioCaptureClient)client.GetService(typeof(IAudioCaptureClient).GUID);
                held.Add(capture);
                client.Start();
            }
            catch (Exception e)
            {
                fail(e);
                return;
            }
            finally { ready.Set(); }

            try
            {
                while (!stopping)
                {
                    Thread.Sleep(10);
                    Drain(capture);
                }
                Drain(capture);
                client.Stop();
            }
            catch (Exception e)
            {
                failure = "The sound stopped early: " + e.Message;
            }
        }
        finally
        {
            foreach (object o in held) Marshal.ReleaseComObject(o);
        }
    }

    void ReadFormat(IntPtr f)
    {
        int tag = (ushort)Marshal.ReadInt16(f, 0);
        channels = Marshal.ReadInt16(f, 2);
        rate = Marshal.ReadInt32(f, 4);
        blockAlign = Marshal.ReadInt16(f, 12);
        if (channels < 1 || rate < 1 || blockAlign < channels) throw new NotSupportedException("The playback device has an unknown sound format.");
        bits = blockAlign * 8 / channels;
        isFloat = tag == 3; // WAVE_FORMAT_IEEE_FLOAT
        if (tag == 0xFFFE)  // WAVE_FORMAT_EXTENSIBLE: the sample type is its SubFormat
        {
            var g = new byte[16];
            Marshal.Copy(f + 24, g, 0, 16);
            isFloat = new Guid(g) == FloatFormat;
        }
    }

    void Drain(IAudioCaptureClient capture)
    {
        int next;
        Check(capture.GetNextPacketSize(out next));
        while (next > 0)
        {
            IntPtr data;
            int frames, flags;
            long devicePosition, qpc;
            Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpc));
            var p = new Pcm.Packet { Frames = frames, Hns = (flags & TimeErrorFlag) != 0 ? long.MinValue : qpc };
            if ((flags & SilentFlag) == 0 && frames > 0)
            {
                var raw = new byte[frames * blockAlign];
                Marshal.Copy(data, raw, 0, raw.Length);
                p.Samples = Pcm.ToFloat(raw, frames * channels, bits, isFloat);
            }
            Check(capture.ReleaseBuffer(frames));
            packets.Add(p);
            Check(capture.GetNextPacketSize(out next));
        }
    }

    static void Check(int hr)
    {
        if (hr == DeviceGone) throw new IOException("the playback device changed or was turned off.");
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints();
        IMMDevice GetDefaultAudioEndpoint(int dataFlow, int role);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx, IntPtr activationParams);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize();
        void GetStreamLatency();
        void GetCurrentPadding();
        void IsFormatSupported();
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod();
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetService([MarshalAs(UnmanagedType.LPStruct)] Guid iid);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }
}

// An MP4 file of a video recording: H.264 at a constant 30 frames a second, and AAC sound when it has sound.
static class Mp4
{
    public const int Fps = 30;
    const long Second = 10000000;
    const int AudioChunk = 1024;   // sound frames in each sample written
    const int Version = 0x00020070; // MF_VERSION

    static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid AvgBitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid InterlaceMode = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid PixelAspect = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid AudioChannels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    static readonly Guid AudioRate = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    static readonly Guid AudioBits = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    static readonly Guid AudioBytesPerSecond = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    static readonly Guid AudioBlockAlign = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
    static readonly Guid AllSamplesIndependent = new Guid("c9173739-5e56-461c-b713-46fb995cb95f");
    static readonly Guid DisableThrottling = new Guid("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    static readonly Guid ContainerType = new Guid("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    static readonly Guid Mpeg4Container = new Guid("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    static readonly Guid VideoType = new Guid("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid AudioType = new Guid("73647561-0000-0010-8000-00AA00389B71");
    static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid Rgb32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
    static readonly Guid Aac = new Guid("00001610-0000-0010-8000-00AA00389B71");
    static readonly Guid PcmType = new Guid("00000001-0000-0010-8000-00AA00389B71");

    // H.264 needs an even width and height.
    public static Size Fit(Size size, int percent)
    {
        Size s = Gif.Scaled(size, percent);
        return new Size(Math.Max(2, s.Width & ~1), Math.Max(2, s.Height & ~1));
    }

    // 0.05 bits a pixel: screen video stays sharp and a minute of Full HD is about 25 MB.
    public static int Bitrate(Size s) { return (int)Math.Max(500000, Math.Min(8000000, (long)s.Width * s.Height * Fps / 20)); }

    public static int FrameCount(int fromMs, int toMs) { return Math.Max(1, (int)Math.Ceiling((toMs - fromMs) * (double)Fps / 1000)); }

    // Frame pixels of size `from` at size `to`: at full size an odd last row or column is cut off, else the frame is scaled.
    public static byte[] Fitted(byte[] px, Size from, Size to)
    {
        if (to == from) return px;
        if (to.Width <= from.Width && to.Height <= from.Height && from.Width - to.Width <= 1 && from.Height - to.Height <= 1)
        {
            var o = new byte[to.Width * to.Height * 4];
            for (int y = 0; y < to.Height; y++) Buffer.BlockCopy(px, y * from.Width * 4, o, y * to.Width * 4, to.Width * 4);
            return o;
        }
        return Gif.Scale(px, from, to);
    }

    // Writes fromMs to toMs of a video recording at path; progress(0 to 100).
    public static void Write(string path, Recording rec, int fromMs, int toMs, int percent, Action<int> progress)
    {
        Size to = Fit(rec.Size, percent);
        int count = FrameCount(fromMs, toMs);
        short[] audio = rec.Audio;
        int audioFirst = 0, audioEnd = 0;
        if (audio != null)
        {
            audioFirst = Math.Min(audio.Length / 2, (int)((long)fromMs * Pcm.Rate / 1000));
            audioEnd = Math.Min(audio.Length / 2, (int)((long)toMs * Pcm.Rate / 1000));
        }
        bool sound = audioEnd > audioFirst;

        MFStartup(Version, 0);
        IMFSinkWriter writer = null;
        try
        {
            IMFAttributes attrs;
            MFCreateAttributes(out attrs, 2);
            try
            {
                attrs.SetUINT32(DisableThrottling, 1);
                attrs.SetGUID(ContainerType, Mpeg4Container);
                MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out writer);
            }
            finally { Marshal.ReleaseComObject(attrs); }
            int videoStream = AddVideo(writer, to);
            int soundStream = sound ? AddSound(writer) : -1;
            writer.BeginWriting();

            // Frames decode in parallel batches; a frame equal to the one before is not decoded again.
            int batch = Math.Max(2, Environment.ProcessorCount);
            int lastIndex = -1, audioAt = audioFirst;
            byte[] lastPx = null;
            for (int first = 0; first < count; first += batch)
            {
                int n = Math.Min(batch, count - first);
                var index = new int[n];
                for (int i = 0; i < n; i++) index[i] = Gif.FrameAt(rec.StartMs, fromMs + (int)((long)(first + i) * 1000 / Fps));
                var px = new byte[n][];
                int known = lastIndex;
                Parallel.For(0, n, i =>
                {
                    if (index[i] != known && (i == 0 || index[i] != index[i - 1])) px[i] = Fitted(rec.Pixels(index[i]), rec.Size, to);
                });
                for (int i = 0; i < n; i++)
                {
                    if (px[i] == null) px[i] = i == 0 ? lastPx : px[i - 1];
                    long k = first + i, time = k * Second / Fps, end = (k + 1) * Second / Fps;
                    if (sound) audioAt = WriteSound(writer, soundStream, audio, audioFirst, audioAt, (int)Math.Min(audioEnd, audioFirst + end * Pcm.Rate / Second));
                    WriteSample(writer, videoStream, px[i], time, end - time);
                    if (progress != null) progress((int)((k + 1) * 100 / count));
                }
                lastIndex = index[n - 1];
                lastPx = px[n - 1];
            }
            if (sound) WriteSound(writer, soundStream, audio, audioFirst, audioAt, audioEnd);
            writer.DoFinalize();
        }
        finally
        {
            if (writer != null) Marshal.ReleaseComObject(writer);
            MFShutdown();
        }
    }

    static int AddVideo(IMFSinkWriter writer, Size size)
    {
        long frameSize = (long)size.Width << 32 | (uint)size.Height;
        long rate = (long)Fps << 32 | 1;
        long square = 1L << 32 | 1;
        IMFMediaType output, input;
        MFCreateMediaType(out output);
        MFCreateMediaType(out input);
        try
        {
            output.SetGUID(MajorType, VideoType);
            output.SetGUID(Subtype, H264);
            output.SetUINT32(AvgBitrate, Bitrate(size));
            output.SetUINT32(InterlaceMode, 2); // progressive
            output.SetUINT64(FrameSize, frameSize);
            output.SetUINT64(FrameRate, rate);
            output.SetUINT64(PixelAspect, square);
            int index;
            writer.AddStream(output, out index);
            input.SetGUID(MajorType, VideoType);
            input.SetGUID(Subtype, Rgb32);
            input.SetUINT32(InterlaceMode, 2);
            input.SetUINT64(FrameSize, frameSize);
            input.SetUINT64(FrameRate, rate);
            input.SetUINT64(PixelAspect, square);
            input.SetUINT32(DefaultStride, size.Width * 4); // a positive stride: the rows go top to bottom
            writer.SetInputMediaType(index, input, null);
            return index;
        }
        finally
        {
            Marshal.ReleaseComObject(output);
            Marshal.ReleaseComObject(input);
        }
    }

    static int AddSound(IMFSinkWriter writer)
    {
        IMFMediaType output, input;
        MFCreateMediaType(out output);
        MFCreateMediaType(out input);
        try
        {
            output.SetGUID(MajorType, AudioType);
            output.SetGUID(Subtype, Aac);
            output.SetUINT32(AudioBits, 16);
            output.SetUINT32(AudioRate, Pcm.Rate);
            output.SetUINT32(AudioChannels, 2);
            output.SetUINT32(AudioBytesPerSecond, 16000); // 128 kbit/s
            int index;
            writer.AddStream(output, out index);
            input.SetGUID(MajorType, AudioType);
            input.SetGUID(Subtype, PcmType);
            input.SetUINT32(AudioBits, 16);
            input.SetUINT32(AudioRate, Pcm.Rate);
            input.SetUINT32(AudioChannels, 2);
            input.SetUINT32(AudioBlockAlign, 4);
            input.SetUINT32(AudioBytesPerSecond, Pcm.Rate * 4);
            input.SetUINT32(AllSamplesIndependent, 1);
            writer.SetInputMediaType(index, input, null);
            return index;
        }
        finally
        {
            Marshal.ReleaseComObject(output);
            Marshal.ReleaseComObject(input);
        }
    }

    // Writes sound frames at to until (origin: the frame at time 0 of the file); returns until.
    static int WriteSound(IMFSinkWriter writer, int stream, short[] audio, int origin, int at, int until)
    {
        while (at < until)
        {
            int n = Math.Min(AudioChunk, until - at);
            var bytes = new byte[n * 4];
            Buffer.BlockCopy(audio, at * 4, bytes, 0, bytes.Length);
            long time = (long)(at - origin) * Second / Pcm.Rate, end = (long)(at + n - origin) * Second / Pcm.Rate;
            WriteSample(writer, stream, bytes, time, end - time);
            at += n;
        }
        return at;
    }

    static void WriteSample(IMFSinkWriter writer, int stream, byte[] data, long time, long duration)
    {
        IMFMediaBuffer buffer;
        MFCreateMemoryBuffer(data.Length, out buffer);
        IMFSample sample = null;
        try
        {
            IntPtr at;
            int max, current;
            buffer.Lock(out at, out max, out current);
            try { Marshal.Copy(data, 0, at, data.Length); }
            finally { buffer.Unlock(); }
            buffer.SetCurrentLength(data.Length);
            MFCreateSample(out sample);
            sample.AddBuffer(buffer);
            sample.SetSampleTime(time);
            sample.SetSampleDuration(duration);
            writer.WriteSample(stream, sample);
        }
        finally
        {
            if (sample != null) Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateAttributes(out IMFAttributes attributes, int size);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter writer);

    // Only the methods Snip calls have real signatures; the others hold their place in the vtable.
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize();
        void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
        void SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
        void SetDouble();
        void SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize();
        void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
        void SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
        void SetDouble();
        void SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize();
        void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob();
        void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime();
        void SetSampleTime(long time);
        void GetSampleDuration();
        void SetSampleDuration(long duration);
        void GetBufferCount(); void GetBufferByIndex(); void ConvertToContiguousBuffer();
        void AddBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        void Unlock();
        void GetCurrentLength();
        void SetCurrentLength(int length);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSinkWriter
    {
        void AddStream(IMFMediaType type, out int index);
        void SetInputMediaType(int index, IMFMediaType type, IMFAttributes encodingParameters);
        void BeginWriting();
        void WriteSample(int index, IMFSample sample);
        void SendStreamTick(); void PlaceMarker(); void NotifyEndOfSegment(); void Flush();
        void DoFinalize();
    }
}
