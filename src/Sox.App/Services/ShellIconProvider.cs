using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Sox.App.Services;

internal static class ShellIconProvider
{
    private const int DefaultSize = 64;
    private const int MaxPngCacheEntries = 4096;

    private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte[]> PngCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly StaIconWorker Worker = new();

    // Shell thumbnail extraction is the expensive path (it loads the actual content), so only media
    // types use it. Everything else gets its much cheaper type/system-image-list icon.
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif",
        // Video
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".mpg", ".mpeg",
        // Audio: the shell returns embedded cover art (album art) as the thumbnail, which is much more
        // useful in a row than the generic media-player icon.
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma", ".opus", ".aiff", ".alac",
        // Documents the shell thumbnails (first page / preview).
        ".pdf", ".docx", ".xlsx", ".pptx", ".epub",
    };

    public static BitmapImage? GetIcon(string path, bool isDir, int pixelSize = DefaultSize)
    {
        var key = (isDir ? "::dir" : (Path.GetExtension(path) is { Length: > 0 } ext ? ext : "::file"))
            + "@" + pixelSize;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var png = GetIconPng(path, isDir, pixelSize);
        if (png is null)
        {
            return null;
        }

        var image = CreateImage(png);
        Cache[key] = image;
        return image;
    }

    /// <summary>Renders the shell icon to PNG bytes. Safe to call from any thread (STA worker inside).</summary>
    public static byte[]? GetIconPng(string path, bool isDir, int pixelSize = DefaultSize)
    {
        var key = path + "|" + pixelSize;
        if (PngCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var png = Worker.Run(() => RenderPng(path, isDir, Math.Clamp(pixelSize, 16, 256)));
            if (png is not null)
            {
                // Bounded so a long session touching many distinct icons cannot grow without limit.
                if (PngCache.Count >= MaxPngCacheEntries)
                {
                    PngCache.Clear();
                }

                PngCache[key] = png;
            }

            return png;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load shell icon for '{path}'", ex);
            return null;
        }
    }

    /// <summary>Creates a WinUI image from PNG bytes. Must be called on the UI thread.</summary>
    public static BitmapImage CreateImage(byte[] png)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        try
        {
            writer.WriteBytes(png);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        finally
        {
            writer.Dispose();
        }

        stream.Seek(0);
        var image = new BitmapImage();
        image.SetSource(stream);
        return image;
    }

    /// <summary>
    /// Dedicated STA thread for shell/COM calls. <c>IShellItemImageFactory</c> fails with
    /// RPC_E_WRONG_THREAD when invoked from the thread pool (MTA).
    /// </summary>
    private sealed class StaIconWorker
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread[] _threads;

        public StaIconWorker(int count = 3)
        {
            _threads = new Thread[count];
            for (var i = 0; i < count; i++)
            {
                var thread = new Thread(() =>
                {
                    foreach (var work in _queue.GetConsumingEnumerable())
                    {
                        work();
                    }
                })
                {
                    IsBackground = true,
                    Name = $"Sox.ShellIcon{i}",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                _threads[i] = thread;
            }
        }

        public T Run<T>(Func<T> func)
        {
            T result = default!;
            using var done = new ManualResetEventSlim(false);
            _queue.Add(() =>
            {
                try
                {
                    result = func();
                }
                catch (Exception ex)
                {
                    Log.Error("Shell icon worker failed", ex);
                }
                finally
                {
                    done.Set();
                }
            });

            done.Wait();
            return result;
        }
    }

    private static byte[]? RenderPng(string path, bool isDir, int pixelSize)
    {
        var bitmap = LoadHighQuality(path, isDir, pixelSize);
        if (bitmap is null)
        {
            return null;
        }

        using (bitmap)
        using (var memory = new MemoryStream())
        {
            bitmap.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
    }

    private static Bitmap? LoadHighQuality(string path, bool isDir, int pixelSize)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        if (exists && !isDir && ThumbnailExtensions.Contains(Path.GetExtension(path)))
        {
            // Real content thumbnail (no ICONONLY) for media: Explorer shows the actual picture, and
            // ICONONLY instead yields the generic type icon with a page-corner overlay. Held back to
            // media extensions because it is the slow path; everything else uses the icons below.
            var thumbnail = TryShellItemImage(path, pixelSize, iconOnly: false);
            if (thumbnail is not null)
            {
                return thumbnail;
            }
        }

        if (exists)
        {
            // Ask the shell for the icon at the exact pixel size we will display it, exactly as Explorer
            // does. IShellItemImageFactory returns a SQUARE canvas with the art scaled to fit and picks
            // the file's best-matching native resolution -- an .exe that ships only 48px comes back as a
            // crisp 48x48, not a blurry upscale. The image-list path below does NOT size this way: its
            // jumbo tier leaves each file's art at whatever size/position the author drew it (a 48px exe
            // becomes a small blob in a 256 canvas, a .dll a centred 167x222), so trimming that produced
            // wildly different result sizes -- the exe-big / doc-small bug.
            var icon = TryShellItemImage(path, pixelSize, iconOnly: true);
            if (icon is not null)
            {
                return icon;
            }
        }

        if (exists)
        {
            // Fallback for the rare file the factory cannot open: the system image list, trimmed of the
            // shell's padding and padded back to a square so it matches the factory path's footprint.
            var fromImageList = TryImageList(path, pixelSize);
            if (fromImageList is not null)
            {
                return fromImageList;
            }
        }

        return TrySHGetFileInfo(path, isDir);
    }

    // ---- System image list (overlay-free, high resolution) ----

    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const int SHIL_EXTRALARGE = 2;
    private const int SHIL_JUMBO = 4;
    private const int ILD_TRANSPARENT = 0x1;
    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    private static Bitmap? TryImageList(string path, int pixelSize)
    {
        try
        {
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(
                path,
                0,
                ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_SYSICONINDEX);
            if (result == IntPtr.Zero)
            {
                return null;
            }

            // Jumbo first for crispness, then ExtraLarge; crop the padding the shell adds when a
            // small icon is centered inside a larger canvas.
            foreach (var shil in new[] { SHIL_JUMBO, SHIL_EXTRALARGE })
            {
                var bitmap = FromImageList(info.iIcon, shil);
                if (bitmap is null)
                {
                    continue;
                }

                var trimmed = TrimCenteredPadding(bitmap);
                if (trimmed.Width >= 24 && trimmed.Height >= 24)
                {
                    // Pad back to a square so every row's icon occupies the same footprint regardless
                    // of its art's aspect ratio (see CenterOnSquare). The icon box uses Uniform stretch.
                    return CenterOnSquare(trimmed);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning($"System image list lookup failed for '{path}': {ex.Message}");
            return null;
        }
    }

    private static Bitmap? FromImageList(int iIcon, int shil)
    {
        IImageList? list = null;
        try
        {
            var iid = IID_IImageList;
            if (SHGetImageList(shil, ref iid, out list) < 0 || list is null)
            {
                return null;
            }

            if (list.GetIcon(iIcon, ILD_TRANSPARENT, out var hicon) < 0 || hicon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return HIconToBitmap(hicon);
            }
            finally
            {
                DestroyIcon(hicon);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Image list icon failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (list is not null)
            {
                Marshal.ReleaseComObject(list);
            }
        }
    }

    private static Bitmap? HIconToBitmap(IntPtr hicon)
    {
        if (!GetIconInfo(hicon, out var info))
        {
            return null;
        }

        try
        {
            if (info.hbmColor == IntPtr.Zero)
            {
                return null;
            }

            return HBitmapToBitmap(info.hbmColor);
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero)
            {
                DeleteObject(info.hbmColor);
            }

            if (info.hbmMask != IntPtr.Zero)
            {
                DeleteObject(info.hbmMask);
            }
        }
    }

    private static Bitmap? TryShellItemImage(string path, int pixelSize, bool iconOnly)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            if (factory is null)
            {
                return null;
            }

            try
            {
                var size = new SIZE { cx = pixelSize, cy = pixelSize };
                var flags = SIIGBF_BIGGERSIZEOK | (iconOnly ? SIIGBF_ICONONLY : 0u);
                var hr = factory.GetImage(size, flags, out var hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return HBitmapToBitmap(hBitmap);
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"IShellItemImageFactory failed for '{path}': {ex.Message}");
            return null;
        }
    }

    // Center a real content thumbnail (which can be non-square, e.g. 256x141) on a square transparent
    // canvas so it renders at a consistent size in the fixed 28-DIP icon slot instead of being
    // letterboxed small by Uniform stretch.
    private static Bitmap FitSquare(Bitmap source, int pixelSize)
    {
        if (source.Width == source.Height)
        {
            return source;
        }

        try
        {
            var canvas = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var scale = Math.Min((float)pixelSize / source.Width, (float)pixelSize / source.Height);
                var w = (int)Math.Round(source.Width * scale);
                var h = (int)Math.Round(source.Height * scale);
                var x = (pixelSize - w) / 2;
                var y = (pixelSize - h) / 2;
                g.DrawImage(source, new Rectangle(x, y, w, h));
            }

            source.Dispose();
            return canvas;
        }
        catch
        {
            return source;
        }
    }

    private static Bitmap? TrySHGetFileInfo(string path, bool isDir)
    {
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | SHGFI_LARGEICON;
        var attribute = FILE_ATTRIBUTE_NORMAL;
        var lookupPath = path;

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attribute = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            lookupPath = isDir ? "folder" : (Path.GetFileName(path) is { Length: > 0 } name ? name : "file");
        }

        var result = SHGetFileInfo(lookupPath, attribute, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return HIconToBitmap(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    // Convert an HBITMAP (32bpp, top-down) to a managed Bitmap, preserving the alpha channel.
    // Image.FromHbitmap drops alpha, which made every icon render on a black square.
    private static Bitmap HBitmapToBitmap(IntPtr hBitmap)
    {
        GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var native);
        var width = native.bmWidth;
        var height = native.bmHeight;

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var hdc = GetDC(IntPtr.Zero);
            try
            {
                GetDIBits(hdc, hBitmap, 0, (uint)height, data.Scan0, ref info, DIB_RGB_COLORS);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        EnsureOpaqueIfAlphaMissing(bitmap);
        return bitmap;
    }

    // Some shell icons come back with a fully-zero alpha channel; treat those as opaque.
    private static void EnsureOpaqueIfAlphaMissing(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var hasAlpha = false;
            unsafe
            {
                var pixels = (byte*)data.Scan0;
                var count = data.Stride * bitmap.Height;
                for (var i = 3; i < count; i += 4)
                {
                    if (pixels[i] != 0)
                    {
                        hasAlpha = true;
                        break;
                    }
                }

                if (!hasAlpha)
                {
                    for (var i = 3; i < count; i += 4)
                    {
                        pixels[i] = 255;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    // The shell centers a small icon inside a larger canvas rather than upscaling it, leaving a
    // transparent frame. Cropping to the opaque content lets normal scaling fill the display size.
    // Only trims when the content is well short of the canvas, so ordinary full-size icons pass through.
    private static Bitmap TrimCenteredPadding(Bitmap source)
    {
        try
        {
            var width = source.Width;
            var height = source.Height;
            var data = source.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int left = width, right = -1, top = height, bottom = -1;
                unsafe
                {
                    var basePtr = (byte*)data.Scan0;
                    for (var y = 0; y < height; y++)
                    {
                        var row = basePtr + y * data.Stride;
                        for (var x = 0; x < width; x++)
                        {
                            if (row[x * 4 + 3] <= 8)
                            {
                                continue;
                            }

                            if (x < left) left = x;
                            if (x > right) right = x;
                            if (y < top) top = y;
                            if (y > bottom) bottom = y;
                        }
                    }
                }

                if (right < left || bottom < top)
                {
                    return source;
                }

                var contentWidth = right - left + 1;
                var contentHeight = bottom - top + 1;
                if (contentWidth >= width * 0.85 && contentHeight >= height * 0.85)
                {
                    return source;
                }

                var cropped = new Bitmap(contentWidth, contentHeight, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(cropped))
                {
                    g.DrawImage(
                        source,
                        new Rectangle(0, 0, contentWidth, contentHeight),
                        new Rectangle(left, top, contentWidth, contentHeight),
                        GraphicsUnit.Pixel);
                }

                source.Dispose();
                return cropped;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }
        catch
        {
            return source;
        }
    }

    // Pads a trimmed icon back out to a square, centered, WITHOUT scaling it. Icons are shown in a fixed
    // square box with Uniform stretch, so a wide-short icon (an .exe whose art is 48x40) fills the width
    // and looks large while a tall-narrow one (a document icon, 167x222) is height-limited and looks
    // small -- same box, visibly different sizes. Centering each on a square of its own longest side (no
    // resampling, no extra magnification) makes every icon occupy the same footprint with the art's true
    // proportions intact; the box's Uniform stretch then renders them at matching sizes.
    private static Bitmap CenterOnSquare(Bitmap source)
    {
        if (source.Width == source.Height)
        {
            return source;
        }

        var side = Math.Max(source.Width, source.Height);
        try
        {
            var square = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(square))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(source, (side - source.Width) / 2, (side - source.Height) / 2);
            }

            source.Dispose();
            return square;
        }
        catch
        {
            return source;
        }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_ICONONLY = 0x00000004;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    // Only slots up to GetIcon (index 7) are declared; the rest of the vtable is unused.
    // Order MUST match CommCtrl.h IImageList exactly or calls dispatch to the wrong method.
    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add();
        [PreserveSig] int ReplaceIcon();
        [PreserveSig] int SetOverlayImage();
        [PreserveSig] int Replace();
        [PreserveSig] int AddMasked();
        [PreserveSig] int Draw();
        [PreserveSig] int Remove();
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int nCount, out BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}