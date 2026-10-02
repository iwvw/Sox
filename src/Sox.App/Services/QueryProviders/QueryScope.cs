namespace Sox.App.Services.QueryProviders;

/// <summary>
/// A mode entered by typing a keyword followed by a space ("g "), which isolates the result list to one
/// provider and shows a badge for it in the search box -- Listary's scope behaviour. The keyword is the
/// text that triggers the scope; <see cref="Name"/>, <see cref="Glyph"/> and <see cref="IconPath"/> are
/// the badge's label and icon (a custom icon wins over the glyph when present).
/// </summary>
internal sealed record QueryScope(string Keyword, string Name, string Glyph, string? IconPath = null);
