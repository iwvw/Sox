using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Sox.App.Services;
using Windows.UI;
using WindowsCompositionColorBrush = Windows.UI.Composition.CompositionColorBrush;
using WindowsCompositionCompositor = Windows.UI.Composition.Compositor;

namespace Sox.App.Materials;

internal sealed partial class TintedControllerBackdrop : SystemBackdrop, IDisposable
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, BackdropTarget?> _targets = [];

    private BackdropSettings? _settings;
    private bool _isBackdropAttached;
    private bool _isInputActive = true;
    private bool _isDisposed;

    public event Action<bool>? BackdropAttachmentChanged;

    public bool IsBackdropAttached => _isBackdropAttached;

    public bool IsInputActive
    {
        get => _isInputActive;
        set
        {
            _isInputActive = value;

            foreach (var target in _targets.Values)
            {
                target?.SetIsInputActive(value);
            }
        }
    }

    public void Update(BackdropParameters backdrop, BackdropControllerKind kind, bool isImageMode, bool hasColorization)
    {
        if (_isDisposed)
        {
            return;
        }

        var settings = new BackdropSettings(
            kind,
            Color.FromArgb(
                (byte)(backdrop.EffectiveOpacity * 255),
                backdrop.TintColor.R,
                backdrop.TintColor.G,
                backdrop.TintColor.B),
            backdrop.TintColor,
            isImageMode ? 0.0f : backdrop.EffectiveOpacity,
            backdrop.FallbackColor,
            backdrop.EffectiveLuminosityOpacity,
            hasColorization || isImageMode);
        _settings = settings;

        foreach (var (target, state) in _targets)
        {
            state?.Apply(target, settings);
        }

        UpdateBackdropAttachmentState();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        _targets[connectedTarget] = null;

        try
        {
            var target = new BackdropTarget(xamlRoot, _isInputActive, UpdateBackdropAttachmentState);
            _targets[connectedTarget] = target;

            if (!_isDisposed && _settings is { } settings)
            {
                target.Apply(connectedTarget, settings);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to connect controller-backed system backdrop", ex);
        }

        UpdateBackdropAttachmentState();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            base.OnTargetDisconnected(disconnectedTarget);
        }
        finally
        {
            if (_targets.Remove(disconnectedTarget, out var target))
            {
                target?.Close(disconnectedTarget);
            }

            UpdateBackdropAttachmentState();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _settings = null;

        foreach (var (target, state) in _targets)
        {
            state?.Close(target);
        }

        UpdateBackdropAttachmentState();
    }

    private void UpdateBackdropAttachmentState()
    {
        var isBackdropAttached = false;

        foreach (var target in _targets.Values)
        {
            if (target?.IsBackdropAttached == true)
            {
                isBackdropAttached = true;
                break;
            }
        }

        if (_isBackdropAttached == isBackdropAttached)
        {
            return;
        }

        _isBackdropAttached = isBackdropAttached;

        try
        {
            BackdropAttachmentChanged?.Invoke(isBackdropAttached);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update system backdrop fallback", ex);
        }
    }

    private static SystemBackdropTheme ResolveTheme(XamlRoot xamlRoot) =>
        xamlRoot.Content is FrameworkElement rootElement
            ? rootElement.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default,
            }
            : SystemBackdropTheme.Default;

    private readonly record struct BackdropSettings(
        BackdropControllerKind Kind,
        Color SolidColor,
        Color TintColor,
        float TintOpacity,
        Color FallbackColor,
        float LuminosityOpacity,
        bool ApplyTint);

    private sealed class BackdropTarget
    {
        private const int SolidAttachRetryCount = 2;

        private readonly XamlRoot _xamlRoot;
        private readonly SystemBackdropConfiguration _configuration;
        private readonly Action _backdropAttachmentChanged;

        private BackdropSettings? _appliedSettings;
        private BackdropSettings? _queuedSettings;
        private WindowsCompositionCompositor? _solidColorCompositor;
        private WindowsCompositionColorBrush? _solidColorBrush;
        private MicaController? _micaController;
        private DesktopAcrylicController? _acrylicController;
        private int _queuedSolidAttachRetries;
        private bool _backdropHasTarget;
        private bool _isApplyQueued;
        private bool _isClosed;

        public bool IsBackdropAttached => _backdropHasTarget;

        public BackdropTarget(XamlRoot xamlRoot, bool isInputActive, Action backdropAttachmentChanged)
        {
            _xamlRoot = xamlRoot;
            _backdropAttachmentChanged = backdropAttachmentChanged;
            _configuration = new SystemBackdropConfiguration
            {
                IsInputActive = isInputActive,
                Theme = ResolveTheme(xamlRoot),
            };
        }

        public void SetIsInputActive(bool isInputActive)
        {
            if (!_isClosed)
            {
                _configuration.IsInputActive = isInputActive;
            }
        }

        public void Apply(ICompositionSupportsSystemBackdrop target, BackdropSettings settings)
        {
            if (_isClosed)
            {
                return;
            }

            if (_isApplyQueued)
            {
                _queuedSettings = settings;
                _queuedSolidAttachRetries = settings.Kind == BackdropControllerKind.Solid
                    ? SolidAttachRetryCount
                    : 0;
                return;
            }

            Apply(
                target,
                settings,
                deferSolidAttach: true,
                solidAttachRetriesRemaining: SolidAttachRetryCount);
        }

        public void Close(ICompositionSupportsSystemBackdrop target)
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;
            _queuedSettings = null;
            DetachBackdrop(target);
        }

        private void Apply(
            ICompositionSupportsSystemBackdrop target,
            BackdropSettings settings,
            bool deferSolidAttach,
            int solidAttachRetriesRemaining)
        {
            _configuration.Theme = ResolveTheme(_xamlRoot);

            if (_appliedSettings == settings)
            {
                return;
            }

            if (settings.Kind == BackdropControllerKind.Solid && _solidColorBrush is not null && _backdropHasTarget)
            {
                try
                {
                    _solidColorBrush.Color = settings.SolidColor;
                    _appliedSettings = settings;
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to update solid system backdrop tint", ex);
                }
            }

            DetachBackdrop(target);

            if (deferSolidAttach &&
                settings.Kind == BackdropControllerKind.Solid &&
                QueueApply(target, settings, SolidAttachRetryCount))
            {
                return;
            }

            try
            {
                switch (settings.Kind)
                {
                    case BackdropControllerKind.Solid:
                        AttachSolidColorBrush(target, settings);
                        break;

                    case BackdropControllerKind.Mica:
                    case BackdropControllerKind.MicaAlt:
                        AttachMicaController(target, settings);
                        break;

                    case BackdropControllerKind.Acrylic:
                    case BackdropControllerKind.AcrylicThin:
                    default:
                        AttachAcrylicController(target, settings);
                        break;
                }

                _appliedSettings = settings;
            }
            catch (UnauthorizedAccessException ex) when (settings.Kind == BackdropControllerKind.Solid)
            {
                DetachBackdrop(target);

                if (solidAttachRetriesRemaining > 0 &&
                    QueueApply(target, settings, solidAttachRetriesRemaining - 1))
                {
                    return;
                }

                Log.Warning(
                    $"Solid backdrop target remained unavailable after the native handoff; using the fallback background. HRESULT: 0x{ex.HResult:X8}.");
            }
            catch (Exception ex)
            {
                DetachBackdrop(target);
                Log.Error("Failed to apply composition-backed system backdrop", ex);
            }
        }

        private bool QueueApply(
            ICompositionSupportsSystemBackdrop target,
            BackdropSettings settings,
            int solidAttachRetriesRemaining)
        {
            if (_isClosed)
            {
                return false;
            }

            _queuedSettings = settings;
            _queuedSolidAttachRetries = solidAttachRetriesRemaining;

            if (_isApplyQueued)
            {
                return true;
            }

            _isApplyQueued = true;
            if (_xamlRoot.Content.DispatcherQueue.TryEnqueue(() => ApplyQueued(target)))
            {
                return true;
            }

            _isApplyQueued = false;
            _queuedSettings = null;
            _queuedSolidAttachRetries = 0;
            return false;
        }

        private void ApplyQueued(ICompositionSupportsSystemBackdrop target)
        {
            _isApplyQueued = false;

            var settings = _queuedSettings;
            var solidAttachRetriesRemaining = _queuedSolidAttachRetries;
            _queuedSettings = null;
            _queuedSolidAttachRetries = 0;

            if (_isClosed || settings is null)
            {
                return;
            }

            try
            {
                Apply(
                    target,
                    settings.Value,
                    deferSolidAttach: false,
                    solidAttachRetriesRemaining: solidAttachRetriesRemaining);
            }
            catch (Exception ex)
            {
                DetachBackdrop(target);
                Log.Error("Failed to apply queued system backdrop", ex);
            }
            finally
            {
                _backdropAttachmentChanged();
            }
        }

        private void DetachBackdrop(ICompositionSupportsSystemBackdrop target)
        {
            _appliedSettings = null;

            var solidColorCompositor = _solidColorCompositor;
            var solidColorBrush = _solidColorBrush;
            var micaController = _micaController;
            var acrylicController = _acrylicController;
            var backdropHasTarget = _backdropHasTarget;

            _solidColorCompositor = null;
            _solidColorBrush = null;
            _micaController = null;
            _acrylicController = null;
            _backdropHasTarget = false;

            if (solidColorBrush is not null)
            {
                RemoveTargetAndDispose(solidColorBrush, target, backdropHasTarget);
            }

            if (solidColorCompositor is not null)
            {
                Dispose(solidColorCompositor);
            }

            if (micaController is not null)
            {
                RemoveTargetAndDispose(micaController, target, backdropHasTarget);
            }

            if (acrylicController is not null)
            {
                RemoveTargetAndDispose(acrylicController, target, backdropHasTarget);
            }
        }

        private void AttachSolidColorBrush(ICompositionSupportsSystemBackdrop target, BackdropSettings settings)
        {
            var compositor = new WindowsCompositionCompositor();
            _solidColorCompositor = compositor;
            var brush = compositor.CreateColorBrush(settings.SolidColor);
            _solidColorBrush = brush;
            _backdropHasTarget = true;
            target.SystemBackdrop = brush;
        }

        private void AttachMicaController(ICompositionSupportsSystemBackdrop target, BackdropSettings settings)
        {
            if (!MicaController.IsSupported())
            {
                return;
            }

            var controller = new MicaController
            {
                Kind = settings.Kind == BackdropControllerKind.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base,
            };
            _micaController = controller;

            if (settings.ApplyTint)
            {
                controller.TintColor = settings.TintColor;
                controller.TintOpacity = settings.TintOpacity;
                controller.FallbackColor = settings.FallbackColor;
                controller.LuminosityOpacity = settings.LuminosityOpacity;
            }

            controller.SetSystemBackdropConfiguration(_configuration);
            _backdropHasTarget = true;
            controller.AddSystemBackdropTarget(target);
        }

        private void AttachAcrylicController(ICompositionSupportsSystemBackdrop target, BackdropSettings settings)
        {
            if (!DesktopAcrylicController.IsSupported())
            {
                return;
            }

            var controller = new DesktopAcrylicController
            {
                Kind = settings.Kind == BackdropControllerKind.AcrylicThin
                    ? DesktopAcrylicKind.Thin
                    : DesktopAcrylicKind.Default,
                TintColor = settings.TintColor,
                TintOpacity = settings.TintOpacity,
                FallbackColor = settings.FallbackColor,
                LuminosityOpacity = settings.LuminosityOpacity,
            };
            _acrylicController = controller;

            controller.SetSystemBackdropConfiguration(_configuration);
            _backdropHasTarget = true;
            controller.AddSystemBackdropTarget(target);
        }

        private static void RemoveTargetAndDispose(WindowsCompositionColorBrush brush, ICompositionSupportsSystemBackdrop target, bool backdropHasTarget)
        {
            try
            {
                if (backdropHasTarget)
                {
                    target.SystemBackdrop = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to remove solid system backdrop target", ex);
            }
            finally
            {
                try
                {
                    brush.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to dispose solid system backdrop brush", ex);
                }
            }
        }

        private static void Dispose(WindowsCompositionCompositor compositor)
        {
            try
            {
                compositor.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to dispose solid system backdrop compositor", ex);
            }
        }

        private static void RemoveTargetAndDispose(MicaController controller, ICompositionSupportsSystemBackdrop target, bool backdropHasTarget)
        {
            try
            {
                if (backdropHasTarget)
                {
                    controller.RemoveSystemBackdropTarget(target);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to remove Mica system backdrop target", ex);
            }
            finally
            {
                try
                {
                    controller.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to dispose Mica system backdrop controller", ex);
                }
            }
        }

        private static void RemoveTargetAndDispose(DesktopAcrylicController controller, ICompositionSupportsSystemBackdrop target, bool backdropHasTarget)
        {
            try
            {
                if (backdropHasTarget)
                {
                    controller.RemoveSystemBackdropTarget(target);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to remove acrylic system backdrop target", ex);
            }
            finally
            {
                try
                {
                    controller.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to dispose acrylic system backdrop controller", ex);
                }
            }
        }
    }
}
