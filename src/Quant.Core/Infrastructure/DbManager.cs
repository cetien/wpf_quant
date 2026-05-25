// Infrastructure/DbManager.cs
using Chaen;

using Dapper;

using DuckDB.NET.Data;

using Quant.Core.Models;

using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using static System.Runtime.InteropServices.JavaScript.JSType;

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
//      Execute(sql, param)             int                 Dapper 기반 DML (DuckDB 제약 주의)
//      Scalar<T>(sql)                  T?                  COUNT / MAX 단일 값
//      Query<T>(sql, param?)           IEnumerable<T>      타입 매핑(Repository용)
//      QueryFirst<T>(sql, param?)      T?                  단일 행 타입 매핑
//      IsConnected()                   bool                DB 파일 존재 + 연결 가능 여부
//      DbManager.DbPath                static string       경로 읽기 전용 노출
//      DbManager.Instance              static DbManager    DI 미사용 환경용 Singleton

//// ✅ DML — 네이티브 positional 사용
// using var conn = db.OpenNativeConnection();
// using var cmd = conn.CreateCommand();
// cmd.CommandText = "INSERT INTO t (a) VALUES ($1)";
// cmd.Parameters.Add(new DuckDBParameter { Value = val });

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

    private readonly object _optionsLock = new();
    private AppOptions? _optionsCache;
    private readonly object _stockCacheLock = new();
    private List<StockItem>? _stockItemCache;

    /// <summary>
    /// DI 컨테이너 없이 직접 접근하는 전역 인스턴스.
    /// DI를 사용한다면 이 프로퍼티 대신 생성자 주입을 사용할 것.
    /// </summary>
    private static DbManager? _instance;
    public static DbManager Instance => _instance ??= new DbManager();

    // ──────────────────────────────────────────────────────────
    //  생성자 — 초기화
    // ──────────────────────────────────────────────────────────

    public DbManager()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        RunMigrations();
        // EnsureStockCache()는 생성자에서 제거 — UI 스레드 블로킹 방지
        // 호출 책임: MainWindow.OnLoaded → Task.Run(() => db.EnsureStockCache())
    }

    // ──────────────────────────────────────────────────────────
    //  연결 생성 (내부 전용)
    // ──────────────────────────────────────────────────────────

    private IDbConnection OpenConnection()
    {
        var conn = new DuckDBConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }

    /// <summary>
    /// DuckDB 네이티브 파라미터($1,$2)가 필요한 DML용 연결 노출.
    /// 호출 측에서 반드시 using으로 닫을 것.
    /// </summary>
    public DuckDBConnection OpenNativeConnection() => (DuckDBConnection)OpenConnection();

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
        using var conn   = OpenConnection();
        using var cmd    = conn.CreateCommand();
        cmd.CommandText  = sql;

        using var reader = cmd.ExecuteReader();
        var dt = new DataTable();
        int fieldCount = reader.FieldCount;

        // 컬럼 타입을 DB 실제 타입으로 매핑 (숫자형 정렬 정확성을 위해)
        for (int i = 0; i < fieldCount; i++)
        {
            var type = reader.GetFieldType(i);

            // DBNull-safe: nullable 숫자도 double?/long?로 보존
            dt.Columns.Add(
                reader.GetName(i),
                type == typeof(float) ? typeof(double) : type); // float → double 통일
        }

        var values = new object[fieldCount];
        dt.BeginLoadData();

        while (reader.Read())
        {
            reader.GetValues(values);

            var row = dt.NewRow();
            for (int i = 0; i < fieldCount; i++)
            {
                row[i] = values[i] is float f ? (double)f : values[i];
            }
            dt.Rows.Add(row);
        }

        //while (reader.Read())
        //{
        //    var row = dt.NewRow();
        //    for (int i = 0; i < fieldCount; i++)
        //    {
        //        if (reader.IsDBNull(i)) { row[i] = DBNull.Value; continue; }
        //        var v = reader.GetValue(i);
        //        // float → double 통일 (DataGrid 정렬 일관성)
        //        row[i] = v is float f ? (double)f : v;
        //    }
        //    dt.Rows.Add(row);
        //}

        dt.EndLoadData();
        return dt;
    }

    public DataRow? QuerySingleRow(string sql)
    {
        var dt = Query(sql);
        return dt.Rows.Count > 0 ? dt.Rows[0] : null;
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
        //return (T)Convert.ChangeType(result, typeof(T));

        var targetType =
            Nullable.GetUnderlyingType(typeof(T))
            ?? typeof(T);

        return (T)Convert.ChangeType(result, targetType);
    }

    // ScalarDateOnly($"SELECT MAX({safeCol}) FROM {safe}")
    // var lastDate = db.ScalarDateOnly("SELECT MAX(date) FROM daily_prices")
    public DateOnly? ScalarDateOnly(string sql) => ChaenHelper.ParseDateOnly(Scalar<string>($"SELECT CAST(({sql}) AS VARCHAR)"));

    // var exists = Scalar<long>("SELECT COUNT(*) FROM stocks WHERE ticker = $1", "005930");
    public T? Scalar<T>(string sql, params object?[] values)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = sql;

        foreach (var value in values)
        {
            cmd.Parameters.Add(
                new DuckDBParameter
                {
                    Value = value ?? DBNull.Value
                });
        }

        var result = cmd.ExecuteScalar();

        if (result is null || result is DBNull)
            return default;

        var targetType =
            Nullable.GetUnderlyingType(typeof(T))
            ?? typeof(T);

        return (T)Convert.ChangeType(result, targetType);
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

    // Execute("DELETE FROM stocks WHERE ticker = $1", "005930");
    // Execute(@"UPDATE stocks SET name = $1 WHERE ticker = $2", "삼성전자", "005930");
    // Execute(@"INSERT INTO options(key, value) VALUES($1, $2)", "exclude_spac", "true");
    public int Execute(string sql, params object?[] values)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = sql;

        foreach (var value in values)
        {
            cmd.Parameters.Add(
                new DuckDBParameter { Value = value ?? DBNull.Value });
        }

        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Dapper 기반 실행. 파라미터 바인딩이 필요한 INSERT/UPDATE용.
    /// ⚠ DuckDB는 @Param 미지원. SELECT 전용 Dapper param만 사용할 것.
    /// DML에 파라미터가 필요하면 OpenConnection() + DuckDBParameter($1,$2) 사용.
    /// </summary>
    [Obsolete("DuckDB는 Dapper @Param DML 미지원. 네이티브 DuckDBParameter 사용 권장.")]
    public int ExecuteDapper(string sql, object param)
    {
        using var conn = OpenConnection();
        return conn.Execute(sql, param);
    }

    /// <summary>
    /// Dapper 기반 타입 매핑 쿼리. Repository·Service 계층용.
    /// ※ DuckDB는 @Param 바인딩 미지원 — 파라미터가 필요한 DML은 네이티브 cmd 사용.
    /// </summary>
    public List<T> Query<T>(string sql, object? param = null)
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
    public long RowCount(string table, string where)
    {
        var safe = SanitizeIdentifier(table);
        return Scalar<long>($"SELECT COUNT(*) FROM {safe} WHERE {where}");
    }
    public bool Exists(string table, string where)
    {
        var safe = SanitizeIdentifier(table);
        return Scalar<bool>($"SELECT EXISTS(SELECT 1 FROM {safe} WHERE {where})");
    }
    public long DistinctCount(string table, string column)
    {
        var safe = SanitizeIdentifier(table);
        var safeCol = SanitizeIdentifier(column);
        return Scalar<long>($"SELECT COUNT(DISTINCT {safeCol}) FROM {safe}");
    }

    //public bool Exists(string table, string where) => RowCount(table, where) > 0;
    //public bool ExistTicker(string table, string ticker) => RowCount(table, $"ticker = '{ticker}'") > 0;

    //public long DistinctCount(string table, string column)
    //{
    //    var safe = SanitizeIdentifier(table);
    //    var safeCol = SanitizeIdentifier(column);
    //    return Scalar<long>($"SELECT COUNT(DISTINCT {safeCol}) FROM {safe}");
    //}


    /// <summary>
    /// 지정 테이블의 date 컬럼 최신 날짜.
    /// ticker 지정 시 해당 ticker 기준, null이면 전체 기준.
    /// </summary>
    //public DateOnly? GetLastDate(string table, string dateCol = "date", string? ticker = null)
    //{
    //    var safe    = SanitizeIdentifier(table);
    //    var safeCol = SanitizeIdentifier(dateCol);

    //    if (ticker is null)
    //    {
    //        var raw = Scalar<string>($"SELECT MAX({safeCol}) FROM {safe}");
    //        return raw is not null && DateOnly.TryParse(raw, out var d) ? d : null;
    //    }

    //    using var conn = OpenConnection();
    //    using var cmd  = conn.CreateCommand();
    //    cmd.CommandText = $"SELECT MAX({safeCol}) FROM {safe} WHERE ticker = $1";
    //    cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
    //    var result = cmd.ExecuteScalar();
    //    if (result is null || result is DBNull) return null;
    //    return DateOnly.TryParse(result.ToString(), out var date) ? date : null;
    //}

    // db.MaxDate("daily_prices", where: "ticker='005930'");
    public DateOnly? MaxDate(string table, string dateCol = "date", string? where = null)
    {
        var safeTable = SanitizeIdentifier(table);
        var safeCol = SanitizeIdentifier(dateCol);

        string sql = $"SELECT CAST(MAX({safeCol}) AS VARCHAR) FROM {safeTable}";

        if (!string.IsNullOrWhiteSpace(where))
            sql += $" WHERE {where}";

        //string? raw =
        //    ticker is null
        //    ? Scalar<string>(
        //        $"SELECT CAST(MAX({safeCol}) AS VARCHAR) FROM {safe}")
        //    : Scalar<string>(
        //        $"SELECT CAST(MAX({safeCol}) AS VARCHAR) FROM {safe} WHERE ticker = $1",
        //        new DuckDBParameter { Value = ticker });

        return ChaenHelper.ParseDateOnly(Scalar<string>(sql));
    }


    // ──────────────────────────────────────────────────────────
    // Quant-specific helpers
    // ──────────────────────────────────────────────────────────
    public bool ExistTicker(string table, string ticker) => Exists(table, where: $"ticker='{ticker}'");
    public DateOnly? MaxDateForTicker(string table, string ticker) => MaxDate(table, where: $"ticker='{ticker}'");
    public string SecurityTypeForTicker(string ticker) => Scalar<string>($"SELECT security_type FROM stocks WHERE ticker = '{ticker}'") ?? "stock";
    public string MarketForTicker(string ticker) => Scalar<string>($"SELECT market FROM stocks WHERE ticker = '{ticker}'") ?? "KP";

    /// <summary>
    /// KRX 종목 여부 판별.
    /// 1순위: DB stocks.market = 'KP' 또는 'KQ'
    /// 2순위(미등록): 코드 패턴
    ///   - \d{6}       → 일반 KRX (069500)
    ///   - \d{4}[A-Z]\d → 신규 ETF (0091P0, 2023년 이후)
    /// </summary>
    public bool IsKrxTicker(string ticker)
    {
        var market = MarketForTicker(ticker);
        if (market == "KP" || market == "KQ")
            return true;

        return System.Text.RegularExpressions.Regex.IsMatch(ticker, @"^\d{6}$") ||
               System.Text.RegularExpressions.Regex.IsMatch(ticker, @"^\d{4}[A-Z]\d$");
    }

    /// <summary>stocks 마스터 전체 반환 (이력·마이그레이션 검증용).</summary>
    /// Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    public List<Stock> StockList() => Query<Stock>("SELECT * FROM stocks ORDER BY ticker");
    public List<StockGroup> GroupList() => Query<StockGroup>("SELECT * FROM groups ORDER BY name");
    public List<StockGroupMap> GroupMapList() => Query<StockGroupMap>("SELECT * FROM stock_group_map ORDER BY ticker");

    // for StockPickerControl
    /*
        _allStocks
            .Where(x =>
                x.Name.Contains(keyword,
                    StringComparison.OrdinalIgnoreCase)
                ||
                x.Ticker.StartsWith(keyword))
            .Take(30)    
    */
    public IReadOnlyList<StockItem> StockItemList()
    {
        lock (_stockCacheLock)
        {
            if (_stockItemCache == null)
            {
                _stockItemCache = Query<StockItem>("SELECT ticker, name FROM stocks ORDER BY ticker");
            }
            // 외부에서 목록을 수정하더라도 캐시가 오염되지 않도록 새 리스트로 복사하여 반환합니다.
            return new List<StockItem>(_stockItemCache);
        }
    }

    //public string Name2Ticker(string name) => Scalar<string>($"SELECT ticker FROM stocks WHERE name = '{name}'") ?? "";
    public string Name2Ticker(string name) => StockItemList().FirstOrDefault(s => s.Name == name)?.Ticker ?? "";

    // ──────────────────────────────────────────────────────────
    /// <summary>data_update_log 기록.</summary>
    // ──────────────────────────────────────────────────────────
    public void LogUpdate(string? ticker, DateOnly? date, string source, string status, string? errorMsg = null)
    {
        Execute(@"
            INSERT INTO data_update_log (ticker, date, source, status, error_msg, run_at)
            VALUES ($1, $2, $3, $4, $5, now())",
            ticker ?? (object)DBNull.Value,
            date is null ? DBNull.Value : date.Value.ToString("yyyy-MM-dd"),
            source,
            status,
            errorMsg ?? (object)DBNull.Value);

        //using var conn = OpenConnection();
        //using var cmd = conn.CreateCommand();
        //cmd.CommandText = @"
        //    INSERT INTO data_update_log (ticker, date, source, status, error_msg, run_at)
        //    VALUES ($1, $2, $3, $4, $5, now())";
        //cmd.Parameters.Add(new DuckDBParameter { Value = ticker ?? (object)DBNull.Value });
        //cmd.Parameters.Add(new DuckDBParameter { Value = date is null ? DBNull.Value : date.Value.ToString("yyyy-MM-dd") });
        //cmd.Parameters.Add(new DuckDBParameter { Value = source });
        //cmd.Parameters.Add(new DuckDBParameter { Value = status });
        //cmd.Parameters.Add(new DuckDBParameter { Value = errorMsg ?? (object)DBNull.Value });
        //cmd.ExecuteNonQuery();
    }

    public void LogUpdate(string? ticker, DateOnly? date, string source, Exception ex) => LogUpdate(ticker, date, source, "Exception", ex.Message);

    // ──────────────────────────────────────────────────────────
    //  StockCache
    // ──────────────────────────────────────────────────────────
    //-- rebuild: staleness check
    //--      SELECT MAX(date) FROM daily_prices
    //--      SELECT MAX(asof_date) FROM stock_cache
    //--      if cache_date<daily_price_date: full_rebuild(CREATE OR REPLACE TABLE stock_cache AS ...)
    //-- 참고: current_data <=== SELECT MAX(date) FROM daily_prices WHERE ticker =?
    public void EnsureStockCache()
    {
        if (!TableExists("stock_cache"))
            return;

        var latestPriceDate = MaxDate("daily_prices");
        if (latestPriceDate is null)
            return;

        var cacheDate = MaxDate("stock_cache", dateCol: "asof_date");
        if (cacheDate is null || cacheDate < latestPriceDate)
        {
            try
            {
                RebuildStockCache();
            }
            catch (Exception ex)
            {
                LogUpdate(null, latestPriceDate, "stock_cache", ex);
            }
        }
    }

    public void RebuildStockCache()
    {
        using var conn = OpenNativeConnection();
        using var trans = conn.BeginTransaction(); // 원자성 보장을 위해 트랜잭션 사용
        try
        {
            // 1. 기존 캐시 삭제
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM stock_cache";
                cmd.ExecuteNonQuery();
            }

            // 2. 대량 인서트 (DuckDB의 성능을 위해 복잡한 연산을 SQL 하나로 처리)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO stock_cache (
    ticker,
    asof_date,
    current_price,
    ret_1m,
    ret_3m,
    ret_6m,
    ret_1y,
    rs,
    eps,
    per,
    pbr,
    roe,
    atr_14,
    atr_percent,
    high_52w,
    low_52w,
    volume_avg_20d,
    distance_from_high,
    ma20,
    ma60,
    ma120,
    volume_ratio,
    high_60d,
    high_120d,
    report_count,
    target_price
)
WITH base AS (
    SELECT
        dp.ticker,
        dp.date,
        dp.open,
        dp.high,
        dp.low,
        dp.close,
        dp.adj_close,
        dp.volume,

        ROW_NUMBER() OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date DESC
        ) AS rn,

        LAG(dp.adj_close) OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date
        ) AS prev_close,

        MAX(dp.high) OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date
            ROWS BETWEEN 251 PRECEDING AND CURRENT ROW
        ) AS high_52w,

        MIN(dp.low) OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date
            ROWS BETWEEN 251 PRECEDING AND CURRENT ROW
        ) AS low_52w,

        AVG(dp.volume) OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date
            ROWS BETWEEN 19 PRECEDING AND CURRENT ROW
        ) AS volume_avg_20d,

        AVG(dp.adj_close) OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date
            ROWS BETWEEN 19 PRECEDING AND CURRENT ROW
        ) AS ma20,

        AVG(dp.adj_close) OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date
            ROWS BETWEEN 59 PRECEDING AND CURRENT ROW
        ) AS ma60,

        AVG(dp.adj_close) OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date
            ROWS BETWEEN 119 PRECEDING AND CURRENT ROW
        ) AS ma120,

        MAX(dp.high) OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date
            ROWS BETWEEN 59 PRECEDING AND CURRENT ROW
        ) AS high_60d,

        MAX(dp.high) OVER (
            PARTITION BY dp.ticker
            ORDER BY dp.date
            ROWS BETWEEN 119 PRECEDING AND CURRENT ROW
        ) AS high_120d

    FROM daily_prices dp
    WHERE dp.date >= CURRENT_DATE - INTERVAL 400 DAY
),

latest AS (
    SELECT *
    FROM base
    WHERE rn = 1
),

returns AS (
    SELECT
        l.ticker,
        l.date AS asof_date,
        l.adj_close AS current_price,

        ROUND(
            (
                l.adj_close /
                (
                    SELECT p.adj_close
                    FROM daily_prices p
                    WHERE p.ticker = l.ticker
                      AND p.date <= l.date - INTERVAL 1 MONTH
                    ORDER BY p.date DESC
                    LIMIT 1
                ) - 1
            ) * 100,
            2
        ) AS ret_1m,

        ROUND(
            (
                l.adj_close /
                (
                    SELECT p.adj_close
                    FROM daily_prices p
                    WHERE p.ticker = l.ticker
                      AND p.date <= l.date - INTERVAL 3 MONTH
                    ORDER BY p.date DESC
                    LIMIT 1
                ) - 1
            ) * 100,
            2
        ) AS ret_3m,

        ROUND(
            (
                l.adj_close /
                (
                    SELECT p.adj_close
                    FROM daily_prices p
                    WHERE p.ticker = l.ticker
                      AND p.date <= l.date - INTERVAL 6 MONTH
                    ORDER BY p.date DESC
                    LIMIT 1
                ) - 1
            ) * 100,
            2
        ) AS ret_6m,

        ROUND(
            (
                l.adj_close /
                (
                    SELECT p.adj_close
                    FROM daily_prices p
                    WHERE p.ticker = l.ticker
                      AND p.date <= l.date - INTERVAL 1 YEAR
                    ORDER BY p.date DESC
                    LIMIT 1
                ) - 1
            ) * 100,
            2
        ) AS ret_1y,

        l.date AS latest_date   -- kospi_ret JOIN 키

    FROM latest l
),

-- KOSPI 수익률: 종목별 latest_date 기준으로 JOIN
-- correlated subquery 대신 도우 테이블 방식으로 회피
kospi_current AS (
    SELECT date, adj_close
    FROM daily_prices
    WHERE ticker = 'IDX_KOSPI'
),

kospi_ret AS (
    SELECT
        r.ticker,
        ROUND(
            (
                kc.adj_close
                / NULLIF(kp.adj_close, 0)
                - 1
            ) * 100,
            2
        ) AS kospi_ret_3m
    FROM returns r
    -- 종목 latest_date 이하 작은 가장 가까운 KOSPI 종가
    JOIN kospi_current kc
        ON kc.date = (
            SELECT MAX(date) FROM kospi_current
            WHERE date <= r.latest_date
        )
    -- latest_date - 3개월 이하 가장 가까운 KOSPI 종가
    JOIN kospi_current kp
        ON kp.date = (
            SELECT MAX(date) FROM kospi_current
            WHERE date <= r.latest_date - INTERVAL 3 MONTH
        )
),

atr_calc AS (
    SELECT
        b.ticker,
        b.date,

        GREATEST(
            b.high - b.low,
            ABS(b.high - b.prev_close),
            ABS(b.low  - b.prev_close)
        ) AS tr

    FROM base b
),

atr_latest AS (
    SELECT
        ticker,
        AVG(tr) AS atr_14
    FROM (
        SELECT
            ticker,
            tr,
            ROW_NUMBER() OVER (
                PARTITION BY ticker
                ORDER BY date DESC
            ) AS rn
        FROM atr_calc
    )
    WHERE rn <= 14
    GROUP BY ticker
),

latest_fundamentals AS (
    SELECT *
    FROM (
        SELECT
            f.*,
            ROW_NUMBER() OVER (
                PARTITION BY f.ticker
                ORDER BY f.report_date DESC
            ) AS rn
        FROM fundamentals f
    )
    WHERE rn = 1
),
report_counts AS (
    -- pdf_reports.ticker = 종목코드(code) 기준 — stocks JOIN 불필요
    SELECT p.ticker, COUNT(*) AS report_count
    FROM pdf_reports p
    WHERE p.ticker IS NOT NULL
    GROUP BY p.ticker
),
latest_tp AS (
    SELECT ticker, target_price
    FROM (
        SELECT p.ticker, p.target_price,
               ROW_NUMBER() OVER (PARTITION BY p.ticker ORDER BY p.date DESC) AS rn
        FROM pdf_reports p
        WHERE p.target_price IS NOT NULL AND p.target_price > 0
          AND p.ticker IS NOT NULL
    )
    WHERE rn = 1
)

SELECT
    r.ticker,
    r.asof_date,
    r.current_price,

    r.ret_1m,
    r.ret_3m,
    r.ret_6m,
    r.ret_1y,

    -- RS = 종목 3개월 수익률 - KOSPI 3개월 수익률 (동일 l.date 기준)
    ROUND(r.ret_3m - COALESCE(k.kospi_ret_3m, 0), 2) AS rs,

    lf.eps,
    lf.per,
    lf.pbr,
    lf.roe,

    a.atr_14,
    ROUND(
        (COALESCE(a.atr_14, 0) / NULLIF(r.current_price, 0)) * 100, 2
    ) AS atr_percent,

    l.high_52w,
    l.low_52w,

    l.volume_avg_20d,

    ROUND(

        (
            r.current_price / l.high_52w - 1
        ) * 100,
        2
    ) AS distance_from_high,

    ROUND(l.ma20,   2) AS ma20,
    ROUND(l.ma60,   2) AS ma60,
    ROUND(l.ma120,  2) AS ma120,
    ROUND(
        l.volume / NULLIF(l.volume_avg_20d, 0),
        4
    ) AS volume_ratio,
    l.high_60d,
    l.high_120d,
    COALESCE(rc.report_count, 0) AS report_count,
    lt.target_price

FROM returns r
JOIN latest l
    ON r.ticker = l.ticker

LEFT JOIN kospi_ret k
    ON r.ticker = k.ticker

LEFT JOIN atr_latest a
    ON r.ticker = a.ticker

LEFT JOIN latest_fundamentals lf
    ON r.ticker = lf.ticker

LEFT JOIN report_counts rc
    ON r.ticker = rc.ticker

LEFT JOIN latest_tp lt
    ON r.ticker = lt.ticker
";
                cmd.ExecuteNonQuery();
            }
            trans.Commit();
        }
        catch { trans.Rollback(); throw; }
    }
    /*
몇 가지 설명:

latest_fundamentals
    최신 report_date 기준
    실제론 announce_date 기준 join이 더 정확할 수 있음
WHERE dp.date >= CURRENT_DATE - INTERVAL 400 DAY
    full scan 방지용
subquery 방식
    DuckDB에서 surprisingly 잘 동작함
    readability도 좋음

추가로 이후 고려할 수 있는 것:
    market_cap
    52w breakout flag
    volume_ratio
    momentum score
    earnings growth
    rolling volatility
    sharpe-like metrics


*** 필수 ***
1. RS(상대강도) 고도화 (필수): 현재 ret_3m을 그대로 사용 중이나,
    실제 RS는 (종목 수익률 / 지수 수익률) * 100 또는 특정 기간의 지수 대비 초과 수익률로 계산해야 변별력이 생깁니다.

2. ATR의 단위 문제: 절대값 ATR(atr_14)은 주가가 높은 종목(예: 삼성전자)과 낮은 종목(예: 동전주) 간 비교가 불가능합니다.
    주가 대비 비율인 %ATR(atr_14 / current_price * 100)을 추가로 저장하는 것이 실무적으로 유용합니다.


RS(Relative Strength) 정밀화 로직 제안 (Pre-Analysis)
    시장 지수(KOSPI) 대비 상대강도를 계산하기 위한 논리적 흐름입니다.

단계                  계산 로직 (Calculation Logic)                                           기대 효과
1. 지수 수익률       SELECT ret_3m FROM returns WHERE ticker = '0001' (KOSPI 예시)           시장 기준점 확보
2. 상대 수익률       (종목 ret_3m - 시장 ret_3m)                                             단순 초과 수익률 산출
3. RS Score         (종목 ret_3m / 시장 ret_3m) * 100                                       시장 대비 강도 지수화



[Refinement]: SQL 쿼리 내 RS 고도화 예시
returns CTE 이후에 시장 지수 수익률을 가져와 연산하는 부분을 보완할 수 있습니다

-- returns CTE 내부 또는 이후에 추가
market_return AS (
    SELECT ret_3m 
    FROM returns 
    WHERE ticker = '0001' -- KOSPI 지수 티커 가정
    LIMIT 1
),
final_select AS (
    SELECT 
        r.*,
        ROUND(r.ret_3m - COALESCE((SELECT ret_3m FROM market_return), 0), 2) AS rs_alpha
    FROM returns r
)


    */

    public StockCache? GetStockCache(string ticker)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.*, s.name, s.rating, s.market, s.security_type, s.is_active
            FROM stock_cache c
            JOIN stocks s ON c.ticker = s.ticker
            WHERE c.ticker = $1";
        cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        static double Safe(object v) => v is DBNull || v is null ? 0.0 : Convert.ToDouble(v);

        return new StockCache
        {
            Ticker = reader["ticker"]?.ToString() ?? "",
            Name = reader["name"]?.ToString() ?? "",
            SecurityType = reader["security_type"]?.ToString() ?? "",
            Market = reader["market"]?.ToString() ?? "",
            Rating = (int)Safe(reader["rating"]),
            IsActive = reader["is_active"] is not DBNull && Convert.ToBoolean(reader["is_active"]),
            AsofDate = DateOnly.TryParse(reader["asof_date"]?.ToString(), out var d) ? d : default,
            CurrentPrice = Safe(reader["current_price"]),
            Ret1M = Safe(reader["ret_1m"]),
            Ret3M = Safe(reader["ret_3m"]),
            Ret6M = Safe(reader["ret_6m"]),
            Ret1Y = Safe(reader["ret_1y"]),
            Rs = Safe(reader["rs"]),
            Per = Safe(reader["per"]),        // LEFT JOIN → nullable
            Pbr = Safe(reader["pbr"]),        // LEFT JOIN → nullable
            Roe = Safe(reader["roe"]),        // LEFT JOIN → nullable
            Eps = Safe(reader["eps"]),        // LEFT JOIN → nullable
            Atr14 = Safe(reader["atr_14"]),     // LEFT JOIN → nullable
            AtrPercent = Safe(reader["atr_percent"]),
            High52W = Safe(reader["high_52w"]),
            Low52W = Safe(reader["low_52w"]),
            VolumeAvg20D = Safe(reader["volume_avg_20d"]),
            DistanceFromHigh = Safe(reader["distance_from_high"]),
            Ma20 = Safe(reader["ma20"]),
            Ma60 = Safe(reader["ma60"]),
            Ma120 = Safe(reader["ma120"]),
            VolumeRatio = Safe(reader["volume_ratio"]),
            High60D = Safe(reader["high_60d"]),
            High120D = Safe(reader["high_120d"]),
        };
    }
    public Stock? StockInfo(string ticker) => GetStockCache(ticker);
    //public string StockName(string ticker) => StockInfo(ticker)?.Name ?? "";

    // ──────────────────────────────────────────────────────────
    //  Data helpers
    // ──────────────────────────────────────────────────────────
    // <summary>ticker → 종목명. 없으면 ticker 반환.</summary>
    //public (string name, int rating, string market, bool isActive) GetStockInfo(string ticker)
    //{
    //    using var conn = OpenConnection();
    //    using var cmd  = conn.CreateCommand();
    //    cmd.CommandText = "SELECT name, rating, market, is_active FROM stocks WHERE ticker = $1";
    //    cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
    //    using var reader = cmd.ExecuteReader();
    //    if (!reader.Read()) return (ticker, 0, "", false);

    //    var name = reader.IsDBNull(0) ? ticker : reader.GetValue(0)?.ToString() ?? ticker;
    //    var rating = reader.IsDBNull(1) ? 0 : Math.Clamp(Convert.ToInt32(reader.GetValue(1)), 0, 10);
    //    var market = reader.IsDBNull(2) ? "" : reader.GetValue(2)?.ToString() ?? "";
    //    var isActive = !reader.IsDBNull(3) && Convert.ToBoolean(reader.GetValue(3));
    //    return (name, rating, market, isActive);
    //}
    //public string GetStockName(string ticker) => GetStockInfo(ticker).name;

    // ──────────────────────────────────────────────────────────
    //  Set Rating helpers
    // ──────────────────────────────────────────────────────────
    public bool SetStockRating(int rating, string ticker) => SetRating("stocks", "ticker", ticker, rating);
    public bool SetGroupRating(int rating, int group_id) => SetRating("groups", "group_id", group_id.ToString(), rating);
    private bool SetRating(string table, string key, string id, int rating)
    {
        try
        {
            var safeTable = SanitizeIdentifier(table);
            var safeKey = SanitizeIdentifier(key);
            using var conn = OpenConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {safeTable} SET rating=$1, updated_at=CURRENT_TIMESTAMP WHERE {safeKey} = $2";
            cmd.Parameters.Add(new DuckDBParameter { Value = rating });
            cmd.Parameters.Add(new DuckDBParameter { Value = id });
            cmd.ExecuteNonQuery();
            return true;
        }
        catch { return false; }
    }

    //public (double current, double ret1m, double ret3m, double ret6m, double ret1y) LoadPrice_(string ticker)
    //{
    //    try
    //    {
    //        using var conn = OpenConnection();
    //        using var cmd = conn.CreateCommand();
    //        // ticker를 리터럴로 삽입: DuckDB positional $1 다중 CTE 재사용 불안정 방어
    //        // ticker는 6자리 숫자 코드 또는 IDX_ 접두사 — SQL 인젝션 위험 없음
    //        var t = ticker.Replace("'", "''"); // 방어적 escape
    //        cmd.CommandText =
    //            $"WITH latest AS ( " +
    //            $"  SELECT adj_close, ROW_NUMBER() OVER (ORDER BY date DESC) AS rn " +
    //            $"  FROM daily_prices WHERE ticker = '{t}' " +
    //            $"), " +
    //            $"price_1m AS ( " +
    //            $"  SELECT adj_close, " +
    //            $"    ROW_NUMBER() OVER (ORDER BY ABS(DATEDIFF('day', date, CURRENT_DATE - INTERVAL 1 MONTH))) AS rn " +
    //            $"  FROM daily_prices " +
    //            $"  WHERE ticker = '{t}' " +
    //            $"    AND date BETWEEN CURRENT_DATE - INTERVAL 1 MONTH - INTERVAL 7 DAY " +
    //            $"                 AND CURRENT_DATE - INTERVAL 1 MONTH + INTERVAL 7 DAY " +
    //            $"), " +
    //            $"price_3m AS ( " +
    //            $"  SELECT adj_close, " +
    //            $"    ROW_NUMBER() OVER (ORDER BY ABS(DATEDIFF('day', date, CURRENT_DATE - INTERVAL 3 MONTH))) AS rn " +
    //            $"  FROM daily_prices " +
    //            $"  WHERE ticker = '{t}' " +
    //            $"    AND date BETWEEN CURRENT_DATE - INTERVAL 3 MONTH - INTERVAL 7 DAY " +
    //            $"                 AND CURRENT_DATE - INTERVAL 3 MONTH + INTERVAL 7 DAY " +
    //            $"), " +
    //            $"price_6m AS ( " +
    //            $"  SELECT adj_close, " +
    //            $"    ROW_NUMBER() OVER (ORDER BY ABS(DATEDIFF('day', date, CURRENT_DATE - INTERVAL 6 MONTH))) AS rn " +
    //            $"  FROM daily_prices " +
    //            $"  WHERE ticker = '{t}' " +
    //            $"    AND date BETWEEN CURRENT_DATE - INTERVAL 6 MONTH - INTERVAL 7 DAY " +
    //            $"                 AND CURRENT_DATE - INTERVAL 6 MONTH + INTERVAL 7 DAY " +
    //            $"), " +
    //            $"price_1y AS ( " +
    //            $"  SELECT adj_close, " +
    //            $"    ROW_NUMBER() OVER (ORDER BY ABS(DATEDIFF('day', date, CURRENT_DATE - INTERVAL 1 YEAR))) AS rn " +
    //            $"  FROM daily_prices " +
    //            $"  WHERE ticker = '{t}' " +
    //            $"    AND date BETWEEN CURRENT_DATE - INTERVAL 1 YEAR - INTERVAL 7 DAY " +
    //            $"                 AND CURRENT_DATE - INTERVAL 1 YEAR + INTERVAL 7 DAY " +
    //            $") " +
    //            $"SELECT " +
    //            $"  l.adj_close AS current, " +
    //            $"  ROUND((l.adj_close - p1.adj_close)  / p1.adj_close  * 100, 2) AS ret_1m, " +
    //            $"  ROUND((l.adj_close - p3.adj_close)  / p3.adj_close  * 100, 2) AS ret_3m, " +
    //            $"  ROUND((l.adj_close - p6.adj_close)  / p6.adj_close  * 100, 2) AS ret_6m, " +
    //            $"  ROUND((l.adj_close - p1y.adj_close) / p1y.adj_close * 100, 2) AS ret_1y " +
    //            $"FROM latest l " +
    //            $"LEFT JOIN price_1m  p1  ON p1.rn  = 1 " +
    //            $"LEFT JOIN price_3m  p3  ON p3.rn  = 1 " +
    //            $"LEFT JOIN price_6m  p6  ON p6.rn  = 1 " +
    //            $"LEFT JOIN price_1y  p1y ON p1y.rn = 1 " +
    //            $"WHERE l.rn = 1";

    //        using var reader = cmd.ExecuteReader();
    //        if (!reader.Read()) return (0, 0, 0, 0, 0);

    //        double.TryParse(reader["current"]?.ToString(), out var current);
    //        double.TryParse(reader["ret_1m"]?.ToString(),  out var ret1m);
    //        double.TryParse(reader["ret_3m"]?.ToString(),  out var ret3m);
    //        double.TryParse(reader["ret_6m"]?.ToString(),  out var ret6m);
    //        double.TryParse(reader["ret_1y"]?.ToString(),  out var ret1y);
    //        return (current, ret1m, ret3m, ret6m, ret1y);
    //    }
    //    catch
    //    {
    //        return (0, 0, 0, 0, 0);
    //    }
    //}

    /// <summary>stock_cache 전체 지표 (rs DESC).</summary>
    //public IEnumerable<StockCache> GetLatestIndicators()
    //    => Query<StockCache>(@"
    //        SELECT ticker, last_date, ret_1m, ret_3m, ret_6m,
    //               rs, beta_60d, per, pbr, roe, updated_at
    //        FROM stock_cache ORDER BY rs DESC NULLS LAST");

    ///// <summary>ticker → 대표 섹터명 맵 (v_stock_primary_sector 기반).</summary>
    //public IDictionary<string, string> GetSectorMap()
    //{
    //    var rows = Query<(string Ticker, string Sector)>(
    //        "SELECT ticker, sector FROM v_stock_primary_sector WHERE sector IS NOT NULL");
    //    return rows.ToDictionary(r => r.Ticker, r => r.Sector);
    //}
    ///


    // ──────────────────────────────────────────────────────────
    //  Upsert Stock
    // ──────────────────────────────────────────────────────────
    /// <summary>stocks 마스터 upsert (market='KP', security_type='stock' 기본값).</summary>
    public void UpsertStock(string ticker, string name)
    {
        //using var conn = OpenConnection();
        //using var cmd  = conn.CreateCommand();
        //cmd.CommandText = @"
        //    INSERT INTO stocks (ticker, name, market, security_type, updated_at)
        //    VALUES ($1, $2, 'KP', 'stock', now())
        //    ON CONFLICT (ticker) DO UPDATE SET
        //        name = excluded.name, updated_at = now()";
        //cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
        //cmd.Parameters.Add(new DuckDBParameter { Value = name });
        //cmd.ExecuteNonQuery();

        var stock = new Stock
        {
            Ticker = ticker,
            Name = name,
            Market = "KP",
            SecurityType = "stock",
            UpdatedAt = DateTime.Now
        };

        UpsertStock(stock);
    }

    public void UpsertStock(Stock stock)
    {
        lock (_stockCacheLock) _stockItemCache = null;
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
        INSERT INTO stocks
        (
            ticker,
            name,
            market,
            security_type,
            listed_date,
            rating,
            is_active,
            updated_at
        )
        VALUES
        (
            $1,$2,$3,$4,$5,$6,$7,$8
        )
        ON CONFLICT (ticker)
        DO UPDATE SET
            name          = excluded.name,
            market        = excluded.market,
            security_type = excluded.security_type,
            listed_date   = excluded.listed_date,
            rating        = excluded.rating,
            is_active     = excluded.is_active,
            updated_at    = excluded.updated_at";

        cmd.Parameters.Add(new DuckDBParameter { Value = stock.Ticker });
        cmd.Parameters.Add(new DuckDBParameter { Value = stock.Name });
        cmd.Parameters.Add(new DuckDBParameter { Value = stock.Market });
        cmd.Parameters.Add(new DuckDBParameter { Value = stock.SecurityType });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)stock.ListedDate ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = stock.Rating });
        cmd.Parameters.Add(new DuckDBParameter { Value = stock.IsActive });
        cmd.Parameters.Add(new DuckDBParameter { Value = stock.UpdatedAt });

        cmd.ExecuteNonQuery();
    }
    /// <summary>
    /// stocks 테이블 upsert (전체 컬럼 지정 버전).
    /// market: 'KP' | 'KQ' | 'NYSE' 등
    /// securityType: 'stock' | 'ETF' | 'index'
    /// </summary>
    //public void UpsertStock(string ticker, string name, string market,
    //                        string securityType, string? sector, bool isEtf)
    //{
    //    using var conn = OpenNativeConnection();
    //    using var cmd = conn.CreateCommand();
    //    // sector 컬럼이 없으면 무시 (스키마에 따라 조정)
    //    cmd.CommandText = @"
    //        INSERT INTO stocks (ticker, name, market, security_type, is_active, updated_at)
    //        VALUES ($1, $2, $3, $4, TRUE, now())
    //        ON CONFLICT (ticker) DO UPDATE SET
    //            name          = excluded.name,
    //            market        = excluded.market,
    //            security_type = excluded.security_type,
    //            is_active     = TRUE,
    //            updated_at    = now()";
    //    cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
    //    cmd.Parameters.Add(new DuckDBParameter { Value = name });
    //    cmd.Parameters.Add(new DuckDBParameter { Value = market });
    //    cmd.Parameters.Add(new DuckDBParameter { Value = securityType });
    //    cmd.ExecuteNonQuery();
    //}

    // ──────────────────────────────────────────────────────────
    //  Options helpers
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 캐시된 옵션을 초기화하여 다음 호출 시 DB에서 새로 읽도록 한다.
    /// </summary>
    public void InvalidateOptions()
    {
        lock (_optionsLock) { _optionsCache = null; }
    }

    /// <summary>
    /// options 테이블 전체를 읽어 AppOptions 객체로 반환.
    /// 테이블이 없거나 키가 없으면 기본값 사용.
    /// </summary>
    public AppOptions Options()
    {
        Dictionary<string, string> LoadOptionDictionary() => Query<(string Key, string Value)>(
            "SELECT key, value FROM options")
            .ToDictionary(x => x.Key, x => x.Value);

        lock (_optionsLock)
        {
            if (_optionsCache == null)
                _optionsCache = AppOptions.FromDictionary(LoadOptionDictionary());
        }
        return _optionsCache;
    }
    //var opts = new AppOptions();
    //_optionsCache = opts;

    //try
    //{
    //    var dt = Query("SELECT key, value FROM options");
    //    var map = dt.Rows.Cast<DataRow>()
    //                 .ToDictionary(
    //                     r => r[0]?.ToString() ?? "",
    //                     r => r[1]?.ToString() ?? "");

    //    if (map.TryGetValue("auto_append_history", out var v1))
    //        opts.AutoAppendHistory = v1 == "true";
    //    if (map.TryGetValue("report_pdf_folder", out var v2))
    //        opts.ReportPdfFolder = v2;
    //    if (map.TryGetValue("query_filter_preferred", out var v3))
    //        opts.QueryFilterPreferred = v3 == "true";
    //    if (map.TryGetValue("query_filter_covered", out var v4))
    //        opts.QueryFilterCovered = v4 == "true";
    //    if (map.TryGetValue("query_filter_cheap", out var v5))
    //        opts.QueryFilterCheap = v5 == "true";
    //    if (map.TryGetValue("exclude_spac", out var v6))
    //        opts.ExcludeSpac = v6 != "false";
    //    if (map.TryGetValue("exclude_pref_stock", out var v7))
    //        opts.ExcludePrefStock = v7 != "false";
    //    if (map.TryGetValue("exclude_halted", out var v8))
    //        opts.ExcludeHalted = v8 != "false";
    //}
    //catch { /* options 테이블 미생성 시 기본값 반환 */
    //}
    //return opts;
    //    }
    //}

    /// <summary>
    /// AppOptions를 options 테이블에 upsert.
    /// </summary>
    
    private static string _cmd_InsertOption = @"
        INSERT INTO options (key, value, updated_at)
                VALUES ($1, $2, now())
                ON CONFLICT (key) DO UPDATE SET
                    value      = excluded.value,
                    updated_at = now()";
    public void SaveOptions(AppOptions opts)
    {
        //lock (_optionsLock)
        //    _optionsCache = null;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = _cmd_InsertOption;

        var pKey = new DuckDBParameter("key", "");
        var pValue = new DuckDBParameter("value", "");

        cmd.Parameters.Add(pKey);
        cmd.Parameters.Add(pValue);

        foreach (var (key, value) in opts.ToDictionary())
        {
            pKey.Value = key;
            pValue.Value = value;
            cmd.ExecuteNonQuery();
        }

        //Save("auto_append_history", opts.AutoAppendHistory ? "true" : "false");
        //Save("report_pdf_folder", opts.ReportPdfFolder ?? "");
        //Save("query_filter_preferred", opts.QueryFilterPreferred ? "true" : "false");
        //Save("query_filter_covered", opts.QueryFilterCovered ? "true" : "false");
        //Save("query_filter_cheap", opts.QueryFilterCheap ? "true" : "false");
        //Save("exclude_spac", opts.ExcludeSpac ? "true" : "false");
        //Save("exclude_pref_stock", opts.ExcludePrefStock ? "true" : "false");
        //Save("exclude_halted", opts.ExcludeHalted ? "true" : "false");

        lock (_optionsLock)
            _optionsCache = opts;
    }
    //private void SaveOption(DuckDBCommand cmd, string key, string value) 
    //{
    //    cmd.Parameters.Add(new DuckDBParameter { Value = key });
    //    cmd.Parameters.Add(new DuckDBParameter { Value = value });

    //    cmd.ExecuteNonQuery();
    //}
    public void SaveOption(string key, string value)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = _cmd_InsertOption;
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = key });
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = value });
        cmd.ExecuteNonQuery();
    }

    // ──────────────────────────────────────────────────────────
    //  Stock 등록 / 삭제
    // ──────────────────────────────────────────────────────────

    /// <summary>stocks 테이블에 ticker가 존재하는지 확인.</summary>
    //public bool _StockExists(string ticker)
    //{
    //    using var conn = OpenNativeConnection();
    //    using var cmd  = conn.CreateCommand();
    //    cmd.CommandText = "SELECT COUNT(*) FROM stocks WHERE ticker = $1";
    //    cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
    //    var result = cmd.ExecuteScalar();
    //    return result is not null && result is not DBNull && Convert.ToInt64(result) > 0;
    //}



    public void DeletePriceData(string ticker)
    {
        Execute("DELETE FROM stock_cache WHERE ticker = $1", ticker);
        Execute("DELETE FROM daily_prices WHERE ticker = $1", ticker);
    }

    /// <summary>
    /// ticker 관련 모든 데이터 삭제.
    /// 순서: stock_cache → supply → fundamentals → daily_prices → stock_group_map → stocks
    /// DuckDB FK 체크는 각 DELETE 실행 시점에 즉시 검증됨.
    /// Transaction 내에서는 이전 DELETE가 아직 visible하지 않아 FK 위반 발생→
    /// auto-commit 모드로 각 구문을 독립 실행해야 정상 동작.
    /// </summary>
    public void DeleteStockAllData(string ticker)
    {
        lock (_stockCacheLock) _stockItemCache = null;
        DeletePriceData(ticker);
        foreach (var sql in new[]
        {
            //"DELETE FROM stock_cache      WHERE ticker = $1",
            "DELETE FROM supply           WHERE ticker = $1",
            "DELETE FROM fundamentals     WHERE ticker = $1",
            //"DELETE FROM daily_prices     WHERE ticker = $1",
            "DELETE FROM stock_group_map  WHERE ticker = $1",
            "DELETE FROM stocks           WHERE ticker = $1",
        })
        {
            using var conn = OpenNativeConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
            cmd.ExecuteNonQuery();
        }
    }



    // ──────────────────────────────────────────────────────────
    //  Group list SQL
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 그룹 목록 공통 SQL. EditGroupView·LeftSidePanelViewModel 양쪽에서 사용.
    /// <para>
    /// SELECT 컬럼: group_id, kind, name, description, rating, is_active, count, ret_3m<br/>
    /// kindFilter : "AND g.kind != 'sector' " 형식의 WHERE 추가 조건 (없으면 "")<br/>
    /// ORDER BY   : g.rating DESC, g.ret_3m DESC<br/>
    /// </para>
    /// </summary>
    private static string GroupListSql(string kindFilter = "") => $"""
        SELECT
            g.group_id,
            g.kind,
            g.name,
            g.description,
            g.rating,
            CASE WHEN g.is_active THEN '' ELSE 'X' END AS is_active,
            COUNT(m.ticker) AS stock_count,
            (SELECT c.ret_3m
             FROM stock_group_map sgm
             JOIN stock_cache c ON c.ticker = sgm.ticker
             WHERE sgm.group_id = g.group_id
             ORDER BY sgm.weight DESC, sgm.ticker
             LIMIT 1) AS ret_3m
        FROM groups g
        LEFT JOIN stock_group_map m ON m.group_id = g.group_id
        WHERE 1=1 {kindFilter}
        GROUP BY g.group_id, g.kind, g.name, g.description, g.rating, g.is_active
        ORDER BY g.rating DESC, ret_3m DESC
        """;

    private static string BuildGroupKindFilter(bool? sector, bool? theme)
    {
        string kindFilter = "";
        if (sector != true)
            kindFilter += "AND g.kind != 'sector' ";
        if (theme != true)
            kindFilter += "AND g.kind != 'theme' ";
        return kindFilter.Length > 0 ? kindFilter : "";
    }
    public DataTable Query_GroupList(bool? sector, bool? theme) => Query(GroupListSql(BuildGroupKindFilter(sector, theme)));

    // ──────────────────────────────────────────────────────────
    //  Stock list SQL
    // ──────────────────────────────────────────────────────────
    /// <summary>
    /// stocks 조회 쿼리에 삽입할 전역 제외 필터 절 반환.
    /// alias: SQL에서 stocks 테이블에 붙인 별칭 (기본 "s").
    /// cacheAlias: stock_cache 테이블 별칭. null이면 ExcludeHalted 필터 제외.
    /// 반환 예: "AND s.name NOT LIKE '%스팩%' AND RIGHT(s.ticker,1)='0' AND c.current_price > 0"
    /// 필터 없으면 빈 문자열 반환.
    /// </summary>
    public string BuildStockExcludeFilter(string alias = "s", string? cacheAlias = null)
    {
        var opts = Options();
        var clauses = new List<string>();
        if (opts.ExcludeSpac)
            clauses.Add($"{alias}.name NOT LIKE '%스팩%'");
        if (opts.ExcludePrefStock)
            clauses.Add($"RIGHT({alias}.ticker, 1) = '0'");
        if (opts.ExcludeHalted && cacheAlias is not null)
            clauses.Add($"COALESCE({cacheAlias}.current_price, 0) > 0");
        return clauses.Count > 0
            ? "AND " + string.Join(" AND ", clauses)
            : "";
    }

    public DataTable Query_StockList(int groupId)
    {
        var excludeFilter = BuildStockExcludeFilter("s", "c");
        return Query($@"
            SELECT
                m.ticker,
                s.name,
                s.market,

                s.rating,
                m.weight,

                c.per,
                c.pbr,

                c.ret_1m,
                c.ret_3m,
                c.ret_6m,
                c.ret_1y,

                c.rs,
                c.atr_percent,
                c.distance_from_high,
                c.report_count,
                c.target_price,
                ROUND((c.target_price / NULLIF(c.current_price, 0) - 1) * 100, 2) AS upside

            FROM stock_group_map m

            JOIN stocks s
                ON s.ticker = m.ticker

            LEFT JOIN stock_cache c
                ON c.ticker = m.ticker

            WHERE
                m.group_id = {groupId}
                {excludeFilter}

            ORDER BY
                c.ret_1m DESC NULLS LAST
        ");
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
