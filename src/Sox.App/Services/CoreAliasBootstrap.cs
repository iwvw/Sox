using System.Globalization;
using Sox.Core;
using Sox.Core.SearchIndex;
using Sox.Plugins.PinyinAlias;
using Sox.PluginSdk.Services;

namespace Sox.App.Services;

/// <summary>
/// Registers the core alias (pinyin) provider inside the App process. The file search runs in
/// SoxService, which loads the provider itself; the App-side instant sources (applications, windows,
/// history recall) call <see cref="FuzzyMatcher"/> in-process, so without this a Chinese app name like
/// 记事本 could only be reached by its literal spelling, never by "jsb" (ADR-0003, ADR-0015).
/// </summary>
internal static class CoreAliasBootstrap
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            var provider = new PinyinAliasProvider();
            WireSettings();
            WireTranslations(provider);
            AliasProviderRegistry.Register(provider);
            Log.Info("Registered PinyinAlias provider for App-side instant search");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to register PinyinAlias provider", ex);
        }
    }

    private static void WireSettings()
    {
        PluginSettingsService.GetSettingFunc ??= (pluginId, key, defVal) =>
        {
            try
            {
                return UserSettingsPluginSupport.GetPluginSetting(UserSettings.Load(), pluginId, key, defVal);
            }
            catch
            {
                return defVal;
            }
        };
    }

    private static void WireTranslations(PinyinAliasProvider provider)
    {
        if (TranslationService.LookupFunc is not null && TranslationService.LookupFunc("__probe__") != "[__probe__]")
        {
            return;
        }

        var culture = CultureInfo.CurrentUICulture.Name;
        var translations = provider.GetTranslations(culture);
        TranslationService.LookupFunc = key => translations.TryGetValue(key, out var value) ? value : key;
    }
}
