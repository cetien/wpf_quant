// Services/PriceDownloadService.cs
using Quant.Core.Infrastructure;
using Quant.Core.Models;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Quant.Core.Services;

/// <summary>
/// Yahoo Finance v8 API로 일별 OHLCV를 다운로드해 daily_prices에 upsert.
/// <para>
/// - KRX 종목: ticker + ".KS" (KOSPI) 또는 ".KQ" (KOSDAQ) suffix 자동 판별<br/>
/// - 증분 다운로드: DB 최신일 다음날부터 오늘까지만 요청<br/>
/// - stocks 마스터 없으면 자동 upsert (name = ticker)<br/>
/// - 결과: (inserted, skipped, lastDate) 반환
/// </para>
/// </summary>
public class PriceDownloadService
{
    private readonly DbManager _db;
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0" },
            { "Accept",     "application/json" },
        }
    };

    public PriceDownloadService(DbManager db) => _db = db;

    // ──────────────────────────────────────────────────────────
    //  Public entry point
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 지정 ticker의 일별 가격을 다운로드해 DB에 저장.
    /// </summary>
    /// <param name="ticker">KRX 6자리 (예: "005930") 또는 Yahoo 심볼 그대로</param>
    /// <param name="progress">진행 메시지 콜백 (UI 상태바 연결용)</param>
    /// <returns>(inserted, lastDate)</returns>
    public async Task<(int inserted, DateOnly lastDate)> DownloadAsync(
        string ticker,
        IProgress<string>? progress = null)
    {
        // ── 1. stocks 마스터 보장 ──────────────────────────────
        EnsureStockExists(ticker);

        // ── 2. 기준일 결정 (증분) ─────────────────────────────
        var lastInDb = _db.GetLastDate("daily_prices", ticker: ticker);
        var fromDate = lastInDb.HasValue
            ? lastInDb.Value.AddDays(1)
            : new DateOnly(2000, 1, 1);
        var toDate = DateOnly.FromDateTime(DateTime.Today);

        if (fromDate > toDate)
        {
            progress?.Report($"{ticker}: 이미 최신 ({lastInDb})");
            return (0, lastInDb ?? toDate);
        }

        progress?.Report($"{ticker}: {fromDate} ~ {toDate} 다운로드 중…");

        // ── 3. Yahoo Finance 심볼 결정 ────────────────────────
        var symbol = ToYahooSymbol(ticker);

        // ── 4. HTTP 요청 ──────────────────────────────────────
        List<DailyPrice> prices;
        try
        {
            prices = await FetchYahooAsync(symbol, ticker, fromDate, toDate);
        }
        catch (Exception ex)
        {
            _db.LogUpdate(ticker, null, "yahoo", "fail", ex.Message);
            throw new InvalidOperationException($"다운로드 실패 [{symbol}]: {ex.Message}", ex);
        }

        if (prices.Count == 0)
        {
            progress?.Report($"{ticker}: 신규 데이터 없음");
            return (0, lastInDb ?? toDate);
        }

        // ── 5. DB upsert ──────────────────────────────────────
        int inserted = BulkUpsert(prices);
        var newLast  = prices.Max(p => p.Date);
        _db.LogUpdate(ticker, newLast, "yahoo", "success");

        progress?.Report($"{ticker}: {inserted}건 저장 (최신 {newLast})");
        return (inserted, newLast);
    }

    // ──────────────────────────────────────────────────────────
    //  Yahoo Finance v8 fetch
    // ──────────────────────────────────────────────────────────

    private static async Task<List<DailyPrice>> FetchYahooAsync(
        string symbol, string ticker, DateOnly from, DateOnly to)
    {
        long period1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        long period2 = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
                  $"?period1={period1}&period2={period2}&interval=1d&events=adjsplit";

        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();

        using var doc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var result     = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
        var timestamps = result.GetProperty("timestamp").EnumerateArray().ToList();
        var quote      = result.GetProperty("indicators").GetProperty("quote")[0];
        var adjclose   = result.GetProperty("indicators").GetProperty("adjclose")[0]
                              .GetProperty("adjclose").EnumerateArray().ToList();

        var opens   = quote.GetProperty("open").EnumerateArray().ToList();
        var highs   = quote.GetProperty("high").EnumerateArray().ToList();
        var lows    = quote.GetProperty("low").EnumerateArray().ToList();
        var closes  = quote.GetProperty("close").EnumerateArray().ToList();
        var volumes = quote.GetProperty("volume").EnumerateArray().ToList();

        var prices = new List<DailyPrice>(timestamps.Count);
        for (int i = 0; i < timestamps.Count; i++)
        {
            // null bar (거래 없는 날) 스킵
            if (opens[i].ValueKind   == JsonValueKind.Null ||
                closes[i].ValueKind  == JsonValueKind.Null) continue;

            var date     = DateOnly.FromDateTime(
                DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime);
            var open     = opens[i].GetDouble();
            var high     = highs[i].GetDouble();
            var low      = lows[i].GetDouble();
            var close    = closes[i].GetDouble();
            var adj      = adjclose[i].ValueKind == JsonValueKind.Null ? close : adjclose[i].GetDouble();
            var volume   = volumes[i].ValueKind  == JsonValueKind.Null ? 0L    : volumes[i].GetInt64();

            if (open <= 0 || close <= 0 || low <= 0 || high < low) continue;

            prices.Add(new DailyPrice
            {
                Ticker   = ticker,
                Date     = date,
                Open     = open,
                High     = high,
                Low      = low,
                Close    = close,
                AdjClose = adj,
                Volume   = volume,
            });
        }
        return prices;
    }

    // ──────────────────────────────────────────────────────────
    //  DB upsert (DuckDB native $1..$9)
    // ──────────────────────────────────────────────────────────

    private int BulkUpsert(List<DailyPrice> prices)
    {
        int count = 0;
        using var conn = _db.OpenNativeConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO daily_prices
                (ticker, date, open, high, low, close, adj_close, volume)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (ticker, date) DO UPDATE SET
                open      = excluded.open,
                high      = excluded.high,
                low       = excluded.low,
                close     = excluded.close,
                adj_close = excluded.adj_close,
                volume    = excluded.volume";

        // 파라미터 슬롯 미리 생성
        for (int i = 0; i < 8; i++)
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter());

        foreach (var p in prices)
        {
            cmd.Parameters[0].Value = p.Ticker;
            cmd.Parameters[1].Value = p.Date.ToString("yyyy-MM-dd");
            cmd.Parameters[2].Value = p.Open;
            cmd.Parameters[3].Value = p.High;
            cmd.Parameters[4].Value = p.Low;
            cmd.Parameters[5].Value = p.Close;
            cmd.Parameters[6].Value = p.AdjClose;
            cmd.Parameters[7].Value = p.Volume;
            cmd.ExecuteNonQuery();
            count++;
        }
        return count;
    }

    // ──────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// KRX 6자리 숫자 ticker → Yahoo Finance 심볼.
    /// KOSPI(.KS) / KOSDAQ(.KQ) 판별: stocks.market 참조, 없으면 .KS 기본값.
    /// 이미 suffix 포함된 심볼(예: AAPL)은 그대로 반환.
    /// </summary>
    private string ToYahooSymbol(string ticker)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(ticker, @"^\d{6}$"))
            return ticker;

        using var conn = _db.OpenNativeConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT market FROM stocks WHERE ticker = $1";
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
        var result = cmd.ExecuteScalar();
        var market = (result is null || result is DBNull) ? null : result.ToString();

        var suffix = market == "KQ" ? ".KQ" : ".KS";
        return ticker + suffix;
    }

    private void EnsureStockExists(string ticker)
    {
        using var conn = _db.OpenNativeConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT ticker FROM stocks WHERE ticker = $1";
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
        var exists = cmd.ExecuteScalar();
        if (exists is null || exists is DBNull)
            _db.UpsertHistory(ticker, ticker);
    }
}
