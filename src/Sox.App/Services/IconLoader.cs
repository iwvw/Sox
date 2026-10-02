using System.Collections.Concurrent;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Sox.App.Services;

/// <summary>
/// Resolves an engine's icon spec into a WinUI image. Three forms are accepted so a user can point at a
/// local file, an Iconify icon (the huge open icon packs at iconify.design), or a plain image URL:
///   - "logos:google-icon" / "mdi:github"  -> fetched from api.iconify.design as SVG
///   - "https://.../x.png" or ".../x.svg"  -> fetched directly
///   - "C:\icons\foo.ico"                  -> loaded from disk
/// SVG is rasterised with Win2D's Direct2D SVG renderer at the exact pixel size the caller will display,
/// so the icon is shown 1:1 and never minified -- downscaling a 256px raster to a ~28px box produced
/// visibly aliased edges. Results are cached per (spec, size); failures resolve to null so the caller
/// falls back to the glyph.
/// </summary>
internal static class IconLoader
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<Action<ImageSource>>> Waiters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for the "prefix:name" Iconify shorthand (e.g. "logos:github-icon"). A Windows drive
    /// path like "C:\..." also contains a colon, so exclude single-letter drives and any real path.</summary>
    public static bool IsIconifyName(string spec) =>
        spec.Length > 2
        && spec.Contains(':')
        && !spec.Contains('\\')
        && !spec.Contains('/')
        && spec.IndexOf(':') > 1;

    /// <summary>True when the spec is something this loader can render directly: an Iconify name, an
    /// http(s) image URL, or a local image/svg file. A plain executable/shortcut path returns false, so
    /// the caller routes it to the shell-icon extractor instead.</summary>
    public static bool IsIconSpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return false;

        spec = spec.Trim();
        if (IsIconifyName(spec))
            return true;

        if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsImageExtension(Path.GetExtension(spec));
    }

    private static bool IsImageExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" => true,
        _ => false,
    };

    /// <summary>Returns a cached image when ready, otherwise null and kicks off a background load that
    /// invokes <paramref name="onLoaded"/> on the UI thread (via <paramref name="dispatcher"/>) once
    /// resolved. <paramref name="pixelSize"/> is the physical pixel size the icon will occupy, so the
    /// raster/decode can match it exactly. Safe to call from the UI thread.</summary>
    public static ImageSource? Get(string? spec, DispatcherQueue dispatcher, Action<ImageSource> onLoaded, int pixelSize = 32)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        spec = spec.Trim();
        var size = Math.Clamp(pixelSize, 16, 512);
        var key = spec + "@" + size;

        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Waiters.AddOrUpdate(
            key,
            _ => [onLoaded],
            (_, list) =>
            {
                lock (list) list.Add(onLoaded);
                return list;
            });

        if (InFlight.TryAdd(key, 0))
        {
            _ = Task.Run(() => FetchAndBuildAsync(spec, size, key, dispatcher));
        }

        return null;
    }

    private static async Task FetchAndBuildAsync(string spec, int size, string key, DispatcherQueue dispatcher)
    {
        (byte[] Bytes, bool IsSvg)? payload = null;
        try
        {
            payload = await FetchAsync(spec).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning($"Icon '{spec}' failed to fetch: {ex.Message}");
        }

        // Rasterise SVG off the UI thread with Win2D at the display size; the result is a plain PNG.
        if (payload is { IsSvg: true } svg)
        {
            try
            {
                payload = (RasterizeSvgToPng(svg.Bytes, size), false);
            }
            catch (Exception ex)
            {
                Log.Warning($"Icon '{spec}' SVG rasterisation failed: {ex.Message}");
                payload = null;
            }
        }

        // BitmapImage has UI-thread affinity; build and publish it there.
        dispatcher.TryEnqueue(async () =>
        {
            ImageSource? image = null;
            try
            {
                if (payload is { } p)
                {
                    image = await FromBytesAsync(p.Bytes, size);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Icon '{spec}' failed to build: {ex.Message}");
            }
            finally
            {
                if (image is not null)
                {
                    Cache[key] = image;
                }

                InFlight.TryRemove(key, out _);
            }

            if (image is null)
            {
                Waiters.TryRemove(key, out _);
                return;
            }

            if (Waiters.TryRemove(key, out var callbacks))
            {
                List<Action<ImageSource>> snapshot;
                lock (callbacks) snapshot = [.. callbacks];
                foreach (var callback in snapshot)
                {
                    try { callback(image); } catch { /* a stale row's handler is harmless */ }
                }
            }
        });
    }

    private static async Task<(byte[] Bytes, bool IsSvg)> FetchAsync(string spec)
    {
        if (IsIconifyName(spec))
        {
            // Iconify API: /<prefix>/<name>.svg (the ":" becomes "/"). Request a square viewport so the
            // intrinsic size is well-defined; the actual raster size is chosen by the caller.
            var url = "https://api.iconify.design/" + spec.Replace(':', '/') + ".svg?height=256&width=256";
            return (await Http.GetByteArrayAsync(url).ConfigureAwait(false), true);
        }

        if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await Http.GetByteArrayAsync(spec).ConfigureAwait(false);
            return (bytes, spec.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
        }

        if (File.Exists(spec))
        {
            var bytes = await File.ReadAllBytesAsync(spec).ConfigureAwait(false);
            return (bytes, spec.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
        }

        throw new FileNotFoundException($"Icon spec not found: {spec}");
    }

    /// <summary>Rasterises SVG bytes to a PNG at <paramref name="size"/> square pixels using Win2D's
    /// Direct2D SVG renderer. Runs off the UI thread (D2D, not XAML).</summary>
    private static byte[] RasterizeSvgToPng(byte[] svgBytes, int size)
    {
        var xml = Encoding.UTF8.GetString(svgBytes);
        var device = CanvasDevice.GetSharedDevice();
        using var svg = CanvasSvgDocument.LoadFromXml(device, xml);
        using var target = new CanvasRenderTarget(device, size, size, 96);

        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            // DrawSvg does NOT scale the document to the target; it draws at the SVG's intrinsic size
            // (256 here). Scale explicitly so the artwork fills the requested square.
            var scale = (float)size / 256f;
            session.Transform = System.Numerics.Matrix3x2.CreateScale(scale, scale);
            session.DrawSvg(svg, new Windows.Foundation.Size(256, 256));
        }

        using var stream = new InMemoryRandomAccessStream();
        target.SaveAsync(stream, CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);

        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            reader.LoadAsync((uint)stream.Size).AsTask().GetAwaiter().GetResult();
            reader.ReadBytes(bytes);
        }

        return bytes;
    }

    private static async Task<ImageSource> FromBytesAsync(byte[] bytes, int pixelSize)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var image = new BitmapImage
        {
            // Decode straight to the display size with the decoder's filtered resampling; without this a
            // larger bitmap is minified on the GPU, which aliases.
            DecodePixelWidth = pixelSize,
            DecodePixelHeight = pixelSize,
        };
        await image.SetSourceAsync(stream);
        return image;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sox/" + typeof(IconLoader).Assembly.GetName().Version);
        }
        catch
        {
        }

        return client;
    }
}
