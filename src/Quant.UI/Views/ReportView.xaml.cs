using Quant.Core.Infrastructure;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quant.UI.Views;

public partial class ReportView : UserControl
{
    public event Action<string, string>? StatusChanged;

    private readonly DbManager _db = DbManager.Instance;

    // pdf_reports 전체 (필터링 기준 원본)
    private DataTable _allReports = new();

    public ReportView()
    {
        InitializeComponent();
        Loaded += (_, _) => OnOpen();
    }

    // ══════════════════════════════════════════════════════════
    //  Entry point
    // ══════════════════════════════════════════════════════════

    private void OnOpen()
    {
        Ingest();
        LoadGrids();
    }

    // ══════════════════════════════════════════════════════════
    //  A. Ingest
    // ══════════════════════════════════════════════════════════

    private void Ingest()
    {
        var opts   = _db.LoadOptions();
        var folder = opts.ReportPdfFolder;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            SetStatus("report_pdf_folder 미설정 — Options에서 폴더를 지정하세요", "#F9E2AF");
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

        var files    = Directory.GetFiles(folder, "*.pdf", SearchOption.TopDirectoryOnly);
        int upserted = 0;
        DateOnly maxDate = lastDate;

        foreach (var path in files)
        {
            // ① 이미 등록된 파일 → skip (해시 계산 없음)
            if (registered.Contains(path)) continue;

            // ② 파일명 파싱 실패 → skip
            var parsed = ParseFilename(Path.GetFileNameWithoutExtension(path));
            if (parsed is null) continue;
            var (fileDate, ticker, title, writer) = parsed.Value;

            // ③ 날짜 기준 skip
            if (fileDate < lastDate) continue;

            // ④ 신규 파일만 해시 계산 + upsert
            var hash = ComputeHash(path);
            UpsertReport(fileDate, ticker, title, writer, path, hash);
            upserted++;

            if (fileDate > maxDate) maxDate = fileDate;
        }

        if (maxDate > lastDate)
            UpsertOptionRaw("last_report_date", maxDate.ToString("yyyy-MM-dd"));

        if (upserted > 0)
            SetStatus($"수집 완료: {upserted}건 추가 (기준일 → {maxDate})", "#A6E3A1");
    }

    // ──────────────────────────────────────────────────────────
    //  파일명 파싱: YYYYMMDD_종목명_리포트제목_작성자.pdf
    // ──────────────────────────────────────────────────────────
    private static (DateOnly date, string ticker, string title, string writer)?
        ParseFilename(string stem)
    {
        var parts = stem.Split('_');
        if (parts.Length < 4) return null;
        if (!DateOnly.TryParseExact(parts[0], "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)) return null;

        var ticker = parts[1].Trim();
        var writer = parts[^1].Trim();
        var title  = string.Join("_", parts[2..^1]).Trim();

        if (string.IsNullOrEmpty(ticker)) return null;

        return (date, ticker, title, writer);
    }

    // ──────────────────────────────────────────────────────────
    //  DuckDB 네이티브 단일값 조회 ($1 파라미터)
    // ──────────────────────────────────────────────────────────
    private T? QueryFirstNative<T>(string sql, string param)
    {
        using var conn = _db.OpenNativeConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = param });
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return default;
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    // ──────────────────────────────────────────────────────────
    //  MD5 해시 (중복 방지용, 보안 목적 아님)
    // ──────────────────────────────────────────────────────────
    private static string ComputeHash(string path)
    {
        using var fs  = File.OpenRead(path);
        using var md5 = MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
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
        var existingId = QueryFirstNative<int?>(
            "SELECT id FROM pdf_reports WHERE file_hash = $1", hash);
        if (existingId.HasValue)
        {
            var sameFile = QueryFirstNative<int?>(
                "SELECT id FROM pdf_reports WHERE filepath = $1", filepath);
            if (!sameFile.HasValue) return;
        }

        using var conn = _db.OpenNativeConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO pdf_reports (date, ticker, title, writer, filepath, file_hash, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, now())
            ON CONFLICT (filepath) DO UPDATE SET
                date      = excluded.date,
                ticker    = excluded.ticker,
                title     = excluded.title,
                writer    = excluded.writer,
                file_hash = excluded.file_hash";
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = date.ToString("yyyy-MM-dd") });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = title });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = writer });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = filepath });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = hash });
        cmd.ExecuteNonQuery();
    }

    private void UpsertOptionRaw(string key, string value)
    {
        using var conn = _db.OpenNativeConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO options (key, value, updated_at)
            VALUES ($1, $2, now())
            ON CONFLICT (key) DO UPDATE SET
                value      = excluded.value,
                updated_at = now()";
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = key });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = value });
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════
    //  B. Grid 로드
    // ══════════════════════════════════════════════════════════

    private void LoadGrids()
    {
        try
        {
            _allReports = _db.Query(
                "SELECT id, date, ticker, title, writer, filepath " +
                "FROM pdf_reports ORDER BY date DESC, id DESC LIMIT 5000");

            ApplyReportFilter(null);
            RefreshCompanyGrid();

            TxtRowCount.Text = $"{_allReports.Rows.Count:N0} rows";
            SetStatus($"리포트 {_allReports.Rows.Count:N0}건", "#A6E3A1");
        }
        catch (Exception ex) { SetStatus($"오류: {ex.Message}", "#F38BA8"); }
    }

    // ──────────────────────────────────────────────────────────
    //  GridCompany: 첫 행 고정 All + ticker별 건수
    // ──────────────────────────────────────────────────────────
    private void RefreshCompanyGrid()
    {
        var dt = new DataTable();
        dt.Columns.Add("ticker", typeof(string));
        dt.Columns.Add("건수",   typeof(int));

        dt.Rows.Add("All", _allReports.Rows.Count);

        var grouped = _allReports.AsEnumerable()
            .GroupBy(r => r["ticker"]?.ToString() ?? "")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key);

        foreach (var g in grouped)
            dt.Rows.Add(g.Key, g.Count());

        GridCompany.ItemsSource   = dt.DefaultView;
        GridCompany.SelectedIndex = 0;
    }

    // ──────────────────────────────────────────────────────────
    //  GridReport 필터
    // ──────────────────────────────────────────────────────────
    private void ApplyReportFilter(string? ticker)
    {
        _allReports.DefaultView.RowFilter =
            ticker is null || ticker == "All"
                ? ""
                : $"ticker = '{ticker.Replace("'", "''")}'";
        GridReport.ItemsSource = _allReports.DefaultView;
        TxtRowCount.Text = $"{_allReports.DefaultView.Count:N0} rows";
    }

    // ══════════════════════════════════════════════════════════
    //  Event handlers
    // ══════════════════════════════════════════════════════════

    private void Grid_CompanyClick(object sender, SelectionChangedEventArgs e)
    {
        if (GridCompany.SelectedItem is not DataRowView row) return;
        ApplyReportFilter(row["ticker"]?.ToString());
    }

    private void Grid_ReportClick(object sender, MouseButtonEventArgs e)
    {
        if (GridReport.SelectedItem is not DataRowView row) return;
        var filepath = row["filepath"]?.ToString();
        if (string.IsNullOrWhiteSpace(filepath)) { TxtStatus.Text = "파일 경로 없음"; return; }
        if (!File.Exists(filepath))              { TxtStatus.Text = $"파일 없음: {filepath}"; return; }
        OpenWithChrome(filepath);
    }

    // ══════════════════════════════════════════════════════════
    //  Chrome 열기
    // ══════════════════════════════════════════════════════════

    private void OpenWithChrome(string path)
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        ];
        var chrome = candidates.FirstOrDefault(File.Exists);
        try
        {
            if (chrome is not null) Process.Start(chrome, $"\"{path}\"");
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetStatus($"열기: {Path.GetFileName(path)}", "#89B4FA");
        }
        catch (Exception ex) { SetStatus($"열기 오류: {ex.Message}", "#F38BA8"); }
    }

    // ══════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════

    private void SetStatus(string msg, string hex)
    {
        TxtStatus.Text = msg;
        StatusChanged?.Invoke(msg, hex);
    }
}
