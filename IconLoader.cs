using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mindows;

/// <summary>Large shell icons for exe paths, shell:AppsFolder items and shell folders, cached per path.</summary>
static class IconLoader
{
    const int Size = 128;
    static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DIBSECTION { public BITMAP dsBm; public BITMAPINFOHEADER dsBmih; public uint dsBitfields0, dsBitfields1, dsBitfields2; public IntPtr dshSection; public uint dsOffset; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out DIBSECTION dib);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    public static ImageSource? Get(string path)
    {
        if (!cache.TryGetValue(path, out var image)) cache[path] = image = Load(path);
        return image;
    }

    static ImageSource? Load(string path)
    {
        IShellItemImageFactory factory;
        try { SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory); }
        catch (Exception e) when (e is COMException or FileNotFoundException or ArgumentException) { return null; }
        try
        {
            if (factory.GetImage(new SIZE { cx = Size, cy = Size }, 0x4 /* SIIGBF_ICONONLY */, out var hbm) != 0) return null;
            try { return ToBitmapSource(hbm); }
            finally { DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    /// <summary>Copies a 32bpp DIB section. CreateBitmapSourceFromHBitmap would drop the alpha channel.</summary>
    static BitmapSource? ToBitmapSource(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var dib) == 0) return null;
        var bm = dib.dsBm;
        if (bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32) return null;
        int stride = bm.bmWidthBytes, height = bm.bmHeight;
        var pixels = new byte[stride * height];
        Marshal.Copy(bm.bmBits, pixels, 0, pixels.Length);

        // A positive biHeight means the rows are stored bottom-up.
        if (dib.dsBmih.biHeight > 0)
        {
            var row = new byte[stride];
            for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
            {
                Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
                Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
                Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
            }
        }

        var format = AlphaFormat(pixels);
        var src = BitmapSource.Create(bm.bmWidth, height, 96, 96, format, null, pixels, stride);
        src.Freeze();
        return src;
    }

    /// <summary>
    /// Shell icons come back either premultiplied or with straight alpha; treating straight alpha
    /// as premultiplied leaves bright fringes. A color channel above its alpha can only be straight.
    /// Icons without any alpha are made opaque.
    /// </summary>
    static PixelFormat AlphaFormat(byte[] bgra)
    {
        bool straight = false, anyAlpha = false;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            anyAlpha |= a != 0;
            straight |= bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a;
        }
        if (anyAlpha) return straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return PixelFormats.Bgra32;
    }
}
