using Quant.Core.Infrastructure;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Quant.UI.Views;

public interface IMainActions
{
    DbManager Db { get; }

    void ShowChart(string ticker, string name);
    void StatusInfo(string text);
    void StatusSuccess(string text);
    void StatusException(Exception ex, string text);
    void ActionHandler(MenuAction action, string? context);
}

public partial class MainWindow : Window, IMainActions
{
    private readonly DbManager _db = DbManager.Instance; // 전체에서 유일한 Instance 참조
    public DbManager Db => _db;

    private DbBrowserView?     _dbBrowserView;
    private ChartView?         _chartView;
    private ReportView?        _reportView;
    private EditGroupView?     _editGroupView;
    private EditWatchlistView? _editWatchlistView;
    private PlaceholderView?   _dashboardView;
    private SearchView?        _searchView;

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
        StatusInfo("stock_cache 확인 중...");
        try
        {
            await Task.Run(() => _db.EnsureStockCache());
            StatusSuccess("준비 완료");
        }
        catch (Exception ex)
        {
            StatusException(ex, "stock_cache 오류");
        }
    }

    private void SidePanel_StockSelected(string ticker, string name) => ShowChart(ticker, name);

    private void SidePanel_GroupSelected(int groupId, string name) { }

    private void SidePanel_IndicatorSelected(string symbol, string label)
    {
        ShowChart();
        if (MainContent.Content is ChartView cv)
            cv.LoadTicker(symbol, label);
        StatusInfo($"인디케이터: {label}({symbol})");
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
    private async void MenuRebuildStockCache_Click(object sender, RoutedEventArgs e)
    {
        StatusInfo("stock_cache 재구성 중...");
        try
        {
            await Task.Run(() => _db.RebuildStockCache());
            StatusSuccess("stock_cache 재구성 완료");
        }
        catch (Exception ex)
        {
            StatusException(ex, "stock_cache 재구성 실패");
        }
    }

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

    private void ShowChart() => ShowChart(null, null);
    private void ShowChart(string? ticker, string? name = null)
    {
        if (_chartView is null)
        {
            _chartView = new ChartView(_db);
            _chartView.StatusChanged += Status;
        }
        SwitchView(_chartView, BtnChart, "Chart");
        if (!string.IsNullOrEmpty(ticker))
            _chartView.LoadTicker(ticker, name ?? ticker);
    }

    private void ShowSearch()
    {
        if (_searchView is null)
        {
            _searchView = new SearchView(_db);
            _searchView.StatusChanged  += Status;
            _searchView.StockSelected += (ticker, name) => ShowChart(ticker, name);
        }
        SwitchView(_searchView, BtnSearch, "Search");
    }

    private void ShowDbBrowser()
    {
        if (_dbBrowserView is null)
        {
            _dbBrowserView = new DbBrowserView(_db);
            _dbBrowserView.StatusChanged  += Status;
            _dbBrowserView.ElapsedChanged += ms => TxtElapsed.Text = ms;
        }
        SwitchView(_dbBrowserView, BtnDbBrowser, "DB Browser");
    }

    private void ShowReport()
    {
        if (_reportView is null)
        {
            _reportView = new ReportView(this);
            //_reportView.StatusChanged      += Status;
            //_reportView.TickerDoubleClicked += (ticker, name) => ShowChart(ticker, name);
        }
        SwitchView(_reportView, BtnReport, "Report");
    }

    private void ShowEditGroup()
    {
        if (_editGroupView is null)
        {
            _editGroupView = new EditGroupView(_db);
            _editGroupView.StatusChanged      += Status;
            _editGroupView.TickerDoubleClicked += (ticker, name) => ShowChart(ticker, name);
        }
        SwitchView(_editGroupView, BtnGroup, "Edit Group");
    }

    private void ShowEditWatchlist()
    {
        if (_editWatchlistView is null)
        {
            _editWatchlistView = new EditWatchlistView(_db);
            _editWatchlistView.StatusChanged += Status;
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

    public void Status(string message, string colorHex)
    {
        TxtStatusMsg.Text = message;
        TxtStatusMsg.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
    }
    public void StatusSuccess(string message) => Status(message, StatusColors.Success);
    public void StatusInfo(string message) => Status(message, StatusColors.Info);
    public void StatusError(string message) => Status(message, StatusColors.Error);
    public void StatusWarning(string message) => Status(message, StatusColors.Warning);
    public void StatusException(Exception ex, string message) => StatusError($"{message}: {ex.Message}");


    //private void SetStatus(string msg, string hex)
    //{ 
    //    TxtStatusMsg.Text       = msg;
    //    TxtStatusMsg.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    //}

    private static string ShortenPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.StartsWith(local, StringComparison.OrdinalIgnoreCase)
            ? "%LOCALAPPDATA%" + path[local.Length..]
            : path;
    }

    void IMainActions.ShowChart(string ticker, string name)
    {
        ShowChart(ticker, name);
    }

    public void SetStatus(string text)
    {
        throw new NotImplementedException();
    }



    // ══════════════════════════════════════════════════════════
    //  Menu actions
    // ══════════════════════════════════════════════════════════
    public void ActionHandler(MenuAction action, string? context)
    {
        if (string.IsNullOrEmpty(context)) return;

        switch (action)
        {
            // 1. 화면 전환 및 네비게이션 액션
            case MenuAction.DrawChart:
                // MainWindow가 구독 중인 이벤트를 호출하여 차트 뷰로 이동합니다.
                //TickerDoubleClicked?.Invoke(context, context);
                ShowChart(context, context);
                return;

            case MenuAction.FindGroup:
                StatusWarning($"Find Group: {context}");
                return;

            case MenuAction.WebInfo:
                Helpers.OpenExternalLink(context);
                return;

            // 2. DB 관리 액션 (공통 로직으로 분리)
            case MenuAction.Db_DeletePrices:
            case MenuAction.Db_RemoveTicker:
                HandleDbMenuAction(action, context);
                return;
        }
        StatusInfo("no action defined");
    }

    private void HandleDbMenuAction(MenuAction action, string ticker)
    {
        return; // for safe now - remove after testing

        if (MessageBox.Show($"[{ticker}]의 데이터를 삭제하시겠습니까?", "삭제 확인",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            if (action == MenuAction.Db_RemoveTicker) _db.DeleteStockAllData(ticker);
            else if (action == MenuAction.Db_DeletePrices) _db.DeletePriceData(ticker);

            StatusSuccess($"{action} 완료: {ticker}");
            //LoadGrids(); // 변경 사항 반영을 위해 그리드 새로고침
        }
        catch (Exception ex)
        {
            StatusException(ex, "DB 작업 실패");
        }
    }


}
