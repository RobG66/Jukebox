using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Jukebox.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Jukebox.Views;

/// <summary>
/// Host-owned media navigation. Queue and saved playlists remain available in
/// the compact panel while media browser plugins use the surface to its right.
/// </summary>
public partial class PlaylistView : UserControl
{
    private const string FallbackPluginIconPath =
        "M12,3V13.55A4,4 0 1 0 14,17V7H18V3H12Z";

    private readonly List<ToggleButton> _pluginButtons = new();
    private JukeboxViewModel? _viewModel;

    public PlaylistView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as JukeboxViewModel);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        AttachViewModel(null);
    }

    private void AttachViewModel(JukeboxViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel != null)
        {
            _viewModel.PlaylistViewModel.PropertyChanged -= OnPlaylistPropertyChanged;
            _viewModel.PlaylistViewModel.MediaBrowserTabs.CollectionChanged -= OnBrowsersChanged;
        }

        _viewModel = viewModel;

        if (_viewModel != null)
        {
            _viewModel.PlaylistViewModel.PropertyChanged += OnPlaylistPropertyChanged;
            _viewModel.PlaylistViewModel.MediaBrowserTabs.CollectionChanged += OnBrowsersChanged;
        }

        RebuildPluginButtons();
        UpdateSelectedDestination();
    }

    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JukeboxPlaylistViewModel.ActiveTabIndex) or
            nameof(JukeboxPlaylistViewModel.LastHostTabIndex))
        {
            UpdateSelectedDestination();
        }
    }

    private void OnBrowsersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildPluginButtons();
        UpdateSelectedDestination();
    }

    private void QueueNavButton_Click(object? sender, RoutedEventArgs e)
    {
        SelectHostDestination(0);
    }

    private void PlaylistsNavButton_Click(object? sender, RoutedEventArgs e)
    {
        SelectHostDestination(1);
    }

    private void SelectHostDestination(int index)
    {
        if (_viewModel == null)
        {
            return;
        }

        if (_viewModel.PlaylistViewModel.ActiveTab == PlaylistTabType.Plugins)
        {
            // Browser selection and host-panel selection are independent. This
            // lets the user switch between queue and saved playlists without
            // closing the browser alongside them.
            _viewModel.PlaylistViewModel.LastHostTabIndex = index;
            UpdateSelectedDestination();
            return;
        }

        SelectDestination(index);
    }

    private void OnPluginNavButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not ToggleButton { Tag: PluginTab pluginTab })
        {
            return;
        }

        int pluginIndex = _viewModel.PlaylistViewModel.MediaBrowserTabs.IndexOf(pluginTab);
        if (pluginIndex >= 0)
        {
            SelectDestination(pluginIndex + 2);
        }
    }

    private void SelectDestination(int index)
    {
        if (_viewModel == null)
        {
            return;
        }

        _viewModel.PlaylistViewModel.ActiveTabIndex = index;
        UpdateSelectedDestination();
    }

    private void RebuildPluginButtons()
    {
        PluginNavItems.Children.Clear();
        _pluginButtons.Clear();

        if (_viewModel == null)
        {
            return;
        }

        foreach (var pluginTab in _viewModel.PlaylistViewModel.MediaBrowserTabs)
        {
            var button = new ToggleButton
            {
                Tag = pluginTab,
                Content = BuildPluginIcon(pluginTab),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            button.Classes.Add("media-nav");
            button.SetValue(ToolTip.TipProperty, pluginTab.Browser.DisplayName);
            button.Click += OnPluginNavButtonClick;

            _pluginButtons.Add(button);
            PluginNavItems.Children.Add(button);
        }
    }

    private void UpdateSelectedDestination()
    {
        if (_viewModel == null)
        {
            HostPanel.IsVisible = true;
            BrowserPanel.IsVisible = false;
            BrowserContentPresenter.Content = null;
            return;
        }

        int index = _viewModel.PlaylistViewModel.ActiveTabIndex;
        int pluginIndex = index - 2;
        bool isPlugin = pluginIndex >= 0 &&
                        pluginIndex < _viewModel.PlaylistViewModel.MediaBrowserTabs.Count;
        int hostIndex = index is 0 or 1
            ? index
            : _viewModel.PlaylistViewModel.LastHostTabIndex;

        QueueNavButton.IsChecked = hostIndex == 0;
        PlaylistsNavButton.IsChecked = hostIndex == 1;
        for (int i = 0; i < _pluginButtons.Count; i++)
        {
            _pluginButtons[i].IsChecked = isPlugin && i == pluginIndex;
        }

        HostPanel.IsVisible = true;
        BrowserPanel.IsVisible = isPlugin;
        QueueView.IsVisible = hostIndex == 0;
        PlaylistsView.IsVisible = hostIndex == 1;

        BrowserContentPresenter.Content = isPlugin
            ? _viewModel.PlaylistViewModel.MediaBrowserTabs[pluginIndex].View
            : null;
    }

    private static Control BuildPluginIcon(PluginTab pluginTab)
    {
        var bitmap = TryLoadBitmap(pluginTab.Browser.IconUri);
        if (bitmap != null)
        {
            return new Image
            {
                Width = 27,
                Height = 27,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Source = bitmap
            };
        }

        string pathData = string.IsNullOrWhiteSpace(pluginTab.Browser.IconPathData)
            ? FallbackPluginIconPath
            : pluginTab.Browser.IconPathData;

        var icon = new PathIcon
        {
            Data = StreamGeometry.Parse(pathData),
            Width = 23,
            Height = 23,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Classes.Add("media-vector");
        return icon;
    }

    private static Bitmap? TryLoadBitmap(string? uriText)
    {
        if (string.IsNullOrWhiteSpace(uriText))
        {
            return null;
        }

        try
        {
            if (uriText.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = AssetLoader.Open(new Uri(uriText));
                return new Bitmap(stream);
            }

            return new Bitmap(uriText);
        }
        catch
        {
            return null;
        }
    }
}
