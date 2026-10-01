namespace Sox.PluginSdk.Abstractions.Plugins;

public interface IThemeProvider : IPluginComponent
{

    IEnumerable<ITheme> GetThemes();
}
