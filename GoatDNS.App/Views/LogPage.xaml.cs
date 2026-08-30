using System.Collections.Specialized;
using GoatDNS.App.ViewModels;
using GoatDNS.Core.Config;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace GoatDNS.App.Views;

/// <summary>
/// Live log view. Renders rows into a <see cref="RichTextBlock"/> (rather than a ListView) so the
/// text is selectable and copyable. The view-model's bounded <c>Rows</c> collection stays the source
/// of truth; we mirror its changes into <see cref="LogText"/>.Blocks and follow the tail.
/// </summary>
public sealed partial class LogPage : Page
{
    private static readonly SolidColorBrush ErrorBrush = new(Colors.IndianRed);

    public LogPage()
    {
        ViewModel = App.Vm.Log;
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public LogViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RebuildAll(); // catch up on anything logged before this page was shown
        ViewModel.Rows.CollectionChanged += OnRowsChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => ViewModel.Rows.CollectionChanged -= OnRowsChanged;

    // The VM only ever appends (Add at end), trims the oldest (RemoveAt 0), or resets (Clear/rebuild),
    // so mirroring those three cases keeps Blocks in lockstep with Rows without a full rebuild each push.
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                // Follow the tail only if we were already at the bottom — otherwise the user is
                // scrolled up reading/selecting history, and yanking to the end would fight them.
                bool follow = IsAtBottom();
                foreach (LogRow row in e.NewItems) LogText.Blocks.Add(Render(row));
                if (follow) ScrollToEnd();
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex == 0 && LogText.Blocks.Count > 0:
                LogText.Blocks.RemoveAt(0);
                break;
            default:
                RebuildAll();
                break;
        }
    }

    // Within a small slop of the bottom (also true at startup when extent/offset are both 0).
    private bool IsAtBottom() => LogScroll.ScrollableHeight - LogScroll.VerticalOffset <= 40;

    private void RebuildAll()
    {
        LogText.Blocks.Clear();
        foreach (var row in ViewModel.Rows) LogText.Blocks.Add(Render(row));
        ScrollToEnd();
    }

    // Defer the scroll so it runs after the newly-added block has been laid out.
    private void ScrollToEnd()
        => DispatcherQueue.TryEnqueue(() => LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true));

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = LogText.SelectedText;
        if (string.IsNullOrEmpty(text))
            text = string.Join(Environment.NewLine, ViewModel.Rows.Select(r => $"{r.Time}  {r.Level}  {r.Message}"));

        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private static Paragraph Render(LogRow row)
    {
        var brush = BrushFor(row.Level);
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = row.Time + "  ", Foreground = Muted });
        p.Inlines.Add(new Run { Text = row.Level.ToString().PadRight(8) + "  ", Foreground = brush });
        p.Inlines.Add(new Run { Text = row.Message, Foreground = brush });
        return p;
    }

    private static Brush Muted => Res("TextFillColorTertiaryBrush");

    private static Brush BrushFor(LogVerbosity level) => level switch
    {
        LogVerbosity.ErrorsOnly => ErrorBrush,
        LogVerbosity.Verbose => Res("TextFillColorSecondaryBrush"),
        LogVerbosity.Debug => Res("TextFillColorTertiaryBrush"),
        _ => Res("TextFillColorPrimaryBrush"),
    };

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
