// Infrastructure/DbManager.cs
using Dapper;

using DuckDB.NET.Data;

using Quant.Core.Models;

using System.Data;
using System.Security.Cryptography;

namespace Quant.Core.Infrastructure;

/// <summary>
/// DB 접근의 유일한 진입점.
/// <para>
/// - DbPath, 연결 생성, 스키마 초기화를 모두 내부에서 처리한다.<br/>
/// - 사용 측은 <c>DbManager.Instance</c> 하나만 알면 된다.<br/>
/// - DI를 사용하는 경우 <c>services.AddSingleton&lt;DbManager&gt;()</c> 등록 후 주입.
/// </para>
/// <example>
/// <code>
/// // DI 없이 직접 사용
/// var db = DbManager.Instance;
/// var dt  = db.Query("SELECT * FROM stocks LIMIT 5");
/// var cnt = db.Scalar&lt;long&gt;("SELECT COUNT(*) FROM daily_prices");
/// db.Execute("UPDATE stocks SET rating=5 WHERE ticker='005930'");
///
/// // DI 주입 사용 (권장)
/// public MyViewModel(DbManager db) { _db = db; }
/// </code>
/// 
/// DbManager 퍼블릭 API			
//      메서드                         반환                  용도
//      Query(sql)                      DataTable           DataGrid 직접 바인딩
//      Execute(sql)                    int                 DDL 없는 DML(string sql 전용)
//      Execute(sql, param)             int                 파라미터 바인딩 DML(Dapper)
//      Scalar<T>(sql)                  T?                  COUNT / MAX 단일 값
//      Query<T>(sql, param?)           IEnumerable<T>      타입 매핑(Repository용)
//      QueryFirst<T>(sql, param?)      T?                  단일 행 타입 매핑
//      IsConnected()                   bool                DB 파일 존재 + 연결 가능 여부
//      DbManager.DbPath                static string       경로 읽기 전용 노출
//      DbManager.Instance              static DbManager    DI 미사용 환경용 Singleton

//// ✅ DML — 네이티브 positional 사용
//cmd.CommandText = "INSERT INTO t (a, b) VALUES ($1, $2)";
//cmd.Parameters.Add(new DuckDBParameter { Value = val1 });
//cmd.Parameters.Add(new DuckDBParameter { Value = val2 });

//// ✅ SELECT — Dapper @Param 작동 (읽기 전용)
//conn.Query<T>("SELECT * FROM t WHERE key = @Key", new { Key = k });

//// ❌ DML에 Dapper @Param — DuckDB에서 미지원
//conn.Execute("INSERT INTO t VALUES (@Key)", new { Key = k });



/// </example>
/// </summary>
public sealed class DbManager
{
    // ──────────────────────────────────────────────────────────
    //  경로 (변경 시 여기만 수정)
    // ──────────────────────────────────────────────────────────

    public static readonly string DbPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "quant", "quant.duckdb");

    private static readonly string MigrationsDir =
        Path.Combine(AppContext.BaseDirectory, "migrations");

    // ──────────────────────────────────────────────────────────
    //  Singleton (DI 미사용 환경용)
    // ──────────────────────────────────────────────────────────

    private static DbManager? _instance;

    /// <summary>
    /// DI 컨테이너 없이 직접 접근하는 전역 인스턴스.
    /// DI를 사용한다면 이 프로퍼티 대신 생성자 주입을 사용할 것.
    /// </summary>
    public static DbManager Instance => _instance ??= new DbManager();

    // ──────────────────────────────────────────────────────────
    //  생성자 — 초기화
    // ──────────────────────────────────────────────────────────

    public DbManager()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        RunMigrations();
    }

    // ──────────────────────────────────────────────────────────
    //  연결 생성 (내부 전용)
    // ──────────────────────────────────────────────────────────

    private DuckDBConnection OpenConnection()
    {
        var conn = new DuckDBConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }

    /// <summary>
    /// DuckDB 네이티브 파라미터($1,$2)가 필요한 DML용 연결 노출.
    /// 호출 측에서 반드시 using으로 닫을 것.
    /// </summary>
    public DuckDBConnection OpenNativeConnection()
    {
        var conn = new DuckDBConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }

    // ──────────────────────────────────────────────────────────
    //  스키마 초기화
    // ──────────────────────────────────────────────────────────

    private void RunMigrations()
    {
        if (!Directory.Exists(MigrationsDir)) return;
        using var conn = OpenConnection();
        foreach (var file in Directory.GetFiles(MigrationsDir, "*.sql").OrderBy(f => f))
            conn.Execute(File.ReadAllText(file));
    }

    // ══════════════════════════════════════════════════════════
    //  핵심 퍼블릭 API  ─  사용 측에서 직접 호출하는 메서드
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// SELECT → DataTable 반환. DbBrowserView·View 등에서 DataGrid에 직접 바인딩.
    /// </summary>
    public DataTable Query(string sql)
    {
        var dt = new DataTable();
        using var conn   = OpenConnection();
        using var cmd    = conn.CreateCommand();
        cmd.CommandText  = sql;
        using var reader = cmd.ExecuteReader();
        for (int i = 0; i < reader.FieldCount; i++)
            dt.Columns.Add(reader.GetName(i), typeof(string));
        while (reader.Read())
        {
            var row = dt.NewRow();
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "";
            dt.Rows.Add(row);
        }
        return dt;
    }

    /// <summary>
    /// SELECT → 단일 스칼라 값 반환. COUNT, MAX 등.
    /// </summary>
    public T? Scalar<T>(string sql)
    {
        using var conn  = OpenConnection();
        using var cmd   = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return default;
        return (T)Convert.ChangeType(result, typeof(T));
    }

    /// <summary>
    /// INSERT / UPDATE / DELETE. 영향 받은 행 수 반환.
    /// </summary>
    public int Execute(string sql)
    {
        using var conn  = OpenConnection();
        using var cmd   = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Dapper 기반 타입 매핑 쿼리. Repository·Service 계층용.
    /// ※ DuckDB는 @Param 바인딩 미지원 — 파라미터가 필요한 DML은 네이티브 cmd 사용.
    /// </summary>
    public IEnumerable<T> Query<T>(string sql, object? param = null)
    {
        using var conn = OpenConnection();
        return conn.Query<T>(sql, param).ToList(); // connection 닫히기 전 ToList()
    }

    /// <summary>
    /// Dapper 기반 단일 행 조회. 없으면 default(T).
    /// </summary>
    public T? QueryFirst<T>(string sql, object? param = null)
    {
        using var conn = OpenConnection();
        return conn.QueryFirstOrDefault<T>(sql, param);
    }

    /// <summary>
    /// Dapper 기반 실행. 파라미터 바인딩이 필요한 INSERT/UPDATE용.
    /// ⚠ DuckDB는 @Param 미지원. SELECT 전용 Dapper param만 사용할 것.
    /// DML에 파라미터가 필요하면 OpenConnection() + DuckDBParameter($1,$2) 사용.
    /// </summary>
    [Obsolete("DuckDB는 Dapper @Param DML 미지원. 네이티브 DuckDBParameter 사용 권장.")]
    public int Execute(string sql, object param)
    {
        using var conn = OpenConnection();
        return conn.Execute(sql, param);
    }

    // ══════════════════════════════════════════════════════════
    //  편의 헬퍼  (이전 DbManager 기능 유지)
    // ══════════════════════════════════════════════════════════

    /// <summary>DB 파일이 존재하고 연결 가능한지 확인.</summary>
    public bool IsConnected()
    {
        if (!File.Exists(DbPath)) return false;
        try { using var conn = OpenConnection(); return true; }
        catch { return false; }
    }

    /// <summary>테이블 존재 여부.</summary>
    public bool TableExists(string table)
    {
        var count = Scalar<long>(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{table}'");
        return count > 0;
    }

    /// <summary>테이블 행 수.</summary>
    public long RowCount(string table)
    {
        var safe = SanitizeIdentifier(table);
        return Scalar<long>($"SELECT COUNT(*) FROM {safe}");
    }

    /// <summary>
    /// 지정 테이블의 date 컬럼 최신 날짜.
    /// ticker 지정 시 해당 ticker 기준, null이면 전체 기준.
    /// </summary>
    public DateOnly? GetLastDate(string table, string dateCol = "date", string? ticker = null)
    {
        var safe    = SanitizeIdentifier(table);
        var safeCol = SanitizeIdentifier(dateCol);

        if (ticker is null)
        {
            var raw = Scalar<string>($"SELECT MAX({safeCol}) FROM {safe}");
            return raw is not null && DateOnly.TryParse(raw, out var d) ? d : null;
        }

        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"SELECT MAX({safeCol}) FROM {safe} WHERE ticker = $1";
        cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return null;
        return DateOnly.TryParse(result.ToString(), out var date) ? date : null;
    }

    // ──────────────────────────────────────────────────────────
    //  Data helpers
    // ──────────────────────────────────────────────────────────
    /// <summary>ticker → 종목명. 없으면 ticker 반환.</summary>
    public (string name, int rating, string market, bool isActive) GetStockInfo(string ticker)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT name, rating, market, is_active FROM stocks WHERE ticker = $1";
        cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (ticker, 0, "", false);

        var name = reader.IsDBNull(0) ? ticker : reader.GetValue(0)?.ToString() ?? ticker;
        var rating = reader.IsDBNull(1) ? 0 : Math.Clamp(Convert.ToInt32(reader.GetValue(1)), 0, 10);
        var market = reader.IsDBNull(2) ? "" : reader.GetValue(2)?.ToString() ?? "";
        var isActive = !reader.IsDBNull(3) && Convert.ToBoolean(reader.GetValue(3));
        return (name, rating, market, isActive);
    }
    public string GetStockName(string ticker) => GetStockInfo(ticker).name;

    public bool SetStockRating(int rating, string ticker) => SetRating("stocks", rating, $"ticker = '{ticker}'");
    public bool SetGroupRating(int rating, int group_id) => SetRating("groups", rating, $"group_id = {group_id}");
    private bool SetRating(string table, int rating, string where)
    {
        try
        {
            var safeTable = SanitizeIdentifier(table);
            using var conn = OpenConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {safeTable} SET rating=$1, updated_at=CURRENT_TIMESTAMP WHERE {where}";
            cmd.Parameters.Add(new DuckDBParameter { Value = rating });
            cmd.ExecuteNonQuery();
            return true;
        }
        catch { return false; }
    }

    public (double current, double ret1m, double ret3m, double ret1y) LoadPrice(string ticker)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            // ticker를 리터럴로 삽입: DuckDB positional $1 다중 CTE 재사용 불안정 방어
            // ticker는 6자리 숫자 코드 또는 IDX_ 접두사 — SQL 인젝션 위험 없음
            var t = ticker.Replace("'", "''"); // 방어적 escape
            cmd.CommandText =
                $"WITH latest AS ( " +
                $"  SELECT adj_close, ROW_NUMBER() OVER (ORDER BY date DESC) AS rn " +
                $"  FROM daily_prices WHERE ticker = '{t}' " +
                $"), " +
                $"price_1m AS ( " +
                $"  SELECT adj_close, " +
                $"    ROW_NUMBER() OVER (ORDER BY ABS(DATEDIFF('day', date, CURRENT_DATE - INTERVAL 1 MONTH))) AS rn " +
                $"  FROM daily_prices " +
                $"  WHERE ticker = '{t}' " +
                $"    AND date BETWEEN CURRENT_DATE - INTERVAL 1 MONTH - INTERVAL 7 DAY " +
                $"                 AND CURRENT_DATE - INTERVAL 1 MONTH + INTERVAL 7 DAY " +
                $"), " +
                $"price_3m AS ( " +
                $"  SELECT adj_close, " +
                $"    ROW_NUMBER() OVER (ORDER BY ABS(DATEDIFF('day', date, CURRENT_DATE - INTERVAL 3 MONTH))) AS rn " +
                $"  FROM daily_prices " +
                $"  WHERE ticker = '{t}' " +
                $"    AND date BETWEEN CURRENT_DATE - INTERVAL 3 MONTH - INTERVAL 7 DAY " +
                $"                 AND CURRENT_DATE - INTERVAL 3 MONTH + INTERVAL 7 DAY " +
                $"), " +
                $"price_1y AS ( " +
                $"  SELECT adj_close, " +
                $"    ROW_NUMBER() OVER (ORDER BY ABS(DATEDIFF('day', date, CURRENT_DATE - INTERVAL 1 YEAR))) AS rn " +
                $"  FROM daily_prices " +
                $"  WHERE ticker = '{t}' " +
                $"    AND date BETWEEN CURRENT_DATE - INTERVAL 1 YEAR - INTERVAL 7 DAY " +
                $"                 AND CURRENT_DATE - INTERVAL 1 YEAR + INTERVAL 7 DAY " +
                $") " +
                $"SELECT " +
                $"  l.adj_close AS current, " +
                $"  ROUND((l.adj_close - p1.adj_close)  / p1.adj_close  * 100, 2) AS ret_1m, " +
                $"  ROUND((l.adj_close - p3.adj_close)  / p3.adj_close  * 100, 2) AS ret_3m, " +
                $"  ROUND((l.adj_close - p1y.adj_close) / p1y.adj_close * 100, 2) AS ret_1y " +
                $"FROM latest l " +
                $"LEFT JOIN price_1m  p1  ON p1.rn  = 1 " +
                $"LEFT JOIN price_3m  p3  ON p3.rn  = 1 " +
                $"LEFT JOIN price_1y  p1y ON p1y.rn = 1 " +
                $"WHERE l.rn = 1";

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (0, 0, 0, 0);

            double.TryParse(reader["current"]?.ToString(), out var current);
            double.TryParse(reader["ret_1m"]?.ToString(), out var ret1m);
            double.TryParse(reader["ret_3m"]?.ToString(), out var ret3m);
            double.TryParse(reader["ret_1y"]?.ToString(), out var ret1y);
            return (current, ret1m, ret3m, ret1y);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }

    /// <summary>stock_cache 전체 지표 (rs DESC).</summary>
    public IEnumerable<StockCache> GetLatestIndicators()
        => Query<StockCache>(@"
            SELECT ticker, last_date, ret_1m, ret_3m, ret_6m,
                   rs, beta_60d, per, pbr, roe, updated_at
            FROM stock_cache ORDER BY rs DESC NULLS LAST");

    /// <summary>ticker → 대표 섹터명 맵 (v_stock_primary_sector 기반).</summary>
    public IDictionary<string, string> GetSectorMap()
    {
        var rows = Query<(string Ticker, string Sector)>(
            "SELECT ticker, sector FROM v_stock_primary_sector WHERE sector IS NOT NULL");
        return rows.ToDictionary(r => r.Ticker, r => r.Sector);
    }

    // ──────────────────────────────────────────────────────────
    /// <summary>stocks 마스터 upsert (market='KP', security_type='stock' 기본값).</summary>
    public void UpsertHistory(string ticker, string name)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO stocks (ticker, name, market, security_type, updated_at)
            VALUES ($1, $2, 'KP', 'stock', now())
            ON CONFLICT (ticker) DO UPDATE SET
                name = excluded.name, updated_at = now()";
        cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
        cmd.Parameters.Add(new DuckDBParameter { Value = name });
        cmd.ExecuteNonQuery();
    }

    /// <summary>stocks 마스터 전체 반환 (이력·마이그레이션 검증용).</summary>
    public IEnumerable<Stock> GetHistory()
        => Query<Stock>("SELECT * FROM stocks ORDER BY ticker");

    // ──────────────────────────────────────────────────────────
    /// <summary>data_update_log 기록.</summary>
    public void LogUpdate(string? ticker, DateOnly? date, string source, string status, string? errorMsg = null)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO data_update_log (ticker, date, source, status, error_msg, run_at)
            VALUES ($1, $2, $3, $4, $5, now())";
        cmd.Parameters.Add(new DuckDBParameter { Value = ticker   ?? (object)DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = date     is null ? DBNull.Value : date.Value.ToString("yyyy-MM-dd") });
        cmd.Parameters.Add(new DuckDBParameter { Value = source });
        cmd.Parameters.Add(new DuckDBParameter { Value = status });
        cmd.Parameters.Add(new DuckDBParameter { Value = errorMsg ?? (object)DBNull.Value });
        cmd.ExecuteNonQuery();
    }

    // ──────────────────────────────────────────────────────────
    //  Options helpers
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// options 테이블 전체를 읽어 AppOptions 객체로 반환.
    /// 테이블이 없거나 키가 없으면 기본값 사용.
    /// </summary>
    public AppOptions LoadOptions()
    {
        var opts = new AppOptions();
        try
        {
            var dt = Query("SELECT key, value FROM options");
            var map = dt.Rows.Cast<DataRow>()
                         .ToDictionary(
                             r => r[0]?.ToString() ?? "",
                             r => r[1]?.ToString() ?? "");

            if (map.TryGetValue("auto_append_history", out var v1))
                opts.AutoAppendHistory = v1 == "true";
            if (map.TryGetValue("report_pdf_folder", out var v2))
                opts.ReportPdfFolder = v2;
            if (map.TryGetValue("query_filter_preferred", out var v3))
                opts.QueryFilterPreferred = v3 == "true";
            if (map.TryGetValue("query_filter_covered", out var v4))
                opts.QueryFilterCovered = v4 == "true";
            if (map.TryGetValue("query_filter_cheap", out var v5))
                opts.QueryFilterCheap = v5 == "true";
            if (map.TryGetValue("exclude_spac", out var v6))
                opts.ExcludeSpac = v6 != "false";
            if (map.TryGetValue("exclude_pref_stock", out var v7))
                opts.ExcludePrefStock = v7 != "false";
        }
        catch { /* options 테이블 미생성 시 기본값 반환 */ }
        return opts;
    }

    /// <summary>
    /// AppOptions를 options 테이블에 upsert.
    /// </summary>
    public void SaveOptions(AppOptions opts)
    {
        UpsertOption("auto_append_history", opts.AutoAppendHistory ? "true" : "false");
        UpsertOption("report_pdf_folder",   opts.ReportPdfFolder ?? "");
        UpsertOption("query_filter_preferred", opts.QueryFilterPreferred ? "true" : "false");
        UpsertOption("query_filter_covered",   opts.QueryFilterCovered ? "true" : "false");
        UpsertOption("query_filter_cheap",     opts.QueryFilterCheap ? "true" : "false");
        UpsertOption("exclude_spac",           opts.ExcludeSpac ? "true" : "false");
        UpsertOption("exclude_pref_stock",     opts.ExcludePrefStock ? "true" : "false");
    }

    /// <summary>
    /// stocks 조회 쿼리에 삽입할 전역 제외 필터 절 반환.
    /// alias: SQL에서 stocks 테이블에 붙인 별칭 (기본 "s").
    /// 반환 예: "AND s.name NOT LIKE '%스팩%' AND RIGHT(s.ticker,1)='0'"
    /// 필터 없으면 빈 문자열 반환.
    /// </summary>
    public string BuildStockExcludeFilter(string alias = "s")
    {
        var opts = LoadOptions();
        var clauses = new List<string>();
        if (opts.ExcludeSpac)
            clauses.Add($"{alias}.name NOT LIKE '%스팩%'");
        if (opts.ExcludePrefStock)
            clauses.Add($"RIGHT({alias}.ticker, 1) = '0'");
        return clauses.Count > 0
            ? "AND " + string.Join(" AND ", clauses)
            : "";
    }

    private void UpsertOption(string key, string value)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO options (key, value, updated_at)
            VALUES ($1, $2, now())
            ON CONFLICT (key) DO UPDATE SET
                value      = excluded.value,
                updated_at = now()";
        cmd.Parameters.Add(new DuckDBParameter { Value = key });
        cmd.Parameters.Add(new DuckDBParameter { Value = value });
        cmd.ExecuteNonQuery();
    }

    // ──────────────────────────────────────────────────────────
    //  Private
    // ──────────────────────────────────────────────────────────

    private static string SanitizeIdentifier(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException($"유효하지 않은 식별자: '{id}'");
        return id;
    }
}
