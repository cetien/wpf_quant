using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Quant.UI.Views;

public partial class MainWindow : Window
{
    private static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "quant", "quant.duckdb");

    // 뷰 캐시
    private DbBrowserView?       _dbBrowserView;
    private ChartView?           _chartView;
    private ReportView?          _reportView;
    private EditGroupView?       _editGroupView;
    private EditWatchlistView?   _editWatchlistView;
    private PlaceholderView?     _dashboardView;
    private PlaceholderView?     _searchView;

    // 툴버튼 목록 (활성 표시용)
    private Button[] _toolButtons = [];

    // 사이드패널 표시 여부
    private bool _sidePanelVisible = true;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TxtDbPath.Text = ShortenPath(DbPath);
        _toolButtons   = [BtnDashboard, BtnChart, BtnSearch, BtnDbBrowser];
        ShowDashboard();
    }

    // ── LeftSidePanel 이벤트 핸들러 ──────────────────────────
    private void SidePanel_StockSelected(string ticker)
    {
        // ChartView가 현재 활성 뷰이면 바로 로드
        if (MainContent.Content is ChartView cv)
        {
            cv.LoadTicker(ticker);
        }
        // EditWatchlistView가 활성이면 선택 ticker를 상태바에 표시
        SetStatus($"선택: {ticker}", "#CDD6F4");
    }

    private void SidePanel_GroupSelected(int groupId)
    {
        // 향후 그룹 분석 뷰 등에서 활용
        // 현재는 상태바 업데이트만
    }

    // ── 메뉴: Side Panel 토글 ───────────────────────────────
    private void MenuSidePanel_Click(object sender, RoutedEventArgs e)
    {
        _sidePanelVisible = !_sidePanelVisible;
        SideColumn.Width  = _sidePanelVisible
            ? new GridLength(222)
            : new GridLength(0);
        SidePanel.Visibility = _sidePanelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 메뉴 핸들러 ──────────────────────────────────────────
    private void MenuExit_Click(object sender, RoutedEventArgs e)         => Close();
    private void MenuReport_Click(object sender, RoutedEventArgs e)       => ShowReport();
    private void MenuEditGroup_Click(object sender, RoutedEventArgs e)    => ShowEditGroup();
    private void MenuEditWatchlist_Click(object sender, RoutedEventArgs e) => ShowEditWatchlist();
    private void MenuDbBrowser_Click(object sender, RoutedEventArgs e)    => ShowDbBrowser();

    // ── 툴바 핸들러 ──────────────────────────────────────────
    private void BtnDashboard_Click(object sender, RoutedEventArgs e) => ShowDashboard();
    private void BtnChart_Click(object sender, RoutedEventArgs e)     => ShowChart();
    private void BtnSearch_Click(object sender, RoutedEventArgs e)    => ShowSearch();
    private void BtnDbBrowser_Click(object sender, RoutedEventArgs e) => ShowDbBrowser();

    // ── 뷰 전환 ──────────────────────────────────────────────
    private void ShowDashboard()
    {
        _dashboardView ??= new PlaceholderView("⊞", "Dashboard");
        SwitchView(_dashboardView, BtnDashboard, "Dashboard");
    }

    private void ShowChart()
    {
        if (_chartView is null)
        {
            _chartView = new ChartView();
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
            _dbBrowserView = new DbBrowserView();
            _dbBrowserView.StatusChanged  += SetStatus;
            _dbBrowserView.ElapsedChanged += ms => TxtElapsed.Text = ms;
        }
        SwitchView(_dbBrowserView, BtnDbBrowser, "DB Browser");
    }

    private void ShowReport()
    {
        if (_reportView is null)
        {
            _reportView = new ReportView();
            _reportView.StatusChanged += SetStatus;
        }
        SwitchView(_reportView, null, "Report");
    }

    private void ShowEditGroup()
    {
        if (_editGroupView is null)
        {
            _editGroupView = new EditGroupView();
            _editGroupView.StatusChanged += SetStatus;
        }
        SwitchView(_editGroupView, null, "Edit Group");
    }

    private void ShowEditWatchlist()
    {
        if (_editWatchlistView is null)
        {
            _editWatchlistView = new EditWatchlistView();
            _editWatchlistView.StatusChanged += SetStatus;
        }
        SwitchView(_editWatchlistView, null, "Edit Watchlist");
    }

    // ── 공통 뷰 스위치 ────────────────────────────────────────
    private void SwitchView(object view, Button? activeBtn, string label)
    {
        MainContent.Content = view;
        TxtStatus.Text      = label;
        TxtElapsed.Text     = "";
        HighlightToolButton(activeBtn);
    }

    private void HighlightToolButton(Button? active)
    {
        foreach (var btn in _toolButtons)
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
