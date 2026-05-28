using OpenTK.Windowing.GraphicsLibraryFramework;

using Quant.Core.Infrastructure;

using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quant.UI.Views;

//TODO: TxtSliderFrom/TxtSliderTo 에 현재 범위 표시 (report 목록의 min/max 날자 이용)
//TODO: SliderFrom/SliderTo 에서 조절한 날자 범위 이용하여 목록 filtering

public partial class ReportView : UserControl, INavigationAware
{
    //public event Action<string, string>? StatusChanged;
    //public event Action<string, string>? TickerDoubleClicked;  // (ticker, name)

    private readonly IMainActions _main;
    private readonly DbManager _db;
    private DataTable _allReports = new();

    private readonly string TitleForAll = "- All";
    private readonly string TitleForNoTP = "- No TP";
    private readonly string RowFilterNoTP = "target_price IS NULL AND analyze_status <> 'skip'";
    private int needExtractCount;

    public ReportView(IMainActions main)
    {
        _main = main;
        _db = App.DB;// main.Db;
        InitializeComponent();
        Loaded += (_, _) => OnOpen();

        Ingest();
        LoadGrids();
    }

    // ══════════════════════════════════════════════════════════
    //  Entry point
    // ══════════════════════════════════════════════════════════

    private void OnOpen()
    {
        //MigrateTickerNamesToCode();   // 기존 DB 데이터: name → code 1회 변환
        //Ingest();
        //LoadGrids();
    }

    public void OnNavigatedTo(string id)
    {
        //TODO: GridCompany에서 해당 종목 선택 (id == ticker)

        _allReports.DefaultView.RowFilter = string.IsNullOrEmpty(id) ? "" : $"ticker = '{id}'";

        //_allReports.DefaultView.RowFilter = RowFilterNoTP;// "target_price IS NULL AND analyze_status <> 'skip'";// RowFilterNoTP;

        //if (string.IsNullOrEmpty(id) || GridCompany.ItemsSource is not DataView dv) return;
        //for (int i = 0; i < dv.Count; i++)
        //{
        //    if (dv[i]["id"]?.ToString() == _pendingTicker)
        //    {
        //        GridCompany.SelectedIndex = i;
        //        GridCompany.ScrollIntoView(dv[i]);
        //        _pendingTicker = null;
        //        break;
        //    }
        //}
    }

    // ══════════════════════════════════════════════════════════
    //  A. Ingest
    // ══════════════════════════════════════════════════════════
    private void Ingest()
    {
        // ──────────────────────────────────────────────────────────
        //  MD5 해시 (중복 방지용, 보안 목적 아님)
        // ──────────────────────────────────────────────────────────
        static string ComputeHash(string path)
        {
            using var fs = File.OpenRead(path);
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
        }

        var folder = App.Config().ReportPdfFolder;
        //var opts   = _db.LoadOptions();
        //var folder = opts.ReportPdfFolder;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _main.StatusWarning("report_pdf_folder 미설정 — Options에서 폴더를 지정하세요");
            return;
        }

        // DB에 등록된 filepath 전체를 HashSet으로 선로드
        // → 파일 루프 안에서 O(1) 조회, 등록 파일은 해시 계산 없이 즉시 skip
        var registered = _db.Query("SELECT filepath FROM pdf_reports")
                            .AsEnumerable()
                            .Select(r => r[0]?.ToString() ?? "")
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lastDateRaw = _db.QueryFirst<string?>(
            "SELECT value FROM options WHERE key = 'last_report_date'");
        DateOnly lastDate = DateOnly.TryParse(lastDateRaw, out var ld) ? ld : DateOnly.MinValue;

        var files = Directory.GetFiles(folder, "*.pdf", SearchOption.TopDirectoryOnly);
        int upserted = 0;
        DateOnly maxDate = lastDate;

        foreach (var path in files)
        {
            // ① 이미 등록된 파일 → skip (해시 계산 없음)
            if (registered.Contains(path)) continue;

            // ② 파일명 파싱 실패 → skip
            var parsed = ParseFilename(Path.GetFileNameWithoutExtension(path));
            if (parsed is null) continue;
            var (fileDate, commpany, title, writer) = parsed.Value;

            // ③ 날짜 기준 skip
            if (fileDate < lastDate) continue;

            // ④ 파일명의 종목명(commpany)을 종목코드(code)로 변환
            //    stocks 테이블에 없는 이름이면 원본 이름 그대로 저장
            var code = _db.Name2Ticker(commpany);
            var ticker = string.IsNullOrEmpty(code) ? commpany : code;

            // ⑤ 신규 파일만 해시 계산 + upsert
            var hash = ComputeHash(path);
            UpsertReport(fileDate, ticker, title, writer, path, hash);
            upserted++;

            if (fileDate > maxDate) maxDate = fileDate;
        }

        if (maxDate > lastDate)
            _db.SaveOption("last_report_date", maxDate.ToString("yyyy-MM-dd"));

        if (upserted > 0)
            _main.StatusSuccess($"수집 완료: {upserted}건 추가 (기준일 → {maxDate})");
    }

    // ──────────────────────────────────────────────────────────
    //  기존 DB 데이터 마이그레이션: ticker(name) → ticker(code) 1회 변환
    //  이미 6자리 숫자 코드인 행은 skip
    // ──────────────────────────────────────────────────────────
    private void MigrateTickerNamesToCode()
    {
        var rows = _db.Query("SELECT id, ticker FROM pdf_reports WHERE ticker IS NOT NULL");
        int migrated = 0;
        foreach (DataRow row in rows.Rows)
        {
            if (!long.TryParse(row["id"]?.ToString(), out var id)) continue;
            var ticker = row["ticker"]?.ToString() ?? "";

            // 이미 6자리 숫자 코드 형식이면 skip
            if (System.Text.RegularExpressions.Regex.IsMatch(ticker, @"^\d{6}$")) continue;

            var code = _db.Name2Ticker(ticker);
            if (string.IsNullOrEmpty(code)) continue;   // 미등록 종목 → 그대로 유지

            _db.Execute("UPDATE pdf_reports SET ticker = $1 WHERE id = $2", code, id);
            migrated++;
        }
        if (migrated > 0)
            _main.StatusSuccess($"[마이그레이션] ticker name→code 변환: {migrated}건");
    }

    // ──────────────────────────────────────────────────────────
    //  파일명 파싱: YYYYMMDD_종목명_리포트제목_작성자.pdf
    // ──────────────────────────────────────────────────────────
    private static (DateOnly date, string commpany, string title, string writer)?
        ParseFilename(string stem)
    {
        var parts = stem.Split('_');
        if (parts.Length < 4) return null;
        if (!DateOnly.TryParseExact(parts[0], "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)) return null;

        var commpany = parts[1].Trim();
        var writer = parts[^1].Trim();
        var title = string.Join("_", parts[2..^1]).Trim();

        if (string.IsNullOrEmpty(commpany)) return null;
        return (date, commpany, title, writer);
    }

    // ──────────────────────────────────────────────────────────
    //  pdf_reports upsert
    //  (A) filepath 동일           → 메타 갱신
    //  (B) file_hash 동일 + 다른 filepath → 중복 파일 → skip
    // ──────────────────────────────────────────────────────────
    private void UpsertReport(DateOnly date, string ticker, string title,
                              string writer, string filepath, string hash)
    {
        // (B) 동일 hash 가 다른 filepath 로 이미 존재하면 skip
        var existingId = _db.QueryFirst<int?>(
            "SELECT id FROM pdf_reports WHERE file_hash = $hash", new { hash });
        if (existingId.HasValue)
        {
            var sameFile = _db.QueryFirst<int?>(
                "SELECT id FROM pdf_reports WHERE filepath = $filepath", new { filepath });
            if (!sameFile.HasValue) return;
        }

        using var conn = _db.OpenNativeConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO pdf_reports (date, ticker, title, writer, filepath, file_hash, analyze_status, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, 'pending', now())
            ON CONFLICT (filepath) DO UPDATE SET
                date      = excluded.date,
                ticker    = excluded.ticker,
                title     = excluded.title,
                writer    = excluded.writer,
                file_hash = excluded.file_hash
                -- analyze_status/target_price 는 덮어쓰지 않음 (분석 결과 보존)";
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = date.ToString("yyyy-MM-dd") });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = title });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = writer });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = filepath });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = hash });
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════
    //  B. Grid 로드
    // ══════════════════════════════════════════════════════════
    private void LoadGrids()
    {
        try
        {
            var TpIsInvalid= "r.writer = '한국IR협의회' OR r.writer LIKE '%평가정보%'";
            _allReports = _db.Query(
                "SELECT r.id, r.date, r.ticker, s.name AS name, s.sector AS \"group\", r.title, r.writer, r.filepath, " +
                $"CASE WHEN {TpIsInvalid} THEN CAST('NaN' AS DOUBLE) ELSE r.target_price END AS target_price, " +
                "r.analyze_status, c.current_price AS price, " +
                $"CASE WHEN {TpIsInvalid} THEN CAST('NaN' AS DOUBLE) " +
                "     ELSE ROUND((r.target_price / NULLIF(c.current_price, 0) - 1) * 100, 2) " +
                "END AS upside " +
                "FROM pdf_reports r " +
                "LEFT JOIN v_stock_primary_sector s ON s.ticker = r.ticker " +
                "LEFT JOIN stock_cache c ON c.ticker = r.ticker " +
//$"WHERE {TpIsInvalid} " +
                "ORDER BY r.date DESC, r.ticker ASC LIMIT 5000");

            needExtractCount = _db.Scalar<int>($"select count(*) from pdf_reports where {RowFilterNoTP}");
            RefreshTpCount();

            ApplyFilter(null, null);    // reset 종목별, 날짜별 필터
            RefreshAddtionalFilterGrid();

            //TODO: TxtSliderFrom/TxtSliderTo 에 min/max date 표시
            //TODO: SliderFrom/SliderTo 에 min/max date 이용하여 범위 설정

            //TxtRowCount.Text = $"{_allReports.Rows.Count:N0} rows";
            _main.StatusSuccess($"Total {_allReports.Rows.Count:N0}");
        }
        catch (Exception ex) { _main.StatusException(ex, "로드 오류"); }
    }

    // ──────────────────────────────────────────────────────────
    //  GridCompany: 첫 행 고정 -All + ticker별 건수
    //      종목 list, 날짜 list
    // ──────────────────────────────────────────────────────────
    private void RefreshAddtionalFilterGrid()
    {
        void FillGrid_GroupedBy(DataGrid grid, string textColumn, string idColumn)
        {
            var dt = new DataTable();
            dt.Columns.Add(textColumn, typeof(string));
            dt.Columns.Add("cnt", typeof(int));
            dt.Columns.Add("id", typeof(string)); // 내부 식별용 컬럼

            dt.Rows.Add(TitleForAll, _allReports.Rows.Count, "");  // 1행: -All
            dt.Rows.Add(TitleForNoTP, needExtractCount, "");  // 2행: -No TP
            var grouped = _allReports.AsEnumerable()
                .GroupBy(r => r[textColumn]?.ToString() ?? "")
                .Select(g => new
                {
                    Display = g.Key,
                    Count = g.Count(),
                    Id = !string.IsNullOrEmpty(idColumn) ? g.First()[idColumn]?.ToString() ?? "" : g.Key
                })
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Display);

            foreach (var g in grouped)
                dt.Rows.Add(g.Display, g.Count, g.Id);

            grid.ItemsSource = dt.DefaultView;
            grid.SelectedIndex = 0;
            Helpers.SetColumnAlign(grid, 1, TextAlignment.Right);
        }

        FillGrid_GroupedBy(GridCompany, "name", "ticker");
        FillGrid_GroupedBy(GridDate, "date", "date");
        if (GridDate.ItemsSource is DataView dateView)
            dateView.Sort = "date DESC";
    }

    // id 컬럼이 화면에 자동 생성되는 것을 방지
    private void OnGridAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == "id") e.Cancel = true;
    }

    // ──────────────────────────────────────────────────────────
    //  GridReport 필터
    //      종목별, 날짜별, 또는 전체(-All) 보기
    // ──────────────────────────────────────────────────────────
    private void ApplyFilter(string? key, string? value)
    {
        if (key is null || value is null || value == TitleForAll)
        {
            _allReports.DefaultView.RowFilter = "";
        }
        else if (value == TitleForNoTP)
        {
            _allReports.DefaultView.RowFilter = RowFilterNoTP;// "target_price IS NULL AND analyze_status <> 'skip'";// RowFilterNoTP;
        } else
        {
            string escapedValue = value.Replace("'", "''");
            // DateOnly 컬럼과 String 비교 시 발생하는 EvaluateException 방지
            _allReports.DefaultView.RowFilter = (key == "date")
                ? $"Convert({key}, 'System.String') = '{escapedValue}'"
                : $"{key} = '{escapedValue}'";
        }

        GridReport.ItemsSource = _allReports.DefaultView;
        //TxtRowCount.Text = $"{_allReports.DefaultView.Count:N0} rows";
    }

    // ══════════════════════════════════════════════════════════
    //  Event handlers
    // ══════════════════════════════════════════════════════════
    private void ButtonShowAll_Click(object sender, RoutedEventArgs e)
    {
        ApplyFilter(null, null);
    }
    private void Grid_CompanyClick(object sender, SelectionChangedEventArgs e)
    {
        if (GridCompany.SelectedItem is not DataRowView row) return;
        ApplyFilter("name", row["name"]?.ToString());
    }
    private void Grid_DateClick(object sender, SelectionChangedEventArgs e)
    {
        if (GridDate.SelectedItem is not DataRowView row) return;
        ApplyFilter("date", row["date"]?.ToString());
    }

    // GridReport 행 클릭 시 상태 표시줄에 상세 정보 출력
    private void Grid_ReportClick(object sender, SelectionChangedEventArgs e)
    {
        if (GridReport.SelectedItem is not DataRowView row) return;

        var ticker = row["ticker"]?.ToString() ?? "";
        var name = row["name"]?.ToString() ?? ticker;
        _main.StatusInfo($"{row["id"]}. {name} ({ticker}) {row["filepath"] ?? ""}");
    }
    private void Grid_ReportDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // TP 컬럼 더블클릭 시 편집 모드 진입 — PDF 열기 방지
        if (GridReport.CurrentColumn is DataGridTemplateColumn tc &&
            tc.Header?.ToString() == "TP") return;

        ExecuteAction(MenuAction.OpenReport, null, GridReport);
        //if (GridReport.SelectedItem is not DataRowView row) return;
        //var filepath = row["filepath"]?.ToString();
        //var (ok, message) = Helpers.OpenWithChrome(filepath);
        //Helpers.Status(StatusChanged, message, ok ? StatusColors.Info : StatusColors.Error);
    }

    private void GridReport_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not DataRowView row) return;

        // 바인딩 소스 업데이트가 완료된 후 실행 (Background 우선순위)
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _db.Execute("UPDATE pdf_reports SET target_price = $1, analyze_status = 'done' WHERE id = $2",
                    Convert.ToDouble(row["target_price"]), Convert.ToInt64(row["id"]));

                //using var conn = _db.OpenNativeConnection();
                //using var cmd = conn.CreateCommand();
                //cmd.CommandText = "UPDATE pdf_reports SET target_price = $1, analyze_status = 'done' WHERE id = $2";
                //cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = Convert.ToDouble(row["target_price"]) });
                //cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = Convert.ToInt64(row["id"]) });
                //cmd.ExecuteNonQuery();

                _main.StatusSuccess($"목표주가 저장: {row["name"]} → {row["target_price"]:N0}");
            }
            catch (Exception ex)
            {
                _main.StatusException(ex, "TP 저장 오류");
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void TextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()));
    }

    // ══════════════════════════════════════════════════════════
    //  Menu actions
    // ══════════════════════════════════════════════════════════
    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        ContextMenuHelper.ShowGridMenu(sender, e, ExecuteAction);
    }

    private void ExecuteAction(MenuAction action, string? context, object source)
    {
        if (source is not DataGrid grid || grid.SelectedItem is not DataRowView rowView) return;

        switch (action)
        {
            case MenuAction.OpenReport:
                if (grid == GridReport)
                {
                    var filepath = rowView["filepath"]?.ToString();
                    var (ok, message) = Helpers.OpenWithChrome(filepath);
                    //_main.Status(message, ok ? StatusColors.Info : StatusColors.Error);
                }
                else
                {
                    _main.StatusInfo("no action defined for this grid");
                }
                return;

            case MenuAction.DeleteReport:
                if (grid == GridReport)
                    DeleteReport(rowView);
                else if (grid == GridDate)
                {
                    var date = rowView["date"]?.ToString();
                    if (string.IsNullOrEmpty(date)) return;
                    if (MessageBox.Show($"[{date}] 날짜의 모든 리포트를 삭제하시겠습니까?", "삭제 확인",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    try
                    {
_main.StatusInfo($"no action: delete file: {date}");
return;

                        int affected = _db.Execute("DELETE FROM pdf_reports WHERE date = $1", date);
                        _main.StatusSuccess($"삭제 완료: {affected}건");
                        LoadGrids(); // 변경 사항 반영을 위해 그리드 새로고침

                        //TODO: 파일 삭제
                        //TODO: 파일 삭제는 별도의 스크립트로 일괄 처리 권장 (파일 경로가 DB에 저장되어 있지만 실제 파일이 존재하지 않는 경우가 많음)
                    }
                    catch (Exception ex)
                    {
                        _main.StatusException(ex, $"fail to delete: {date}");
                    }
                }
                return;
        }

        if (string.IsNullOrEmpty(context) || context == TitleForAll || context == TitleForNoTP) return;
        _main.ActionHandler(action, context);
    }

    private void DeleteReport(DataRowView rowView)
    {
        if (rowView is null) return;
        if (!int.TryParse(rowView["id"]?.ToString(), out var id)) return;

        var filepath = rowView["filepath"]?.ToString() ?? "";

        // ── 확인 다이얼로그 ──
        var title = rowView["title"]?.ToString() ?? filepath;
        var confirm = MessageBox.Show(
            $"삭제하시겠습니까?\n\n{title}\n\n• DB 레코드 삭제\n• 파일 삭제: {filepath}",
            "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _main.StatusInfo($"no action: delete file: {filepath}");
        return;

        // ── DB DELETE ──
        try
        {
            _db.Execute("DELETE FROM pdf_reports WHERE id = $1", id);

            //using var conn = _db.OpenNativeConnection();
            //using var cmd = conn.CreateCommand();
            //cmd.CommandText = "DELETE FROM pdf_reports WHERE id = $1";
            //cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = id });
            //cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _main.StatusException(ex, $"fail to delete from DB: {filepath}");
            return;
        }

        // ── 파일 DELETE ──
        if (!string.IsNullOrWhiteSpace(filepath) && File.Exists(filepath))
        {
            try { File.Delete(filepath); }
            catch (Exception ex) { _main.StatusException(ex, $"fail to delete file: {filepath}"); }
        }

        // ── 메모리 테이블에서 제거 후 그리드 갱신 ──
        rowView.Row.Delete();
        _allReports.AcceptChanges();
        //RefreshAddtionalFilterGrid();

        //TxtRowCount.Text = $"{_allReports.DefaultView.Count:N0} rows";
        RefreshTpCount();
        _main.StatusSuccess($"삭제 완료: {Path.GetFileName(filepath)}");
    }

    private void RefreshTpCount()
    {
        var totalCount = _allReports.Rows.Count;
        //var totalCount = _db.Scalar<int>("select count(*) from pdf_reports where 1=1");
        //var needExtractCount = _db.Scalar<int>("select count(*) from pdf_reports where target_price is null and analyze_status='pending'");
        //var needExtractCount = _db.Scalar<int>("select count(*) from pdf_reports where target_price is null and analyze_status!='skip'");
        //var needExtractCount = _db.Scalar<int>($"select count(*) from pdf_reports where {RowFilterNoTP}");
        BtnSkipTP.Content = $"{needExtractCount} / {totalCount}";
    }
    private void BtnSkipTP_Click(object sender, RoutedEventArgs e)
    {
        _db.Execute($"update pdf_reports set analyze_status='skip' where {RowFilterNoTP}");
        LoadGrids();

        //RefreshTpCount();
        //"update pdf_reports set analyze_status='skip' where target_price is null and analyze_status!='skip'"
    }
}
