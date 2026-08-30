using CommunityToolkit.Mvvm.ComponentModel;

namespace GoatDNS.App.ViewModels;

/// <summary>A named, checkable row used by the multi-select editors (pool membership, rule hosts-files).</summary>
public partial class SelectableItem : ObservableObject
{
    public SelectableItem(string name, bool isSelected = false)
    {
        Name = name;
        _isSelected = isSelected;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Shared conversions between the config model's <c>List&lt;string&gt;</c> fields and the
/// newline-delimited text boxes the editors use. Kept in one place so every editor treats
/// blanks and whitespace identically.
/// </summary>
internal static class TextLists
{
    /// <summary>Null out empty/whitespace so we never persist "" where the model means "unset".</summary>
    public static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>One entry per non-empty line, trimmed. Accepts either CRLF or LF input.</summary>
    public static List<string> Split(string? text) =>
        (text ?? "")
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    public static string Join(IEnumerable<string> items) => string.Join(Environment.NewLine, items);
}
