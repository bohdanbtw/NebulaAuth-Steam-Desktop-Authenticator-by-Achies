using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NebulaAuth.Helpers;

namespace NebulaAuth.View;

public partial class InventoryWindow : Window
{
    private CoreWebView2Environment? _webViewEnvironment;
    private bool _extensionsLoaded;

    private readonly ObservableCollection<BrowserBookmarkItem> _bookmarks = [];

    private const string BrowserHomeUrl = "https://www.google.com/";
    private const string SteamLoginUrl = "https://steamcommunity.com/login/home/?goto=%2Fmy%2Finventory%2F";
    private const string RootFolderName = "Bookmarks Bar";

    private sealed class BrowserTabState(WebView2 webView, TextBlock titleBlock)
    {
        public WebView2 WebView { get; } = webView;
        public TextBlock TitleBlock { get; } = titleBlock;
        public SteamQRCodeAuthenticator? Authenticator { get; set; }
    }

    public InventoryWindow(ViewModel.InventoryVM viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Activate();
            Focus();

            if (DataContext is not ViewModel.InventoryVM vm)
            {
                return;
            }

            vm.StatusMessage = "Initializing browser...";
            vm.IsLoading = true;

            LoadBookmarks();
            AddressBar.KeyDown += AddressBar_KeyDown;

            _webViewEnvironment = await CreateEnvironmentAsync(vm);
            await CreateTabAsync(BrowserHomeUrl, false);
            await CreateTabAsync(SteamLoginUrl, true);
        }
        catch (Exception ex)
        {
            if (DataContext is ViewModel.InventoryVM vm)
            {
                vm.StatusMessage = $"Error: {ex.Message}";
                vm.IsLoading = false;
            }
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        foreach (var tab in BrowserTabs.Items.OfType<TabItem>())
        {
            if (tab.Tag is BrowserTabState state)
            {
                state.Authenticator?.Dispose();
            }
        }
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync(ViewModel.InventoryVM vm)
    {
        var accountId = vm.Mafile?.AccountName
                        ?? vm.Mafile?.SessionData?.SteamId.Steam64.ToString()
                        ?? "default";

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NebulaAuth", "WebView2Profiles", accountId);

        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = true
        };

        return await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }

    private async Task CreateTabAsync(string startInput, bool useQrAuthenticator)
    {
        if (_webViewEnvironment == null || DataContext is not ViewModel.InventoryVM vm)
        {
            return;
        }

        var webView = new WebView2 { Margin = new Thickness(0) };

        var titleBlock = new TextBlock
        {
            Text = "New Tab",
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var closeButton = new Button
        {
            Content = "×",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("MaterialDesignToolButton")
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(titleBlock);
        header.Children.Add(closeButton);

        var tabItem = new TabItem { Header = header, Content = null };
        var tabState = new BrowserTabState(webView, titleBlock);
        tabItem.Tag = tabState;

        closeButton.Click += (_, _) => CloseTab(tabItem);

        BrowserTabs.Items.Add(tabItem);
        BrowserTabs.SelectedItem = tabItem;
        BrowserContentHost.Content = webView;

        await webView.EnsureCoreWebView2Async(_webViewEnvironment);

        webView.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            var target = string.IsNullOrWhiteSpace(args.Uri) ? BrowserHomeUrl : args.Uri;
            _ = CreateTabAsync(target, false);
        };

        if (!_extensionsLoaded)
        {
            await ExtensionManager.EnsureAndLoadExtensionsAsync(webView.CoreWebView2, status => vm.StatusMessage = status);
            _extensionsLoaded = true;
        }

        webView.NavigationStarting += (_, _) =>
        {
            vm.StatusMessage = "Loading page...";
            vm.IsLoading = true;
        };

        webView.NavigationCompleted += (_, args) =>
        {
            var source = webView.Source?.ToString() ?? webView.CoreWebView2.Source;
            if (BrowserTabs.SelectedItem == tabItem)
            {
                AddressBar.Text = source;
                UpdateNavigationButtons();
            }

            titleBlock.Text = BuildTabTitle(source);
            vm.StatusMessage = args.IsSuccess ? "Ready" : $"Navigation failed ({args.WebErrorStatus})";
            vm.IsLoading = false;
        };

        if (useQrAuthenticator && vm.Mafile != null)
        {
            var authenticator = new SteamQRCodeAuthenticator();
            tabState.Authenticator = authenticator;
            authenticator.StatusChanged += (_, args) =>
            {
                vm.StatusMessage = args.Status;
                vm.IsLoading = args.IsLoading;
            };

            await authenticator.InitializeAsync(webView.CoreWebView2, vm.Mafile);
        }
        else
        {
            NavigateWebView(webView, startInput);
        }

        UpdateNavigationButtons();
    }

    private void BrowserTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GetCurrentTabState() is { WebView: { } webView })
        {
            BrowserContentHost.Content = webView;
            AddressBar.Text = webView.Source?.ToString() ?? webView.CoreWebView2?.Source ?? string.Empty;
        }
        else
        {
            BrowserContentHost.Content = null;
            AddressBar.Text = string.Empty;
        }

        UpdateNavigationButtons();
    }

    private async void NewTabButton_Click(object sender, RoutedEventArgs e) =>
        await CreateTabAsync(BrowserHomeUrl, false);

    private void CloseCurrentTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserTabs.SelectedItem is TabItem tab)
        {
            CloseTab(tab);
        }
    }

    private async void CloseTab(TabItem tab)
    {
        if (tab.Tag is BrowserTabState state)
        {
            state.Authenticator?.Dispose();
        }

        BrowserTabs.Items.Remove(tab);
        if (BrowserTabs.Items.Count == 0)
        {
            await CreateTabAsync(BrowserHomeUrl, false);
        }

        UpdateNavigationButtons();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var wv = GetCurrentTabState()?.WebView?.CoreWebView2;
        if (wv?.CanGoBack == true) wv.GoBack();
        UpdateNavigationButtons();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        var wv = GetCurrentTabState()?.WebView?.CoreWebView2;
        if (wv?.CanGoForward == true) wv.GoForward();
        UpdateNavigationButtons();
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => GetCurrentTabState()?.WebView?.CoreWebView2?.Reload();
    private void HomeButton_Click(object sender, RoutedEventArgs e) => NavigateCurrentTab(BrowserHomeUrl);
    private void OpenAddressButton_Click(object sender, RoutedEventArgs e) => NavigateCurrentTab(AddressBar.Text);

    private void AddressBar_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateCurrentTab(AddressBar.Text);
        }
    }

    private void SaveBookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        var currentUrl = GetCurrentTabState()?.WebView.Source?.ToString()
                         ?? GetCurrentTabState()?.WebView.CoreWebView2?.Source
                         ?? AddressBar.Text;

        var url = NormalizeInputToUrl(currentUrl);
        if (string.IsNullOrWhiteSpace(url)) return;

        var existing = FindBookmark(url);
        if (existing != null)
        {
            existing.Title = BuildBookmarkTitle(url);
            PersistBookmarks();
            RefreshBookmarkBar();
            BookmarksComboBox.Items.Refresh();
            return;
        }

        _bookmarks.Add(new BrowserBookmarkItem
        {
            Url = url,
            Title = BuildBookmarkTitle(url),
            Folder = RootFolderName
        });

        PersistBookmarks();
        RefreshBookmarkBar();
        BookmarksComboBox.Items.Refresh();
    }

    private void BookmarksComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BookmarksComboBox.SelectedItem is BrowserBookmarkItem bookmark)
        {
            NavigateCurrentTab(bookmark.Url);
        }
    }

    private void LoadBookmarks()
    {
        _bookmarks.Clear();

        var data = BrowserBookmarksStorage.LoadData();
        foreach (var bookmark in data.Bookmarks)
        {
            bookmark.Folder = RootFolderName;
            if (string.IsNullOrWhiteSpace(bookmark.Title))
            {
                bookmark.Title = BuildBookmarkTitle(bookmark.Url);
            }

            _bookmarks.Add(bookmark);
        }

        BookmarksComboBox.ItemsSource = _bookmarks;
        RefreshBookmarkBar();
    }

    private void PersistBookmarks()
    {
        BrowserBookmarksStorage.SaveData(new BrowserBookmarksData
        {
            Bookmarks = _bookmarks.ToList(),
            Folders = []
        });
    }

    private void RefreshBookmarkBar()
    {
        BookmarkBarPanel.Children.Clear();

        foreach (var bookmark in _bookmarks.OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase))
        {
            var button = new Button
            {
                Content = TrimBookmarkTitle(bookmark.Title),
                ToolTip = bookmark.Url,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(10, 4, 10, 4),
                MinHeight = 26,
                Style = (Style)FindResource("MaterialDesignFlatButton"),
                Tag = bookmark.Url
            };

            button.Click += BookmarkQuickButton_Click;
            button.ContextMenu = CreateBookmarkContextMenu(bookmark.Url);
            BookmarkBarPanel.Children.Add(button);
        }
    }

    private ContextMenu CreateBookmarkContextMenu(string bookmarkUrl)
    {
        var menu = new ContextMenu();

        var delete = new MenuItem { Header = "Delete bookmark" };
        delete.Click += (_, _) => DeleteBookmark(bookmarkUrl);

        menu.Items.Add(delete);
        return menu;
    }

    private void BookmarkQuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            NavigateCurrentTab(url);
        }
    }

    private void DeleteBookmark(string url)
    {
        var bookmark = FindBookmark(url);
        if (bookmark == null) return;

        _bookmarks.Remove(bookmark);
        PersistBookmarks();
        RefreshBookmarkBar();
        BookmarksComboBox.Items.Refresh();
    }

    private BrowserBookmarkItem? FindBookmark(string url)
        => _bookmarks.FirstOrDefault(b => b.Url.Equals(url, StringComparison.OrdinalIgnoreCase));

    private static string TrimBookmarkTitle(string value) => value.Length > 24 ? value[..24] + "…" : value;

    private static string NormalizeInputToUrl(string? input)
    {
        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate($"https://{value}", UriKind.Absolute, out var withHttps))
        {
            return withHttps.ToString();
        }

        return string.Empty;
    }

    private string BuildBookmarkTitle(string url)
    {
        var tabTitle = GetCurrentTabState()?.TitleBlock.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(tabTitle) && !tabTitle.Equals("New Tab", StringComparison.OrdinalIgnoreCase))
        {
            return tabTitle;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return url;
    }

    private static string BuildTabTitle(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return "New Tab";
    }

    private void NavigateCurrentTab(string input)
    {
        var state = GetCurrentTabState();
        if (state == null) return;

        NavigateWebView(state.WebView, input);
    }

    private static void NavigateWebView(WebView2 webView, string input)
    {
        var url = NormalizeInputToUrl(input);
        if (string.IsNullOrWhiteSpace(url)) return;

        webView.Source = new Uri(url);
    }

    private void UpdateNavigationButtons()
    {
        var webView = GetCurrentTabState()?.WebView?.CoreWebView2;
        BackButton.IsEnabled = webView?.CanGoBack == true;
        ForwardButton.IsEnabled = webView?.CanGoForward == true;
    }

    private BrowserTabState? GetCurrentTabState() =>
        BrowserTabs.SelectedItem is TabItem { Tag: BrowserTabState state } ? state : null;
}
