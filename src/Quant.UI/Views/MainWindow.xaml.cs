using Quant.Core.Infrastructure;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Quant.UI.Views;

public partial class MainWindow : Window
{
    private readonly DbManager _db = DbManager.Instance; // 전체에서 유일한 Instance 참조

    private DbBrowserView?     _dbBrowserView;
    private ChartView?         _chartView;
    private ReportView?        _reportView;
    private EditGroupView?     _editGroupView;
    private EditWatchlistView? _editWatchlistView;
    private PlaceholderView?   _dashboardView;
    private PlaceholderView?   _searchView;

    private Button[] _toolButtons = [];
    private bool     _sidePanelVisible = true;
    private Button[] _allToolButtons  = [];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        TxtDbPath.Text  = ShortenPath(DbManager.DbPath);
        _toolButtons    = [BtnDashboard, BtnChart, BtnSearch, BtnDbBrowser];
        _allToolButtons = [BtnDashboard, BtnChart, BtnSearch, BtnDbBrowser, BtnGroup, BtnReport];
        ShowDashboard();

        // stock_cache 갱신을 백그라운드에서 실행 — UI 블로킹 방지
        SetStatus("stock_cache 확인 중...", StatusColors.Muted);
        try
        {
            await Task.Run(() => _db.EnsureStockCache());
            SetStatus("준비 완료", StatusColors.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"stock_cache 오류: {ex.Message}", StatusColors.Error);
        }
    }

    private void SidePanel_StockSelected(string ticker, string name)
    {
		if (MainContent.Content is ChartView cv) cv.LoadTicker(ticker, name);
        else if (MainContent.Content is EditGroupView gv) gv.AddTicker(ticker, name);
        SetStatus($"선택: {name}({ticker})", "#CDD6F4");
    }

    private void SidePanel_GroupSelected(int groupId, string name) { }

    private void SidePanel_IndicatorSelected(string symbol, string label)
    {
        ShowChart();
        if (MainContent.Content is ChartView cv)
            cv.LoadTicker(symbol, label);
        SetStatus($"인디케이터: {label}({symbol})", "#CDD6F4");
    }

    private void MenuSidePanel_Click(object sender, RoutedEventArgs e)
    {
        _sidePanelVisible  = !_sidePanelVisible;
        SideColumn.Width   = _sidePanelVisible ? new GridLength(222) : new GridLength(0);
        SidePanel.Visibility = _sidePanelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)          => Close();
    private void MenuOptions_Click(object sender, RoutedEventArgs e)       => new OptionDialog { Owner = this }.ShowDialog();
    private void MenuReport_Click(object sender, RoutedEventArgs e)        => ShowReport();
    private void MenuEditGroup_Click(object sender, RoutedEventArgs e)     => ShowEditGroup();
    private void MenuEditWatchlist_Click(object sender, RoutedEventArgs e) => ShowEditWatchlist();
    private void MenuDbBrowser_Click(object sender, RoutedEventArgs e)     => ShowDbBrowser();

    private void BtnDashboard_Click(object sender, RoutedEventArgs e) => ShowDashboard();
    private void BtnChart_Click(object sender, RoutedEventArgs e)     => ShowChart();
    private void BtnSearch_Click(object sender, RoutedEventArgs e)    => ShowSearch();
    private void BtnDbBrowser_Click(object sender, RoutedEventArgs e) => ShowDbBrowser();
    private void BtnGroup_Click(object sender, RoutedEventArgs e)    => ShowEditGroup();
    private void BtnReport_Click(object sender, RoutedEventArgs e)   => ShowReport();

    private void ShowDashboard()
    {
        _dashboardView ??= new PlaceholderView("⊞", "Dashboard");
        SwitchView(_dashboardView, BtnDashboard, "Dashboard");
    }

    private void ShowChart()
    {
        if (_chartView is null)
        {
            _chartView = new ChartView(_db);
            _chartView.StatusChanged += SetStatus;
        }
        SwitchView(_chartView, BtnChart, "Chart");
    }

    private void ShowSearch()
    {
        _searchView ??= new PlaceholderView("🔍", "Search");
        SwitchView(_searchView, BtnSearch, "Search");
    }

    private void ShowDbBrowser()
    {
        if (_dbBrowserView is null)
        {
            _dbBrowserView = new DbBrowserView(_db);
            _dbBrowserView.StatusChanged  += SetStatus;
            _dbBrowserView.ElapsedChanged += ms => TxtElapsed.Text = ms;
        }
        SwitchView(_dbBrowserView, BtnDbBrowser, "DB Browser");
    }

    private void ShowReport()
    {
        if (_reportView is null)
        {
            _reportView = new ReportView(_db);
            _reportView.StatusChanged += SetStatus;
        }
        SwitchView(_reportView, BtnReport, "Report");
    }

    private void ShowEditGroup()
    {
        if (_editGroupView is null)
        {
            _editGroupView = new EditGroupView(_db);
            _editGroupView.StatusChanged      += SetStatus;
            _editGroupView.TickerDoubleClicked += (ticker, name) =>
            {
                ShowChart();
                (_chartView as ChartView)?.LoadTicker(ticker, name);
            };
        }
        SwitchView(_editGroupView, BtnGroup, "Edit Group");
    }

    private void ShowEditWatchlist()
    {
        if (_editWatchlistView is null)
        {
            _editWatchlistView = new EditWatchlistView(_db);
            _editWatchlistView.StatusChanged += SetStatus;
        }
        SwitchView(_editWatchlistView, null, "Edit Watchlist");
    }

    private void SwitchView(object view, Button? activeBtn, string label)
    {
        MainContent.Content = view;
        TxtStatus.Text      = label;
        TxtElapsed.Text     = "";
        HighlightToolButton(activeBtn);
    }

    private void HighlightToolButton(Button? active)
    {
        foreach (var btn in _allToolButtons)
        {
            var isActive  = ReferenceEquals(btn, active);
            var fg        = isActive ? "#89B4FA" : "#6C7086";
            var brush     = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
            var underline = isActive ? brush : Brushes.Transparent;
            btn.BorderBrush = underline;
            if (btn.Content is StackPanel sp)
                foreach (var child in sp.Children.OfType<TextBlock>())
                    child.Foreground = brush;
        }
    }

    private void SetStatus(string msg, string hex)
    {
        TxtMsg.Text       = msg;
        TxtMsg.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private static string ShortenPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.StartsWith(local, StringComparison.OrdinalIgnoreCase)
            ? "%LOCALAPPDATA%" + path[local.Length..]
            : path;
    }
}
