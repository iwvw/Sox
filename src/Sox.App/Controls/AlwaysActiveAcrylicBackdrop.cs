using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Sox.App.Controls;

/// <summary>
/// Desktop acrylic that stays sampling the content behind it even while the window is
/// inactive or covered (IsInputActive pinned true). Mirrors Momomi/WSLCC so the settings
/// window keeps its material when it loses focus instead of degrading to a solid color.
/// </summary>
public sealed class AlwaysActiveAcrylicBackdrop : SystemBackdrop, IDisposable
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, Target> _targets = new();
    private bool _disposed;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        var config = BuildConfig(xamlRoot);
        var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        controller.SetSystemBackdropConfiguration(config);
        controller.AddSystemBackdropTarget(connectedTarget);

        _targets[connectedTarget] = new Target(controller, config);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        if (_targets.Remove(disconnectedTarget, out var target))
        {
            try
            {
                target.Controller.RemoveSystemBackdropTarget(disconnectedTarget);
            }
            catch
            {
                // Controller may already be detached.
            }

            target.Controller.Dispose();
        }
    }

    /// <summary>
    /// Keep existing controllers in sync with theme changes instead of rebuilding them.
    /// Rebuilding here throws because the old target is already invalid mid-swap.
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        if (_targets.TryGetValue(target, out var entry))
        {
            entry.Config.Theme = ResolveTheme(xamlRoot);
        }
    }

    /// <summary>
    /// Releasing the window's SystemBackdrop does not auto-detach controllers; disposing them
    /// here prevents leaking a DesktopAcrylicController on every material switch.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var kv in _targets)
        {
            try
            {
                kv.Value.Controller.RemoveSystemBackdropTarget(kv.Key);
            }
            catch
            {
                // Ignore detach failures during shutdown.
            }

            kv.Value.Controller.Dispose();
        }

        _targets.Clear();
    }

    private static SystemBackdropConfiguration BuildConfig(XamlRoot xamlRoot) => new()
    {
        IsInputActive = true,
        Theme = ResolveTheme(xamlRoot),
    };

    private static SystemBackdropTheme ResolveTheme(XamlRoot xamlRoot) =>
        xamlRoot.Content is FrameworkElement fe
            ? fe.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default,
            }
            : SystemBackdropTheme.Default;

    private sealed class Target
    {
        public Target(DesktopAcrylicController controller, SystemBackdropConfiguration config)
        {
            Controller = controller;
            Config = config;
        }

        public DesktopAcrylicController Controller { get; }

        public SystemBackdropConfiguration Config { get; }
    }
}