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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
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
            try
            {
                // Premultiplied BGRA DIB; CreateBitmapSourceFromHBitmap would drop the alpha channel.
                GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp);
                if (bmp.bmBits == IntPtr.Zero || bmp.bmBitsPixel != 32) return null;
                var src = BitmapSource.Create(bmp.bmWidth, bmp.bmHeight, 96, 96, PixelFormats.Pbgra32, null,
                    bmp.bmBits, bmp.bmWidthBytes * bmp.bmHeight, bmp.bmWidthBytes);
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
