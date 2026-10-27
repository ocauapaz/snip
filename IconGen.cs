// Writes snip.ico (16 to 256 px, PNG frames) from Ui.DrawLogo, for the exe icon. build.cmd runs it.
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

static class IconGen
{
    static int Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        byte[][] frames = sizes.Select(Png).ToArray();
        using (var w = new BinaryWriter(File.Create(args[0])))
        {
            w.Write((short)0); // reserved
            w.Write((short)1); // type: icon
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte side = (byte)(sizes[i] >= 256 ? 0 : sizes[i]); // 0 means 256
                w.Write(side);
                w.Write(side);
                w.Write((byte)0);  // no palette
                w.Write((byte)0);  // reserved
                w.Write((short)1); // planes
                w.Write((short)32); // bits per pixel
                w.Write(frames[i].Length);
                w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] f in frames) w.Write(f);
        }
        return 0;
    }

    static byte[] Png(int size)
    {
        using (var bmp = new Bitmap(size, size))
        using (var ms = new MemoryStream())
        {
            using (var g = Graphics.FromImage(bmp)) Ui.DrawLogo(g, size);
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}
