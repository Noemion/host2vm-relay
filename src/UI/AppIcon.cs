using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Host2VMRelay;

internal static class AppIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    public static Icon Load(int pixels)
    {
        if (pixels <= 0) throw new ArgumentOutOfRangeException(nameof(pixels));
        pixels = Math.Min(pixels, 256);
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Host2VMRelay.AppIcon")
            ?? throw new InvalidOperationException("应用图标资源缺失。");
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1) throw new InvalidDataException("无效的 ICO 资源。");
        int count = reader.ReadUInt16();
        if (count == 0 || 6L + count * 16L > stream.Length) throw new InvalidDataException("ICO 目录不完整。");
        uint offset = 0, length = 0;
        int bestScore = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            stream.Position = 6 + i * 16;
            int width = reader.ReadByte(), height = reader.ReadByte();
            if (width == 0) width = 256;
            if (height == 0) height = 256;
            stream.Position += 6;
            uint bytes = reader.ReadUInt32(), start = reader.ReadUInt32();
            if (width != height || bytes == 0 || start < 6 + count * 16 || (long)start + bytes > stream.Length)
                throw new InvalidDataException("ICO 图像范围无效。");
            // Prefer downsampling over enlarging a smaller frame (e.g. 28/56px at 175%).
            int score = width >= pixels ? width - pixels : 1024 + pixels - width;
            if (score < bestScore) { bestScore = score; offset = start; length = bytes; }
        }
        stream.Position = offset;
        byte[] data = reader.ReadBytes(checked((int)length));
        if (data.Length != length) throw new InvalidDataException("ICO 图像数据不完整。");
        using var imageStream = new MemoryStream(data, false);
        using var source = new Bitmap(imageStream);
        using var bitmap = new Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, pixels, pixels), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }
}
