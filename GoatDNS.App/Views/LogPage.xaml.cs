using System.Collections.Specialized;
using GoatDNS.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GoatDNS.App.Views;

/// <summary>Live log view. Follows the tail as new rows arrive (unless the user has paused/scrolled).</summary>
public sealed partial class LogPage : Page
{
    public LogPage()
    {
        ViewModel = App.Vm.Log;
        InitializeComponent();

        // Follow the tail only while subscribed to this page; detach on navigate-away.
        Loaded += (_, _) => ViewModel.Rows.CollectionChanged += OnRowsChanged;
        Unloaded += (_, _) => ViewModel.Rows.CollectionChanged -= OnRowsChanged;
    }

    public LogViewModel ViewModel { get; }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && !ViewModel.IsPaused && ViewModel.Rows.Count > 0)
            LogList.ScrollIntoView(ViewModel.Rows[^1]);
    }
}
