// Services/PriceDownloadService.cs
using Quant.Core.Infrastructure;
using Quant.Core.Models;
using System.Net.Http;
using System.Text.Json;

namespace Quant.Core.Services;

/// <summary>
/// 일별 OHLCV 다운로드 → daily_prices upsert.
///
/// 소스 선택:
///   KRX 종목 (market=KP/KQ)
///     ETF           → KRX API 우선, 실패 시 Yahoo fallback
///     일반주식/인덱스 → Yahoo 우선 (adjclose 필수), 실패 시 KRX fallback
///   비KRX (해외)    → Yahoo 전용
///
/// KRX 판별: DB market 컬럼 우선. 미등록 시 코드 패턴으로 추정.
///   - 숫자 6자리        : 일반 KRX (069500)
///   - 숫자4+영문1+숫자1 : 신규 ETF 코드 (0091P0)
/// </summary>
public class PriceDownloadService
{
    private readonly DbManager _db;

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip
                               | System.Net.DecompressionMethods.Deflate,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders =
        {
            { "User-Agent",      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" },
            { "Accept",          "application/json, text/plain, */*" },
            { "Accept-Language", "en-US,en;q=0.9" },
        }
    };

    public PriceDownloadService(DbManager db) => _db = db;

    // ══════════════════════════════════════════════════════════
    //  Public entry point
    // ══════════════════════════════════════════════════════════

    public async Task<(int inserted, DateOnly lastDate)> DownloadAsync(
        string ticker,
        IProgress<string>? progress = null,
        string? yahooSymbolOverride = null)
    {
        EnsureStockExists(ticker);

        var lastInDb = _db.MaxDateForTicker("daily_prices", ticker: ticker);
        var fromDate = lastInDb.HasValue ? lastInDb.Value.AddDays(1) : new DateOnly(2020, 1, 1);
        var toDate   = DateOnly.FromDateTime(DateTime.Today);

        if (fromDate > toDate)
        {
            progress?.Report($"{ticker}: 이미 최신 ({lastInDb})");
            return (0, lastInDb ?? toDate);
        }

        progress?.Report($"{ticker}: {fromDate} ~ {toDate} 다운로드 중…");

        List<DailyPrice> prices;

        if (yahooSymbolOverride is not null)
        {
            prices = await FetchYahooAsync(yahooSymbolOverride, ticker, fromDate, toDate)
                     ?? throw new InvalidOperationException($"다운로드 실패 [{yahooSymbolOverride}]");
        }
        else if (IsKrxTicker(ticker))
        {
            bool isEtf = GetSecurityType(ticker).Equals("ETF", StringComparison.OrdinalIgnoreCase);

            if (isEtf)
            {
                // ETF: 분할 없음 → KRX 우선 (수정주가 불필요)
                prices = await FetchKrxAsync(ticker, fromDate, toDate, progress);
                if (prices.Count == 0)
                {
                    progress?.Report($"{ticker}: KRX 데이터 없음 → Yahoo fallback");
                    prices = await TryFetchYahooWithCandidates(ticker, fromDate, toDate)
                             ?? throw new InvalidOperationException($"다운로드 실패 [{ticker}]: KRX + Yahoo 모두 실패");
                }
            }
            else
            {
                // 일반주식/인덱스: Yahoo adjclose 우선, 실패 시 KRX fallback
                var yp = await TryFetchYahooWithCandidates(ticker, fromDate, toDate);
                if (yp is not null)
                {
                    prices = yp;
                }
                else
                {
                    progress?.Report($"{ticker}: Yahoo 실패 → KRX fallback (수정주가 없음)");
                    prices = await FetchKrxAsync(ticker, fromDate, toDate, progress);
                    if (prices.Count == 0)
                        throw new InvalidOperationException($"다운로드 실패 [{ticker}]: Yahoo + KRX 모두 실패");
                }
            }
        }
        else
        {
            // 해외 ETF / 지수 → Yahoo 전용
            prices = await TryFetchYahooWithCandidates(ticker, fromDate, toDate)
                     ?? throw new InvalidOperationException($"다운로드 실패 [{ticker}]");
        }

        if (prices.Count == 0)
        {
            progress?.Report($"{ticker}: 신규 데이터 없음");
            return (0, lastInDb ?? toDate);
        }

        int inserted = BulkUpsert(prices);
        var newLast  = prices.Max(p => p.Date);
        _db.LogUpdate(ticker, newLast, "krx_or_yahoo", "success");

        progress?.Report($"{ticker}: {inserted}건 저장 (최신 {newLast})");
        return (inserted, newLast);
    }

    // ══════════════════════════════════════════════════════════
    //  KRX OPEN API  (data.krx.co.kr — 순수 HTTP POST)
    // ══════════════════════════════════════════════════════════

    private static async Task<List<DailyPrice>> FetchKrxAsync(
        string ticker, DateOnly from, DateOnly to, IProgress<string>? progress)
    {
        const string url = "http://data.krx.co.kr/comm/bldAttendant/getJsonData.cmd";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["bld"]          = "dbms/MDC/STAT/standard/MDCSTAT01701",
            ["isuCd"]        = ticker,
            ["strtDd"]       = from.ToString("yyyyMMdd"),
            ["endDd"]        = to.ToString("yyyyMMdd"),
            ["share"]        = "1",
            ["money"]        = "1",
            ["csvxls_isNo"]  = "false",
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        req.Headers.Add("Referer", "http://data.krx.co.kr/");

        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return [];

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
            return [];

        var prices = new List<DailyPrice>();
        foreach (var row in output.EnumerateArray())
        {
            var dateStr = GetStr(row, "TRD_DD")?.Replace("/", "-");
            if (!DateOnly.TryParse(dateStr, out var date)) continue;

            if (!TryParseKrxNum(row, "TDD_OPNPRC", out var open))  continue;
            if (!TryParseKrxNum(row, "TDD_HGPRC",  out var high))  continue;
            if (!TryParseKrxNum(row, "TDD_LWPRC",  out var low))   continue;
            if (!TryParseKrxNum(row, "TDD_CLSPRC", out var close)) continue;
            TryParseKrxVol(row, "ACC_TRDVOL", out var volume);

            if (close <= 0 || high < low) continue;

            prices.Add(new DailyPrice
            {
                Ticker = ticker, Date = date,
                Open = open, High = high, Low = low,
                Close = close, AdjClose = close,  // KRX = 수정주가 미제공
                Volume = volume,
            });
        }

        prices.Sort((a, b) => a.Date.CompareTo(b.Date));
        return prices;
    }

    // ══════════════════════════════════════════════════════════
    //  Yahoo Finance v8
    // ══════════════════════════════════════════════════════════

    private async Task<List<DailyPrice>?> TryFetchYahooWithCandidates(
        string ticker, DateOnly from, DateOnly to)
    {
        Exception? lastEx = null;
        foreach (var sym in BuildYahooCandidates(ticker))
        {
            try
            {
                var result = await FetchYahooAsync(sym, ticker, from, to);
                if (result is not null)
                {
                    UpdateMarketIfNeeded(ticker, sym);
                    return result;
                }
            }
            catch (Exception ex) { lastEx = ex; }
        }
        _db.LogUpdate(ticker, null, "yahoo", "fail", lastEx?.Message);
        return null;
    }

    private static async Task<List<DailyPrice>?> FetchYahooAsync(
        string symbol, string ticker, DateOnly from, DateOnly to)
    {
        long p1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        long p2 = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

        var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
                  $"?period1={p1}&period2={p2}&interval=1d&events=adjsplit";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Referer", "https://finance.yahoo.com/");
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc   = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var chartResult = doc.RootElement.GetProperty("chart").GetProperty("result");
        if (chartResult.ValueKind == JsonValueKind.Null || chartResult.GetArrayLength() == 0) return null;

        var result     = chartResult[0];
        if (!result.TryGetProperty("timestamp", out var tsEl)) return null;
        var timestamps = tsEl.EnumerateArray().ToList();
        var indicators = result.GetProperty("indicators");
        var quote      = indicators.GetProperty("quote")[0];

        List<JsonElement>? adjArr = null;
        if (indicators.TryGetProperty("adjclose", out var adjEl) && adjEl.GetArrayLength() > 0 &&
            adjEl[0].TryGetProperty("adjclose", out var adjInner))
            adjArr = adjInner.EnumerateArray().ToList();

        var opens   = quote.GetProperty("open").EnumerateArray().ToList();
        var highs   = quote.GetProperty("high").EnumerateArray().ToList();
        var lows    = quote.GetProperty("low").EnumerateArray().ToList();
        var closes  = quote.GetProperty("close").EnumerateArray().ToList();
        var volumes = quote.GetProperty("volume").EnumerateArray().ToList();

        var prices = new List<DailyPrice>(timestamps.Count);
        for (int i = 0; i < timestamps.Count; i++)
        {
            if (opens[i].ValueKind == JsonValueKind.Null || closes[i].ValueKind == JsonValueKind.Null) continue;

            var date     = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime);
            var rawOpen  = opens[i].GetDouble();
            var rawHigh  = highs[i].GetDouble();
            var rawLow   = lows[i].GetDouble();
            var rawClose = closes[i].GetDouble();
            var adj      = adjArr != null && adjArr[i].ValueKind != JsonValueKind.Null
                           ? adjArr[i].GetDouble() : rawClose;
            var volume   = volumes[i].ValueKind == JsonValueKind.Null ? 0L : volumes[i].GetInt64();

            if (rawClose <= 0 || adj <= 0) continue;
            var ratio = adj / rawClose;
            var open  = Math.Round(rawOpen * ratio, 2);
            var high  = Math.Round(rawHigh * ratio, 2);
            var low   = Math.Round(rawLow  * ratio, 2);
            if (open <= 0 || low <= 0 || high < low) continue;

            prices.Add(new DailyPrice
            {
                Ticker = ticker, Date = date,
                Open = open, High = high, Low = low,
                Close = adj, AdjClose = adj,
                Volume = volume,
            });
        }
        return prices;
    }

    // ══════════════════════════════════════════════════════════
    //  DB upsert
    // ══════════════════════════════════════════════════════════

    private int BulkUpsert(List<DailyPrice> prices)
    {
        int count = 0;
        using var conn = _db.OpenNativeConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO daily_prices (ticker, date, open, high, low, close, adj_close, volume)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (ticker, date) DO UPDATE SET
                open=excluded.open, high=excluded.high, low=excluded.low,
                close=excluded.close, adj_close=excluded.adj_close, volume=excluded.volume";
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

    // ══════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// KRX 종목 여부 판별.
    /// 1순위: DB stocks.market = 'KP' 또는 'KQ'
    /// 2순위(미등록): 코드 패턴
    ///   - \d{6}       → 일반 KRX (069500)
    ///   - \d{4}[A-Z]\d → 신규 ETF (0091P0, 2023년 이후)
    /// </summary>
    private bool IsKrxTicker(string ticker) => _db.IsKrxTicker(ticker);
    //{
    //    using var conn = _db.OpenNativeConnection();
    //    using var cmd  = conn.CreateCommand();
    //    cmd.CommandText = "SELECT market FROM stocks WHERE ticker = $1";
    //    cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
    //    var res = cmd.ExecuteScalar();
    //    if (res is not null and not DBNull)
    //    {
    //        var m = res.ToString()!;
    //        return m == "KP" || m == "KQ";
    //    }
    //    return System.Text.RegularExpressions.Regex.IsMatch(ticker, @"^\d{6}$") ||
    //           System.Text.RegularExpressions.Regex.IsMatch(ticker, @"^\d{4}[A-Z]\d$");
    //}

    private string GetSecurityType(string ticker) => _db.SecurityTypeForTicker(ticker);
    //{
    //    using var conn = _db.OpenNativeConnection();
    //    using var cmd  = conn.CreateCommand();
    //    cmd.CommandText = "SELECT security_type FROM stocks WHERE ticker = $1";
    //    cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
    //    var res = cmd.ExecuteScalar();
    //    return res is null or DBNull ? "stock" : res.ToString()!;
    //}

    private List<string> BuildYahooCandidates(string ticker)
    {
        using var conn = _db.OpenNativeConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT market FROM stocks WHERE ticker = $1";
        cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
        var res    = cmd.ExecuteScalar();
        var market = res is null or DBNull ? null : res.ToString();

        if (market == "KQ") return [$"{ticker}.KQ", $"{ticker}.KS"];
        if (market == "KP") return [$"{ticker}.KS", $"{ticker}.KQ"];
        return [ticker];  // 해외
    }

    private void UpdateMarketIfNeeded(string ticker, string usedSymbol)
    {
        if (!IsKrxTicker(ticker)) return;
        var correctMarket = usedSymbol.EndsWith(".KQ") ? "KQ" : "KP";
        try
        {
            using var conn = _db.OpenNativeConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "UPDATE stocks SET market=$1 WHERE ticker=$2 AND market != $1";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = correctMarket });
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private static string? GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryParseKrxNum(JsonElement el, string key, out double value)
    {
        var s = GetStr(el, key)?.Replace(",", "");
        return double.TryParse(s, out value) && value >= 0;
    }

    private static bool TryParseKrxVol(JsonElement el, string key, out long value)
    {
        var s = GetStr(el, key)?.Replace(",", "");
        return long.TryParse(s, out value);
    }

    // ══════════════════════════════════════════════════════════
    //  Yahoo 종목 메타 조회  (신규 등록 팝업용)
    // ══════════════════════════════════════════════════════════

    public async Task<(string? LongName, string? ShortName, string? Market, string? QuoteType)?>
        FetchStockMetaAsync(string ticker)
    {
        var candidates = IsKrxTicker(ticker)
            ? new[] { ticker + ".KS", ticker + ".KQ" }
            : new[] { ticker };

        foreach (var symbol in candidates)
        {
            try
            {
                var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
                          $"?period1=0&period2=1&interval=1d";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Referer", "https://finance.yahoo.com/");
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("chart", out var chart)) continue;
                var results = chart.GetProperty("result");
                if (results.ValueKind == JsonValueKind.Null || results.GetArrayLength() == 0) continue;

                var meta = results[0].GetProperty("meta");
                string? Get(string key) =>
                    meta.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                        ? el.GetString() : null;

                var yahooMarket = Get("exchangeName") ?? "";
                var market = yahooMarket.Contains("KOSDAQ", StringComparison.OrdinalIgnoreCase) ? "KQ"
                    : yahooMarket.Contains("KSC",   StringComparison.OrdinalIgnoreCase) ||
                      yahooMarket.Contains("KOSPI", StringComparison.OrdinalIgnoreCase) ? "KP"
                    : symbol.EndsWith(".KQ") ? "KQ" : "KP";

                return (
                    Get("longName") ?? Get("shortName") ?? Get("symbol"),
                    Get("shortName"),
                    market,
                    Get("instrumentType") ?? Get("quoteType")
                );
            }
            catch { }
        }
        return null;
    }

    private void EnsureStockExists(string ticker)
    {
        if (!_db.ExistTicker("stocks", ticker))
            _db.UpsertStock(ticker, ticker);

        //using var conn = _db.OpenNativeConnection();
        //using var cmd  = conn.CreateCommand();
        //cmd.CommandText = "SELECT ticker FROM stocks WHERE ticker = $1";
        //cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = ticker });
        //if (cmd.ExecuteScalar() is null or DBNull)
        //    _db.UpsertStock(ticker, ticker);
    }
}
