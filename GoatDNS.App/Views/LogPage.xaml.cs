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

    // Sticky tail-following. Only user scrolling flips it (see LogScroll_ViewChanged); sampling the
    // offset at append time is unreliable because a burst of rows lands before layout catches up.
    private bool _follow = true;
    private bool _scrollQueued;

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
                // Follow the tail unless the user scrolled up to read/select history.
                foreach (LogRow row in e.NewItems) LogText.Blocks.Add(Render(row));
                if (_follow) ScrollToEnd();
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex == 0 && LogText.Blocks.Count > 0:
                LogText.Blocks.RemoveAt(0);
                break;
            default:
                RebuildAll();
                break;
        }
    }

    // Only user-driven scrolls report intermediate views; our own ChangeView calls never do, so this
    // reads the user's intent without fighting the programmatic jumps.
    private void LogScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
            _follow = LogScroll.ScrollableHeight - LogScroll.VerticalOffset <= 40;
    }

    private void RebuildAll()
    {
        LogText.Blocks.Clear();
        foreach (var row in ViewModel.Rows) LogText.Blocks.Add(Render(row));
        _follow = true;
        ScrollToEnd();
    }

    // Coalesce to one scroll per frame and force the pending layout first: ScrollableHeight still
    // reports the old extent until the newly-added blocks are measured, so scrolling to it lands
    // short and, once short by more than the slop, the view never catches up again.
    private void ScrollToEnd()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollQueued = false;
            LogScroll.UpdateLayout();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        });
    }

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
