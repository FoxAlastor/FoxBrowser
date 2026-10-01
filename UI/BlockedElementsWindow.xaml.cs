using System.Windows;
using FoxBrowser.Core;

namespace FoxBrowser.UI;

public partial class BlockedElementsWindow : Window
{
    private readonly TabManager _tabManager;

    public BlockedElementsWindow(TabManager tabManager)
    {
        InitializeComponent();
        _tabManager = tabManager;
        RefreshList();
    }

    private void RefreshList()
    {
        SelectorList.ItemsSource = _tabManager.GetBlockedSelectors();
    }

    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectorList.SelectedItem is not string selector) return;
        await _tabManager.RemoveBlockedSelectorAsync(selector);
        RefreshList();
    }

    private async void ClearSite_Click(object sender, RoutedEventArgs e)
    {
        await _tabManager.ClearBlockedForCurrentHostAsync();
        RefreshList();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
