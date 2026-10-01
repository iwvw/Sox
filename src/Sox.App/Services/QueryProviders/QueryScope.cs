namespace Sox.App.Services.QueryProviders;

/// <summary>
/// A mode entered by typing a keyword followed by a space ("g "), which isolates the result list to one
/// provider and shows a badge for it in the search box -- Listary's scope behaviour. The keyword is the
/// text that triggers the scope; <see cref="Name"/> and <see cref="Glyph"/> are the badge's label/icon.
/// </summary>
internal sealed record QueryScope(string Keyword, string Name, string Glyph);
