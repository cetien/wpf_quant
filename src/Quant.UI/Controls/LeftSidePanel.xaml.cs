using System.Windows;
using System.Windows.Controls;

namespace Quant.UI.Controls;

public partial class LeftSidePanel : UserControl
{
    private readonly LeftSidePanelViewModel _vm;

    // ──────────────────────────────────────────────────────────
    //  외부 이벤트 (MainWindow에서 구독)
    // ──────────────────────────────────────────────────────────
    /// <summary>그룹 선택 시 발행 (group_id, name)</summary>
    public event Action<int, string>?    GroupSelected;

	/// <summary>종목 선택 시 발행 (ticker, name)</summary>
	public event Action<string, string>? StockSelected;

    // ──────────────────────────────────────────────────────────
    public LeftSidePanel()
    {
        InitializeComponent();

        _vm = new LeftSidePanelViewModel();
        DataContext = _vm;

        // VM 이벤트를 Control 이벤트로 포워딩
        _vm.GroupSelected += (id, name) => GroupSelected?.Invoke(id, name);
        _vm.StockSelected += (ticker, name) => StockSelected?.Invoke(ticker, name);

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.LoadGroupsCommand.Execute(null);
    }

    // ──────────────────────────────────────────────────────────
    //  외부에서 호출 가능한 공개 API
    // ──────────────────────────────────────────────────────────

    /// <summary>그룹/종목 목록 새로고침</summary>
    public void Reload() => _vm.LoadGroupsCommand.Execute(null);

    /// <summary>
    /// 인디케이터 값 업데이트 (실시간 피드 연동 시 사용)
    /// symbol: "SOX", "WTI", "KOSPI", "SPX", "NDX", "DXY"
    /// </summary>
    public void UpdateIndicator(string symbol, double value, double changePct)
        => _vm.UpdateIndicator(symbol, value, changePct);
}
