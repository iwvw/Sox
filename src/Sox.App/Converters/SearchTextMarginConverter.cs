using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Sox.App.Converters;

/// <summary>
/// Turns a TextBox font size into the small negative top margin the custom search template applies to
/// its content and placeholder. The baseline was tuned by eye at the spotlight's 20px font (a -3px top
/// nudge); hard-coding that value made the same template misalign any smaller user of it -- the docked
/// panel's 12px box put its text visibly high. Scaling the nudge with the font keeps the spotlight
/// unchanged and the panel centred.
/// </summary>
public sealed class SearchTextMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var fontSize = value is double size && size > 0 ? size : 14.0;
        // -3 at 20px (the value the spotlight was tuned with), proportional elsewhere.
        var top = -3.0 * (fontSize / 20.0);
        return new Thickness(0, top, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
