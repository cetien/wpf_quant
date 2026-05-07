// Services/IndicatorDownloadService.cs
using Quant.Core.Infrastructure;

namespace Quant.Core.Services;

/// <summary>
/// Global Indicator 다운로드 서비스.
/// daily_prices 테이블을 그대로 사용하고, stocks 마스터에 indicator 행을 보장한다.
///
/// Yahoo Finance 심볼 참고:
///   ^GSPC  = S&P 500       ^IXIC  = NASDAQ Composite
///   ^SOX   = Philadelphia SOX
///   ^KS11  = KOSPI         ^KQ11  = KOSDAQ
///   DXY=X  = US Dollar Index (DX-Y.NYB 는 일부 환경에서 미작동)
///   CL=F   = WTI Crude Oil Futures
/// </summary>
public class IndicatorDownloadService
{
    // ticker(DB key) → (yahoo symbol, 표시명, market)
    // ticker와 yahoo symbol을 분리: DB key는 안전한 문자열, yahoo는 실제 요청 심볼
    public static readonly IReadOnlyList<IndicatorDef> IndicatorDefs =
    [
        new("IDX_SPX",    "^GSPC",   "S&P 500",  "NYSE"),
        new("IDX_NDX",    "^IXIC",   "NASDAQ",   "NYSE"),
        new("IDX_SOX",    "^SOX",    "SOX",      "NYSE"),
        new("IDX_KOSPI",  "^KS11",   "KOSPI",    "KP"),
        new("IDX_KOSDAQ", "^KQ11",   "KOSDAQ",   "KQ"),
        new("IDX_DXY",    "DXY=X",   "DXY",      "NYSE"),
        new("IDX_WTI",    "CL=F",    "WTI",      "NYSE"),
    ];

    private readonly DbManager            _db;
    private readonly PriceDownloadService _priceSvc;

    public IndicatorDownloadService(DbManager db)
    {
        _db       = db;
        _priceSvc = new PriceDownloadService(db);
    }

    /// <summary>모든 indicator 순차 다운로드.</summary>
    public async Task DownloadAllAsync(
        IProgress<(string symbol, string msg)>? progress = null,
        CancellationToken ct = default)
    {
        EnsureStocksExist();

        foreach (var def in IndicatorDefs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var relay = progress is null ? null
                    : new Progress<string>(msg => progress.Report((def.Label, msg)));

                // PriceDownloadService는 ticker(DB key)로 호출
                // ToYahooSymbol이 6자리 숫자가 아니면 ticker 그대로 사용하므로
                // DB key가 yahoo symbol과 다른 경우 직접 yahoo symbol을 넘겨야 함
                // → OverrideYahooSymbol을 통해 해결
                await _priceSvc.DownloadAsync(def.DbTicker, relay, def.YahooSymbol);
            }
            catch (Exception ex)
            {
                progress?.Report((def.Label, $"오류: {ex.Message}"));
            }
        }
    }

    /// <summary>DB에서 각 indicator 최신 종가 조회.</summary>
    public List<(string DbTicker, double Value, double ChangePct)> LoadLatestValues()
    {
        var result = new List<(string, double, double)>();
        foreach (var def in IndicatorDefs)
        {
            try
            {
                var dt = _db.Query(
                    $"SELECT adj_close FROM daily_prices " +
                    $"WHERE ticker = '{def.DbTicker}' " +
                    $"ORDER BY date DESC LIMIT 2");

                if (dt.Rows.Count == 0) continue;

                double.TryParse(dt.Rows[0]["adj_close"]?.ToString(), out var latest);
                var changePct = 0.0;
                if (dt.Rows.Count >= 2)
                {
                    double.TryParse(dt.Rows[1]["adj_close"]?.ToString(), out var prev);
                    if (prev > 0) changePct = (latest - prev) / prev * 100.0;
                }
                result.Add((def.DbTicker, latest, changePct));
            }
            catch { }
        }
        return result;
    }

    private void EnsureStocksExist()
    {
        using var conn = _db.OpenNativeConnection();
        foreach (var def in IndicatorDefs)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO stocks (ticker, name, market, security_type, is_active) " +
                "VALUES ($1, $2, $3, 'index', TRUE) " +
                "ON CONFLICT (ticker) DO NOTHING";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = def.DbTicker });
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = def.Label    });
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = def.Market   });
            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>Indicator 정의 레코드.</summary>
public record IndicatorDef(string DbTicker, string YahooSymbol, string Label, string Market);
