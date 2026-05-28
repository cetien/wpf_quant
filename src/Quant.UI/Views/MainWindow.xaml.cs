using HandyControl.Data;

using Quant.Core.Infrastructure;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Quant.UI.Views;

public interface INavigationAware
{
    void OnNavigatedTo(string id);
}

public interface IMainActions
{
    DbManager Db { get; }

    // switch view:
    void ShowChart(string ticker);
    void ShowDbBrowser(string ticker);

    // status update:
    void StatusInfo(string text);
    void StatusSuccess(string text);
    void StatusWarning(string text);
    void StatusError(string text);
    void StatusException(Exception ex, string text);

    // 메뉴 액션 핸들러: 
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
    //private EditWatchlistView? _editWatchlistView;
    private PlaceholderView?   _dashboardView;
    private SearchView?        _searchView;
    private TargetPriceView?   _targetPriceView;

    //private Button[] _toolButtons = [];
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
        //_toolButtons    = [BtnDashboard, BtnChart, BtnSearch, BtnDbBrowser];
        _allToolButtons = [BtnDashboard, BtnChart, BtnGroup, BtnSearch, BtnDbBrowser, BtnReport, BtnTargetPrice];

        App.Config().LastTicker = "IDX_KOSPI";
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

    private void SidePanel_StockSelected(string ticker) //=> ShowChart(ticker);
    {
        App.Config().LastTicker = ticker;

        TxtStatus.Text = $"종목: {ticker}";// label;
        if (MainContent.Content is INavigationAware navAwareView) { navAwareView.OnNavigatedTo(ticker); }

    }

    private void SidePanel_GroupSelected(int groupId) { }

    private void SidePanel_IndicatorSelected(string symbol)
    {
        ShowChart(symbol);
        if (MainContent.Content is ChartView cv)
            cv.LoadTicker(symbol);
        StatusInfo($"인디케이터: {symbol}");
    }

    private void MenuSidePanel_Click(object sender, RoutedEventArgs e)
    {
        _sidePanelVisible  = !_sidePanelVisible;
        SideColumn.Width   = _sidePanelVisible ? new GridLength(222) : new GridLength(0);
        SidePanel.Visibility = _sidePanelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)          => Close();
    private void MenuOptions_Click(object sender, RoutedEventArgs e)       => new OptionDialog { Owner = this }.ShowDialog();
    private void MenuReport_Click(object sender, RoutedEventArgs e)        => ShowReport(App.Config().LastTicker);
    private void MenuEditGroup_Click(object sender, RoutedEventArgs e)     => ShowEditGroup();
    //private void MenuEditWatchlist_Click(object sender, RoutedEventArgs e) => ShowEditWatchlist();
    private void MenuDbBrowser_Click(object sender, RoutedEventArgs e)     => ShowDbBrowser(App.Config().LastTicker);
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

    private void BtnDashboard_Click(object sender, RoutedEventArgs e)    => ShowDashboard();
    private void BtnChart_Click(object sender, RoutedEventArgs e)        => ShowChart("");
    private void BtnSearch_Click(object sender, RoutedEventArgs e)       => ShowSearch();
    private void BtnDbBrowser_Click(object sender, RoutedEventArgs e)    => ShowDbBrowser(App.Config().LastTicker);
    private void BtnGroup_Click(object sender, RoutedEventArgs e)        => ShowEditGroup();
    private void BtnReport_Click(object sender, RoutedEventArgs e)       => ShowReport(App.Config().LastTicker);
    private void BtnTargetPrice_Click(object sender, RoutedEventArgs e)  => ShowTargetPrice();

    private void ShowDashboard()
    {
        _dashboardView ??= new PlaceholderView("⊞", "Dashboard");
        SwitchView(_dashboardView, "Dashboard", BtnDashboard);
    }

    //private void ShowChart() => ShowChart(null, null);
    public void ShowChart(string? ticker)
    {
        if (_chartView is null)
        {
            _chartView = new ChartView(_db);
            _chartView.StatusChanged += Status;
        }
        SwitchView(_chartView, "Chart", BtnChart);
        if (!string.IsNullOrEmpty(ticker))
            _chartView.LoadTicker(ticker);
    }

    private void ShowSearch()
    {
        if (_searchView is null)
        {
            _searchView = new SearchView(_db);
            _searchView.StatusChanged  += Status;
            //_searchView.StockSelected += (ticker, name) => ShowChart(ticker, name);
        }
        SwitchView(_searchView, "Search", BtnSearch);
    }

    public void ShowDbBrowser(string? ticker)
    {
        if (_dbBrowserView is null)
        {
            _dbBrowserView = new DbBrowserView(_db);
            //_dbBrowserView.StatusChanged  += Status;
            _dbBrowserView.ElapsedChanged += ms => TxtElapsed.Text = ms;
        }
        SwitchView(_dbBrowserView, ticker, BtnDbBrowser);
    }

    private void ShowReport(string? ticker)
    {
        if (_reportView is null)
        {
            _reportView = new ReportView(this);
            //_reportView.StatusChanged      += Status;
            //_reportView.TickerDoubleClicked += (ticker, name) => ShowChart(ticker, name);
        }
        SwitchView(_reportView, ticker, BtnReport);
    }

    private void ShowEditGroup()
    {
        if (_editGroupView is null)
        {
            _editGroupView = new EditGroupView(_db);
            _editGroupView.StatusChanged      += Status;
            //_editGroupView.TickerDoubleClicked += (ticker, name) => ShowChart(ticker, name);
        }
        SwitchView(_editGroupView, "EditGroup", BtnGroup);
    }

    public void ShowTargetPrice(string? ticker = null)
    {
        if (_targetPriceView is null)
            _targetPriceView = new TargetPriceView(_db);

        SwitchView(_targetPriceView, "TargetPrice", BtnTargetPrice);

        if (!string.IsNullOrEmpty(ticker))
            _targetPriceView.LoadTicker(ticker);
    }

    //private void ShowEditWatchlist()
    //{
    //    if (_editWatchlistView is null)
    //    {
    //        _editWatchlistView = new EditWatchlistView(_db);
    //        _editWatchlistView.StatusChanged += Status;
    //    }
    //    SwitchView(_editWatchlistView, null, "Edit Watchlist");
    //}

    private void SwitchView(object view, string id, Button? activeBtn)
    {
        MainContent.Content = view;
        TxtStatus.Text = "";// label;
        TxtElapsed.Text     = "";
        HighlightToolButton(activeBtn);

        if (view is INavigationAware navAwareView) { navAwareView.OnNavigatedTo(id); }
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

    //void IMainActions.ShowChart(string ticker)
    //{
    //    ShowChart(ticker);
    //}

    //public void SetStatus(string text)
    //{
    //    throw new NotImplementedException();
    //}



    // ══════════════════════════════════════════════════════════
    //  Menu actions
    // ══════════════════════════════════════════════════════════
    public void ActionHandler(MenuAction action, string? context)
    {
        if (string.IsNullOrEmpty(context)) return;

        switch (action)
        {
            // 1. for Ticker
            case MenuAction.DrawChart:
                // MainWindow가 구독 중인 이벤트를 호출하여 차트 뷰로 이동합니다.
                //TickerDoubleClicked?.Invoke(context, context);
                ShowChart(context);
                return;

            case MenuAction.FindGroup:
                StatusWarning($"Find Group: {context}");
                return;

            case MenuAction.WebInfo:
                Helpers.OpenExternalLink(context);
                return;

            // 2. for Group
            case MenuAction.NewGroup:
            case MenuAction.GroupInfo:
            case MenuAction.DeleteGroup:
                StatusInfo($"no action defined: Group: action={action}, context={context}");
                return;

            // 3. for DB
            case MenuAction.Db_QueryInfo:
                ShowDbBrowser(context);
                return;

            case MenuAction.Db_DeletePrices:
            case MenuAction.Db_DeleteSupply:
            case MenuAction.Db_DeleteFundamentals:
            case MenuAction.Db_RemoveTicker:
                HandleDbMenuAction(action, context);
                return;
        }
        StatusInfo($"no action defined: action={action}, context={context}");
    }

    private void HandleDbMenuAction(MenuAction action, string ticker)
    {
        StatusInfo($"no action defined: HandleDbMenuAction={action}, ticker={ticker}");
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
