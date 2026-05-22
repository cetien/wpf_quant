using Quant.Core.Infrastructure;

using DuckDB.NET.Data;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Quant.UI.Views;

// =============================================================================
// SearchView.xaml.cs  —  종목 검색 뷰 (GridFilter 기반 스크리너)
// =============================================================================
// 구조 개요
//   FilterDef          : 개별 필터 정의 (SQL 절 + 파라미터)
//   FilterRow          : GridFilter에 바인딩되는 ViewModel (INotifyPropertyChanged)
//   FilterPreset       : 복합 필터 프리셋 (FilterRow 묶음)
//   BuildWhereClause() : 활성화된 FilterRow들을 AND로 조합 → SQL WHERE 생성
//   ExecuteSearch()    : WHERE + Market/Type/Rating 필터 조합 → stock_cache 쿼리
//
// stock_cache 컬럼 활용:
//   current_price, ma20, ma60, ma120, volume_ratio, high_60d, high_120d,
//   high_52w, rs, ret_1m/3m/6m/1y, atr_percent, distance_from_high
//
// 구현 제외 (stock_cache에 전일값 없음 → Phase 2):
//   골든크로스/데드크로스 계열 (12~43번) — daily_prices 2행 조인 필요
// =============================================================================

// ── FilterDef: 필터 정의 레코드 ─────────────────────────────────────────────
// {P} 플레이스홀더는 FilterRow.ParamValue로 치환됨
internal record FilterDef(
    string Id,
    string Name,
    string Sql,               // DuckDB stock_cache 컬럼 기준 WHERE 절. {P} = 사용자 파라미터
    string? DefaultParam,     // null = 파라미터 없음
    string? ParamHint         // TextBox placeholder
);

// ── FilterRow: GridFilter 바인딩 ViewModel ──────────────────────────────────
internal class FilterRow : INotifyPropertyChanged
{
    private bool   _isActive;
    private string _paramValue = "";

    public string Id          { get; init; } = "";
    public string Name        { get; init; } = "";
    public string SqlTemplate { get; init; } = "";  // {P} 포함 가능
    public string ParamHint   { get; init; } = "";  // TextBox placeholder
    public bool   HasParam    => !string.IsNullOrEmpty(ParamHint);

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnChanged(); }
    }

    public string ParamValue
    {
        get => _paramValue;
        set { _paramValue = value; OnChanged(); }
    }

    // SQL 절 생성 — {P}를 ParamValue로 치환
    public string? BuildSql()
    {
        if (!IsActive) return null;
        if (HasParam && string.IsNullOrWhiteSpace(ParamValue)) return null;
        return SqlTemplate.Replace("{P}", ParamValue.Trim());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

// ── FilterPreset ─────────────────────────────────────────────────────────────
internal record FilterPreset(string Name, string[] FilterIds);

// ============================================================================
public partial class SearchView : UserControl
{
    // ── 이벤트 ──────────────────────────────────────────────────────────────
    public event Action<string, string>? StockSelected;   // (ticker, name)
    public event Action<string, string>? StatusChanged;   // (message, colorHex)

    // ── 상태 ────────────────────────────────────────────────────────────────
    private readonly DbManager _db;
    private readonly ObservableCollection<FilterRow> _filterRows = [];
    private DispatcherTimer? _debounce;

    // ════════════════════════════════════════════════════════════════════════
    //  필터 정의 목록 (stock_cache 컬럼 기준, 골든크로스 계열 제외)
    // ════════════════════════════════════════════════════════════════════════
    private static readonly FilterDef[] FilterDefs =
    [
        // ── 추세/정배열 ─────────────────────────────────────────────────────
        new("ma_align",
            "정배열",
            "current_price > ma20 AND ma20 > ma60 AND ma60 > ma120",
            null, null),

        new("above_ma20",
            "MA20 위",
            "current_price > ma20",
            null, null),

        new("above_ma60",
            "MA60 위",
            "current_price > ma60",
            null, null),

        new("above_ma120",
            "MA120 위",
            "current_price > ma120",
            null, null),

        new("near_ma20",
            "MA20 근처 ±%",
            "ABS(current_price - ma20) / NULLIF(ma20, 0) <= {P} / 100.0",
            "3", "% (예: 3)"),

        // ── 신고가 ──────────────────────────────────────────────────────────
        new("near_high120",
            "120일 신고가 근처 ±%",
            "current_price >= high_120d * (1 - {P} / 100.0)",
            "2", "% (예: 2)"),

        new("break_high120",
            "120일 신고가 돌파",
            "current_price > high_120d",
            null, null),

        new("near_high60",
            "60일 신고가 근처 ±%",
            "current_price >= high_60d * (1 - {P} / 100.0)",
            "2", "% (예: 2)"),

        // ── RS ──────────────────────────────────────────────────────────────
        new("rs_strong",
            "RS 강함 ≥",
            "rs >= {P}",
            "85", "값 (예: 85)"),

        new("rs_top",
            "RS 상위 ≥",
            "rs >= {P}",
            "90", "값 (예: 90)"),

        // ── 거래량 ──────────────────────────────────────────────────────────
        new("vol_surge",
            "거래량 급증 ≥ ×",
            "volume_ratio >= {P}",
            "2", "배 (예: 2)"),

        new("vol_increase",
            "거래량 증가 ≥ ×",
            "volume_ratio >= {P}",
            "1.5", "배 (예: 1.5)"),

        // ── 조정/눌림목 ─────────────────────────────────────────────────────
        new("pullback",
            "눌림목 (정배열+MA20 근처)",
            "current_price > ma60 AND ABS(current_price - ma20) / NULLIF(ma20, 0) < 0.03",
            null, null),

        new("drawdown_60",
            "고점 대비 조정 (60일) 하 %",
            "(high_60d - current_price) / NULLIF(high_60d, 0) BETWEEN {P} / 100.0 AND {P2} / 100.0",
            "10", "하단% (예: 10)"),   // {P2} 처리는 아래 BuildSql 확장으로

        new("rebound_60",
            "저점 반등 (60일) ≥ %",
            "current_price > low_52w AND (current_price - low_52w) / NULLIF(low_52w, 0) >= {P} / 100.0",
            "10", "% (예: 10)"),

        // ── 밸류에이션 ──────────────────────────────────────────────────────
        new("per_low",
            "PER ≤",
            "per > 0 AND per <= {P}",
            "15", "배 (예: 15)"),

        new("pbr_low",
            "PBR ≤",
            "pbr > 0 AND pbr <= {P}",
            "1.5", "배 (예: 1.5)"),

        // ── 리포트 / 목표가 ──────────────────────────────────────────────────
        new("report_count_min",
            "리포트 수 ≥",
            "report_count >= {P}",
            "5", "건 (예: 5)"),

        new("upside_min",
            "상승여력 ≥ %",
            "target_price > 0 AND (target_price / NULLIF(current_price, 0) - 1) * 100 >= {P}",
            "50", "% (예: 50)"),
    ];

    // ── 프리셋 정의 ─────────────────────────────────────────────────────────
    private static readonly FilterPreset[] Presets =
    [
        new("★ RS강함+정배열",       ["rs_strong",  "ma_align"]),
        new("★ RS상위+정배열+거래량", ["rs_top",     "ma_align", "vol_increase"]),
        new("★ 신고가+거래량급증",    ["near_high120","vol_surge"]),
        new("★ 눌림목",              ["pullback"]),
        new("★ 정배열+눌림목+RS강함", ["ma_align",  "pullback", "rs_strong"]),
        new("★ 리포트집중+상승여력",    ["report_count_min", "upside_min"]),
    ];

    // ════════════════════════════════════════════════════════════════════════
    public SearchView(DbManager db)
    {
        _db = db;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildFilterRows();
        BuildPresetButtons();

        // 디바운스 타이머
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ExecuteSearch(); };
        TxtQuery.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };

        TxtWatchlistName.Text = DateTime.Today.ToString("yyyy-MM-dd");
        TxtQuery.Focus();
        SetStatus("필터를 선택하거나 종목명/Ticker를 입력하세요.", StatusColors.Muted);
    }

    // ── 필터 행 초기화 ───────────────────────────────────────────────────────
    private void BuildFilterRows()
    {
        _filterRows.Clear();
        foreach (var def in FilterDefs)
        {
            var row = new FilterRow
            {
                Id          = def.Id,
                Name        = def.Name,
                SqlTemplate = def.Sql,
                ParamValue  = def.DefaultParam ?? "",
                ParamHint   = def.ParamHint ?? "",
            };
            // 필터 변경 시 자동 재검색
            row.PropertyChanged += (_, _) => TriggerSearch();
            _filterRows.Add(row);
        }
        GridFilter.ItemsSource = _filterRows;
    }

    // ── 프리셋 버튼 동적 생성 ───────────────────────────────────────────────
    private void BuildPresetButtons()
    {
        PresetPanel.Children.Clear();
        foreach (var preset in Presets)
        {
            var btn = new Button
            {
                Content     = preset.Name,
                Tag         = preset,
                Margin      = new Thickness(0, 0, 6, 4),
                Padding     = new Thickness(10, 4, 10, 4),
                FontFamily  = new System.Windows.Media.FontFamily("Consolas"),
                FontSize    = 11,
                Cursor      = Cursors.Hand,
            };
            btn.Click += PresetBtn_Click;
            PresetPanel.Children.Add(btn);
        }
    }

    private void PresetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FilterPreset preset) return;

        // 모든 필터 비활성화 후 프리셋 활성화
        foreach (var row in _filterRows) row.IsActive = false;
        foreach (var row in _filterRows)
        {
            if (preset.FilterIds.Contains(row.Id))
            {
                row.IsActive = true;
                // 파라미터는 기본값 유지 (사용자가 이미 수정한 경우 덮어쓰지 않음)
            }
        }
        SetStatus($"프리셋 적용: {preset.Name}", StatusColors.Info);
        ExecuteSearch();
    }

    // ── WHERE 절 빌더 ────────────────────────────────────────────────────────
    private string BuildWhereClause(string q)
    {
        var parts = new List<string>();

        // 텍스트 검색 (name / ticker)
        if (q.Length >= 2)
        {
            var safe = q.Replace("'", "''");
            parts.Add($"(s.name ILIKE '%{safe}%' OR s.ticker ILIKE '{safe}%')");
        }

        // Market / Type / Rating 콤보
        var market = (CmbMarket.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (market is not null && market != "전체")
            parts.Add($"s.market = '{market}'");

        var type = (CmbType.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (type is not null && type != "전체")
            parts.Add($"s.security_type = '{type}'");

        if (int.TryParse(
                (CmbRating.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                out var minRating))
            parts.Add($"s.rating >= {minRating}");

        // GridFilter 활성 조건 (stock_cache 컬럼 기준)
        foreach (var row in _filterRows)
        {
            var sql = row.BuildSql();
            if (sql is not null) parts.Add($"({sql})");
        }

        return parts.Count > 0 ? string.Join(" AND ", parts) : "1=1";
    }

    // ── 검색 실행 ────────────────────────────────────────────────────────────
    private void TriggerSearch()
    {
        _debounce?.Stop();
        _debounce?.Start();
    }

    private void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        _debounce?.Stop();
        ExecuteSearch();
    }

    private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _debounce?.Stop(); ExecuteSearch(); }
    }

    private void ExecuteSearch()
    {
        try
        {
            var q     = TxtQuery.Text.Trim();
            var where = BuildWhereClause(q);
            var excl  = _db.BuildStockExcludeFilter("s", "c");

            var activeCount = _filterRows.Count(r => r.IsActive);
            SetStatus("검색 중...", StatusColors.Muted);

            var sql = $"""
                SELECT
                    s.ticker,
                    s.name,
                    s.market,
                    s.rating,
                    s.security_type,
                    c.current_price,
                    c.ma20,
                    c.ma60,
                    c.ma120,
                    c.volume_ratio,
                    c.high_60d,
                    c.high_120d,
                    c.ret_1m,
                    c.ret_3m,
                    c.ret_6m,
                    c.ret_1y,
                    c.rs,
                    c.atr_percent,
                    c.distance_from_high,
                    c.per,
                    c.pbr,
                    c.roe,
                    c.report_count,
                    c.target_price,
                    ROUND((c.target_price / NULLIF(c.current_price, 0) - 1) * 100, 2) AS upside
                FROM stocks s
                LEFT JOIN stock_cache c ON c.ticker = s.ticker
                WHERE {where} {excl}
                ORDER BY c.rs DESC NULLS LAST
                LIMIT 500
                """;

            var dt = _db.Query(sql);
            GridResults.ItemsSource = dt.DefaultView;

            TxtResultCount.Text = $"{dt.Rows.Count:N0}건";

            var filterDesc = activeCount > 0
                ? $" | 필터 {activeCount}개 적용"
                : "";
            var status = dt.Rows.Count == 500
                ? $"상위 500건 표시 (조건 추가 권장){filterDesc}"
                : $"{dt.Rows.Count:N0}종목 검색됨{filterDesc}";

            SetStatus(status, dt.Rows.Count > 0 ? StatusColors.Success : StatusColors.Warning);
        }
        catch (Exception ex)
        {
            SetStatus($"오류: {ex.Message}", StatusColors.Error);
        }
    }

    // ── 필터 콤보 변경 ───────────────────────────────────────────────────────
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => TriggerSearch();

    // ── 행 선택 ─────────────────────────────────────────────────────────────
    private void GridResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridResults.SelectedItem is not DataRowView row) return;
        var ticker = row["ticker"]?.ToString() ?? "";
        var name   = row["name"]?.ToString() ?? "";
        SetStatus($"선택: {name} ({ticker})", StatusColors.Info);
    }

    private void GridResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridResults.SelectedItem is not DataRowView row) return;
        var ticker = row["ticker"]?.ToString() ?? "";
        var name   = row["name"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(ticker))
            StockSelected?.Invoke(ticker, name);
    }

    // ── Watchlist 등록 ────────────────────────────────────────────────────────
    private void TxtWatchlistName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddToWatchlist();
    }

    private void BtnAddWatchlist_Click(object sender, RoutedEventArgs e) => AddToWatchlist();

    private void AddToWatchlist()
    {
        var name = TxtWatchlistName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            SetStatus("Watchlist 이름을 입력하세요.", StatusColors.Warning);
            TxtWatchlistName.Focus();
            return;
        }

        // 현재 검색 결과 수집
        if (GridResults.ItemsSource is not System.Data.DataView dv || dv.Count == 0)
        {
            SetStatus("등록할 검색 결과가 없습니다.", StatusColors.Warning);
            return;
        }

        var tickers = dv.Cast<System.Data.DataRowView>()
                        .Select(r => r["ticker"]?.ToString() ?? "")
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();

        if (tickers.Count == 0)
        {
            SetStatus("유효한 종목이 없습니다.", StatusColors.Warning);
            return;
        }

        // description = 현재 WHERE 절 (필터 재현용)
        var description = BuildWhereClause(TxtQuery.Text.Trim());

        try
        {
            using var conn = _db.OpenNativeConnection();

            // ── 1. 동일 (kind='watch', name) 그룹 존재 여부 조회
            long groupId;
            using (var cmdSel = conn.CreateCommand())
            {
                cmdSel.CommandText =
                    "SELECT group_id FROM groups WHERE kind = $1 AND name = $2 LIMIT 1";
                cmdSel.Parameters.Add(new DuckDBParameter { Value = "watch" });
                cmdSel.Parameters.Add(new DuckDBParameter { Value = name    });
                using var r = cmdSel.ExecuteReader();
                if (r.Read())
                {
                    // 기존 그룹 재사용 + description 갱신
                    groupId = r.GetInt64(0);
                    using var cmdUpd = conn.CreateCommand();
                    cmdUpd.CommandText =
                        "UPDATE groups SET description = $1, updated_at = CURRENT_TIMESTAMP " +
                        "WHERE group_id = $2";
                    cmdUpd.Parameters.Add(new DuckDBParameter { Value = description });
                    cmdUpd.Parameters.Add(new DuckDBParameter { Value = groupId     });
                    cmdUpd.ExecuteNonQuery();
                }
                else
                {
                    // 신규 INSERT
                    using var cmdIns = conn.CreateCommand();
                    cmdIns.CommandText =
                        "INSERT INTO groups (kind, name, description) VALUES ($1, $2, $3) " +
                        "RETURNING group_id";
                    cmdIns.Parameters.Add(new DuckDBParameter { Value = "watch"      });
                    cmdIns.Parameters.Add(new DuckDBParameter { Value = name         });
                    cmdIns.Parameters.Add(new DuckDBParameter { Value = description  });
                    using var r2 = cmdIns.ExecuteReader();
                    if (!r2.Read()) throw new Exception("group_id 반환 실패");
                    groupId = r2.GetInt64(0);
                }
            }

            // stock_group_map 일괄 삽입
            int inserted = 0;
            foreach (var ticker in tickers)
            {
                using var cmd2 = conn.CreateCommand();
                cmd2.CommandText =
                    "INSERT INTO stock_group_map (ticker, group_id, weight) " +
                    "VALUES ($1, $2, 5) " +
                    "ON CONFLICT (ticker, group_id) DO NOTHING";
                cmd2.Parameters.Add(new DuckDBParameter { Value = ticker  });
                cmd2.Parameters.Add(new DuckDBParameter { Value = groupId });
                inserted += cmd2.ExecuteNonQuery();
            }

            SetStatus(
                $"Watchlist [{name}] 등록 완료 — {inserted}종목 추가 (group_id={groupId})",
                StatusColors.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"등록 오류: {ex.Message}", StatusColors.Error);
        }
    }

    // ── 초기화 ───────────────────────────────────────────────────────────────
    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        TxtQuery.Clear();
        foreach (var row in _filterRows) row.IsActive = false;
        GridResults.ItemsSource = null;
        TxtResultCount.Text = "";
        CmbMarket.SelectedIndex = 0;
        CmbType.SelectedIndex   = 0;
        CmbRating.SelectedIndex = 0;
        SetStatus("초기화되었습니다.", StatusColors.Muted);
        TxtQuery.Focus();
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────────
    private void SetStatus(string msg, string hex)
    {
        TxtStatus.Text = msg;
        StatusChanged?.Invoke(msg, hex);
    }

    // ── 미사용 stub (XAML 이벤트 바인딩 유지용) ─────────────────────────────
    private void FilterActivated_Changed(object sender, RoutedEventArgs e) { }
}
