// CorrelationView.xaml.cs
using DuckDB.NET.Data;

using Quant.Core.Infrastructure;
using Quant.Core.Models;
using Quant.UI.Common;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quant.UI.Views;

// ─────────────────────────────────────────────────────────────────────────────
//  표시용 결과 모델 (UI 전용 — CorrelationResult에 Name, LeadLagLabel 추가)
// ─────────────────────────────────────────────────────────────────────────────
public class CorrelationRow
{
    public string Ticker { get; set; } = "";
    public string Name { get; set; } = "";
    public double Corr20 { get; set; }
    public double Corr60 { get; set; }
    public double Corr120 { get; set; }
    public double Sim20 { get; set; }
    public double Sim60 { get; set; }
    public double Sim120 { get; set; }
    public double Score { get; set; }
    public int BestLag { get; set; }
    public double BestLagCorr { get; set; }

    // stock_cache 추가 정보
    public double Rs { get; set; }
    public double Ret1M { get; set; }
    public double Upside { get; set; }  // (target_price / current_price - 1) * 100
    public double AtrPercent { get; set; }

    // Lead-Lag Grid 판정 텍스트
    // BestLag > 0 → 비교 종목이 N일 선행 (현재 종목 후행)
    // BestLag < 0 → 현재 종목이 N일 선행
    public string LeadLagLabel => BestLag switch
    {
        > 0 => $"선행 +{BestLag}일",
        < 0 => $"후행 {BestLag}일",
        _ => "동행"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
//  CorrelationView
// ─────────────────────────────────────────────────────────────────────────────
public partial class CorrelationView : UserControl, ITickerNavigationAware
{
    // ── 이벤트 ────────────────────────────────────────────────────────────────
    public event Action<string, string>? StatusChanged;

    // ── 상태 ──────────────────────────────────────────────────────────────────
    private readonly DbManager _db;
    private string _currentTicker = "";
    private bool _isInternalTextChange = false;

    // ── 캐시: 전체 종목 수익률 벡터 (종목 전환 시 재사용) ────────────────────
    // key = ticker, value = 일별 수익률 배열 (250거래일 기준)
    private Dictionary<string, double[]> _returnsCache = new();
    private List<string> _tradingDates = new();   // 공통 거래일 순서

    // ── CancellationToken ─────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;

    // ── 캐시 유효성 키 (DB 최신 날짜 변경 시 무효화) ─────────────────────────
    private string _cacheAsofDate = "";

    // ── 비교대상 필터 상태 (체크박스와 동기화) ───────────────────────────────
    private bool _filterKP = true;
    private bool _filterKQ = true;
    private bool _filterUS = true;
    private bool _filterETF = false;
    private bool _isInitialized = false;  // InitializeComponent 완료 전 이벤트 차단

    public CorrelationView(DbManager db)
    {
        _db = db;
        InitializeComponent();
        _isInitialized = true;  // 이후부터 체크박스 이벤트 처리
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ITickerNavigationAware
    // ─────────────────────────────────────────────────────────────────────────
    public void OnNavigatedToTicker(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return;
        LoadTicker(ticker);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  종목 지정 진입점
    // ─────────────────────────────────────────────────────────────────────────
    private void LoadTicker(string tickerOrKeyword)
    {
        var list = SearchStocks(tickerOrKeyword.Trim());
        var match = list.FirstOrDefault();
        var ticker = match?.Ticker ?? tickerOrKeyword.Trim().ToUpper();
        var name = match?.Name ?? "";

        _currentTicker = ticker;

        _isInternalTextChange = true;
        TxtStockSearch.Text = string.IsNullOrEmpty(name) ? ticker : name;
        _isInternalTextChange = false;

        TxtCurrentTicker.Text = ticker;
        TxtCurrentName.Text = name;

        _ = RunAnalysisAsync(ticker);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  분석 메인 (async)
    // ─────────────────────────────────────────────────────────────────────────
    private async Task RunAnalysisAsync(string baseTicker)
    {
        // 이전 분석 취소
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        ShowLoading(true, "가격 데이터 로드 중...");
        GridCorrelation.ItemsSource = null;
        GridLeadLag.ItemsSource = null;
        ClearSummary();

        try
        {
            // ── 1. 수익률 캐시 구성 (캐시 없으면 DB 로드) ────────────────────
            await Task.Run(() => EnsureReturnsCache(token), token);
            token.ThrowIfCancellationRequested();

            if (!_returnsCache.ContainsKey(baseTicker))
            {
                SetStatus($"데이터 없음: {baseTicker}", StatusColors.Warning);
                ShowLoading(false);
                return;
            }

            ShowLoading(true, "상관도 계산 중...");

            // ── 2. 상관도 + 유사도 계산 ──────────────────────────────────────
            var results = await Task.Run(
                () => CalcCorrelations(baseTicker, token), token);
            token.ThrowIfCancellationRequested();

            // ── 3. Score 순 정렬, Corr60 ≥ 0 필터, 상위 50 ──────────────────
            var top50 = results
                .Where(r => r.Corr60 >= 0)
                .OrderByDescending(r => r.Score)
                .Take(50)
                .ToList();

            ShowLoading(true, "Lead-Lag 계산 중...");

            // ── 4. Lead-Lag: Score 상위 200 대상 ─────────────────────────────
            var top200 = results
                .OrderByDescending(r => r.Score)
                .Take(200)
                .ToList();

            await Task.Run(
                () => CalcLeadLag(baseTicker, top200, token), token);
            token.ThrowIfCancellationRequested();

            var leadLagRows = top200
                .Where(r => r.BestLagCorr > 0)
                .OrderByDescending(r => r.BestLagCorr)
                .ToList();

            // ── 5. UI 업데이트 ────────────────────────────────────────────────
            Dispatcher.Invoke(() =>
            {
                GridCorrelation.ItemsSource = top50;
                GridLeadLag.ItemsSource = leadLagRows;
                RenderSummary(baseTicker, top50, leadLagRows);
                TxtPeriodInfo.Text = $"{_tradingDates.Count}거래일 기준";
                SetStatus($"완료  {top50.Count}종목 표시", StatusColors.Success);
                ShowLoading(false);
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Invoke(() =>
            {
                ShowLoading(false);
                SetStatus("분석 취소됨", StatusColors.Muted);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                ShowLoading(false);
                SetStatus($"오류: {ex.Message}", StatusColors.Error);
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  수익률 캐시 구성
    //  - stock_cache 보유 종목 전체
    //  - daily_prices 최신 날짜 기준 250거래일
    //  - 공통 거래일 기준 inner-join
    // ─────────────────────────────────────────────────────────────────────────
    private void EnsureReturnsCache(CancellationToken token)
    {
        // 최신 거래일 확인 → 날짜 바뀌었으면 캐시 무효화
        using var conn0 = _db.OpenNativeConnection();
        string latestDate;
        using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "SELECT CAST(MAX(date) AS VARCHAR) FROM daily_prices";
            latestDate = cmd.ExecuteScalar()?.ToString() ?? "";
        }

        if (latestDate == _cacheAsofDate && _returnsCache.Count > 0 && _tradingDates.Count > 0)
            return; // 캐시 유효

        _returnsCache.Clear();
        _tradingDates.Clear();
        _cacheAsofDate = latestDate;

        if (string.IsNullOrEmpty(latestDate)) return;

        // 2. 필터 적용 종목 수 확인 (0이면 조기 종료)
        int filteredCount;
        using var conn = _db.OpenNativeConnection();
        using (var cmd = conn.CreateCommand())
        {
            var excl = BuildCorrExcludeFilter();
            cmd.CommandText = $"""
                SELECT COUNT(*)
                FROM stock_cache c
                JOIN stocks s ON s.ticker = c.ticker
                WHERE 1=1 {excl}
                """;
            filteredCount = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        if (filteredCount == 0) return;

        token.ThrowIfCancellationRequested();

        // 3. 전체 가격 로드 (250거래일) — JOIN으로 필터 종목만 수신
        //    ticker별 실제 보유 날짜만 읽고, 공통 날짜 기준 forward-fill로 정렬
        var rawPrices = new Dictionary<string, SortedDictionary<string, double>>();

        using (var cmd = conn.CreateCommand())
        {
            var excl = BuildCorrExcludeFilter();
            cmd.CommandText = $"""
                SELECT dp.ticker, CAST(dp.date AS VARCHAR), dp.adj_close
                FROM daily_prices dp
                JOIN stocks s ON s.ticker = dp.ticker
                JOIN stock_cache c ON c.ticker = dp.ticker
                WHERE dp.date IN (
                    SELECT DISTINCT date FROM daily_prices
                    WHERE date <= $1
                    ORDER BY date DESC
                    LIMIT 251
                )
                {excl}
                ORDER BY dp.ticker, dp.date ASC
                """;
            cmd.Parameters.Add(new DuckDBParameter { Value = latestDate });

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var tk = r.GetString(0);
                var dt = r.GetString(1);
                var price = r.GetDouble(2);
                if (!rawPrices.TryGetValue(tk, out var dict))
                    rawPrices[tk] = dict = new SortedDictionary<string, double>();
                dict[dt] = price;
            }
        }

        if (rawPrices.Count == 0) return;

        // 4. 공통 날짜 축 구성: 모든 종목의 날짜 union → 정렬
        var allDates = rawPrices.Values
            .SelectMany(d => d.Keys)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        _tradingDates = allDates;

        // 5. 종목별 forward-fill 후 수익률 벡터 생성
        //    누락 날짜 → 직전 유효 가격으로 채움 (수익률 = 0)
        foreach (var (tk, dict) in rawPrices)
        {
            token.ThrowIfCancellationRequested();

            var prices = new double[allDates.Count];
            double lastPrice = 0;

            for (int i = 0; i < allDates.Count; i++)
            {
                if (dict.TryGetValue(allDates[i], out var p))
                    lastPrice = p;
                // 누락 날짜는 lastPrice 유지 (forward-fill)
                // lastPrice == 0 이면 아직 상장 전 → 0 유지
                prices[i] = lastPrice;
            }

            // 첫 유효 가격 이전 구간(상장 전)이 있으면 첫 유효값으로 backward-fill
            double firstValid = prices.FirstOrDefault(p => p > 0);
            if (firstValid <= 0) continue; // 유효 가격 없음 → 제외
            for (int i = 0; i < prices.Length; i++)
                if (prices[i] <= 0) prices[i] = firstValid;

            // 수익률 벡터
            var rets = new double[allDates.Count - 1];
            for (int i = 1; i < allDates.Count; i++)
                rets[i - 1] = prices[i] / prices[i - 1] - 1.0;

            _returnsCache[tk] = rets;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Pearson Correlation + Cosine Similarity 계산
    // ─────────────────────────────────────────────────────────────────────────
    private List<CorrelationRow> CalcCorrelations(string baseTicker, CancellationToken token)
    {
        var baseRets = _returnsCache[baseTicker];
        var n = baseRets.Length;

        // 종목명·시장·유형 조회
        var nameMap = GetNameMap();

        // 필터 상태 캡처 (UI 스레드 값을 worker 스레드로 전달)
        bool filterKP = _filterKP;
        bool filterKQ = _filterKQ;
        bool filterUS = _filterUS;
        bool filterETF = _filterETF;

        var results = new System.Collections.Concurrent.ConcurrentBag<CorrelationRow>();

        Parallel.ForEach(_returnsCache, new ParallelOptions { CancellationToken = token },
            kvp =>
            {
                if (kvp.Key == baseTicker) return;
                var rets = kvp.Value;
                if (rets.Length != n) return;

                // 비교대상 필터 적용
                if (nameMap.TryGetValue(kvp.Key, out var info))
                {
                    var market = info.Market;
                    var type = info.SecurityType;
                    bool isETF = type == "ETF";
                    bool isKP = market == "KP" && !isETF;
                    bool isKQ = market == "KQ" && !isETF;
                    bool isUS = market != "KP" && market != "KQ" && !isETF;

                    if (isETF && !filterETF) return;
                    if (isKP && !filterKP) return;
                    if (isKQ && !filterKQ) return;
                    if (isUS && !filterUS) return;
                }

                var row = new CorrelationRow
                {
                    Ticker = kvp.Key,
                    Name = nameMap.TryGetValue(kvp.Key, out var i) ? i.Name : kvp.Key,
                    Rs = nameMap.TryGetValue(kvp.Key, out var i2) ? i2.Rs : 0,
                    Ret1M = nameMap.TryGetValue(kvp.Key, out var i3) ? i3.Ret1M : 0,
                    Upside = nameMap.TryGetValue(kvp.Key, out var i4) ? i4.Upside : 0,
                    AtrPercent = nameMap.TryGetValue(kvp.Key, out var i5) ? i5.AtrPercent : 0,
                    Corr20 = Pearson(baseRets, rets, n, 20),
                    Corr60 = Pearson(baseRets, rets, n, 60),
                    Corr120 = Pearson(baseRets, rets, n, 120),
                    Sim20 = CosineSim(baseRets, rets, n, 20),
                    Sim60 = CosineSim(baseRets, rets, n, 60),
                    Sim120 = CosineSim(baseRets, rets, n, 120),
                };

                row.Score = row.Corr60 * 40.0
                          + row.Corr120 * 20.0
                          + row.Sim60 * 20.0
                          + row.Sim120 * 20.0;

                results.Add(row);
            });

        return results.ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Lead-Lag 계산 (top200 in-place 수정)
    // ─────────────────────────────────────────────────────────────────────────
    private void CalcLeadLag(string baseTicker, List<CorrelationRow> rows,
                              CancellationToken token)
    {
        var baseRets = _returnsCache[baseTicker];
        var n = baseRets.Length;
        const int maxLag = 10;

        Parallel.ForEach(rows, new ParallelOptions { CancellationToken = token },
            row =>
            {
                if (!_returnsCache.TryGetValue(row.Ticker, out var rets)) return;
                if (rets.Length != n) return;

                double bestCorr = double.MinValue;
                int bestLag = 0;

                for (int lag = -maxLag; lag <= maxLag; lag++)
                {
                    // lag > 0: 비교 종목을 lag일 앞당겨 비교 (비교 종목이 선행)
                    // lag < 0: base 종목을 |lag|일 앞당겨 비교 (base가 선행)
                    double c = PearsonWithLag(baseRets, rets, n, lag);
                    if (c > bestCorr) { bestCorr = c; bestLag = lag; }
                }

                row.BestLag = bestLag;
                row.BestLagCorr = bestCorr;
            });
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  수학 헬퍼
    // ─────────────────────────────────────────────────────────────────────────

    // 최근 window 일 Pearson
    private static double Pearson(double[] a, double[] b, int n, int window)
    {
        int len = Math.Min(window, n);
        int s = n - len;
        return PearsonSlice(a, b, s, len);
    }

    private static double PearsonSlice(double[] a, double[] b, int start, int len)
    {
        double sumA = 0, sumB = 0;
        for (int i = start; i < start + len; i++) { sumA += a[i]; sumB += b[i]; }
        double meanA = sumA / len, meanB = sumB / len;

        double cov = 0, varA = 0, varB = 0;
        for (int i = start; i < start + len; i++)
        {
            double da = a[i] - meanA, db = b[i] - meanB;
            cov += da * db; varA += da * da; varB += db * db;
        }
        double denom = Math.Sqrt(varA * varB);
        return denom < 1e-12 ? 0 : cov / denom;
    }

    // Cosine: max(0, cos) × 100
    private static double CosineSim(double[] a, double[] b, int n, int window)
    {
        int len = Math.Min(window, n);
        int s = n - len;
        double dot = 0, normA = 0, normB = 0;
        for (int i = s; i < s + len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        double denom = Math.Sqrt(normA * normB);
        double cos = denom < 1e-12 ? 0 : dot / denom;
        return Math.Max(0, cos) * 100.0;
    }

    // Pearson with lag (전체 배열 기준)
    private static double PearsonWithLag(double[] @base, double[] comp, int n, int lag)
    {
        // lag > 0: comp를 lag만큼 앞당김 → base[lag..n-1] vs comp[0..n-1-lag]
        // lag < 0: base를 |lag|만큼 앞당김 → base[0..n-1+lag] vs comp[-lag..n-1]
        int absLag = Math.Abs(lag);
        int len = n - absLag;
        if (len < 10) return 0;

        int startBase = lag > 0 ? lag : 0;
        int startComp = lag < 0 ? -lag : 0;

        double sumA = 0, sumB = 0;
        for (int i = 0; i < len; i++) { sumA += @base[startBase + i]; sumB += comp[startComp + i]; }
        double meanA = sumA / len, meanB = sumB / len;

        double cov = 0, varA = 0, varB = 0;
        for (int i = 0; i < len; i++)
        {
            double da = @base[startBase + i] - meanA;
            double db = comp[startComp + i] - meanB;
            cov += da * db; varA += da * da; varB += db * db;
        }
        double denom = Math.Sqrt(varA * varB);
        return denom < 1e-12 ? 0 : cov / denom;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Summary 텍스트 생성 (룰 기반, 상수는 추후 분리)
    // ─────────────────────────────────────────────────────────────────────────
    private void RenderSummary(string baseTicker,
                                List<CorrelationRow> top50,
                                List<CorrelationRow> leadLag)
    {
        if (top50.Count == 0) { ClearSummary(); return; }

        var best = top50[0];

        // Summary 카드
        TxtTopCorr.Text = string.IsNullOrEmpty(best.Name) ? best.Ticker : best.Name;
        TxtTopCorrVal.Text = $"{best.Ticker}  Corr60 {best.Corr60:F3}";

        var bestSim = top50.OrderByDescending(r => r.Sim60).FirstOrDefault();
        if (bestSim is not null)
        {
            TxtTopSim.Text = string.IsNullOrEmpty(bestSim.Name) ? bestSim.Ticker : bestSim.Name;
            TxtTopSimVal.Text = $"{bestSim.Ticker}  Sim60 {bestSim.Sim60:F0}점";
        }

        var leader = leadLag.Where(r => r.BestLag > 0)
                            .OrderByDescending(r => r.BestLagCorr)
                            .FirstOrDefault();
        if (leader is not null)
        {
            TxtLeader.Text = string.IsNullOrEmpty(leader.Name) ? leader.Ticker : leader.Name;
            TxtLeaderVal.Text = $"{leader.Ticker}  +{leader.BestLag}일 선행";
        }

        var follower = leadLag.Where(r => r.BestLag < 0)
                              .OrderByDescending(r => r.BestLagCorr)
                              .FirstOrDefault();
        if (follower is not null)
        {
            TxtFollower.Text = string.IsNullOrEmpty(follower.Name) ? follower.Ticker : follower.Name;
            TxtFollowerVal.Text = $"{follower.Ticker}  {follower.BestLag}일 후행";
        }

        // Similarity Summary 텍스트
        var sb = new System.Text.StringBuilder();

        static string DisplayName(CorrelationRow r)
            => string.IsNullOrEmpty(r.Name) ? r.Ticker : r.Name;

        var strong = top50.Where(r => r.Corr60 >= 0.7).Take(3).ToList();
        var moderate = top50.Where(r => r.Corr60 is >= 0.4 and < 0.7).Take(3).ToList();

        if (strong.Count > 0)
        {
            sb.AppendLine($"최근 60일 기준 {baseTicker}는");
            sb.AppendLine($"{string.Join(", ", strong.Select(DisplayName))}와");
            sb.AppendLine("강한 동조성을 보인다.");
            sb.AppendLine();
        }
        if (moderate.Count > 0)
        {
            sb.AppendLine($"{string.Join(", ", moderate.Select(DisplayName))}와");
            sb.AppendLine("중간 수준의 상관관계가 있다.");
            sb.AppendLine();
        }
        if (leader is not null)
        {
            sb.AppendLine($"{DisplayName(leader)}의 움직임을");
            sb.AppendLine($"약 {leader.BestLag}일 후행하는 경향이 있다.");
        }

        TxtSummary.Text = sb.ToString().TrimEnd();
    }

    private void ClearSummary()
    {
        TxtTopCorr.Text = TxtTopCorrVal.Text = "-";
        TxtTopSim.Text = TxtTopSimVal.Text = "-";
        TxtLeader.Text = TxtLeaderVal.Text = "-";
        TxtFollower.Text = TxtFollowerVal.Text = "-";
        TxtSummary.Text = "-";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  DB 헬퍼
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CorrelationView 전용 제외 필터.
    /// BuildStockExcludeFilter와 달리 ExcludePrefStock(우선주)을
    /// 한국 종목(KP/KQ)에만 적용한다.
    /// 미국 종목은 알파벳 ticker라 RIGHT(ticker,1)='0' 조건이 전부 걸러내므로 제외.
    /// </summary>
    private string BuildCorrExcludeFilter()
    {
        var opts = _db.Options();
        var clauses = new List<string>();

        if (opts.ExcludeSpac)
            clauses.Add("s.name NOT LIKE '%스팩%'");

        if (opts.ExcludePrefStock)
            // 한국 종목(KP/KQ)만 끝자리 '0' 필터, 미국 종목은 조건 제외
            clauses.Add("(s.market NOT IN ('KP','KQ') OR RIGHT(s.ticker,1) = '0')");

        if (opts.ExcludeHalted)
            clauses.Add("COALESCE(c.current_price, 0) > 0");

        return clauses.Count > 0
            ? "AND " + string.Join(" AND ", clauses)
            : "";
    }
    private Dictionary<string, (string Name, string Market, string SecurityType, double Rs, double Ret1M, double Upside, double AtrPercent)> GetNameMap()
    {
        var map = new Dictionary<string, (string, string, string, double, double, double, double)>();
        using var conn = _db.OpenNativeConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.ticker, s.name, s.market, s.security_type,
                   COALESCE(c.rs, 0),
                   COALESCE(c.ret_1m, 0),
                   CASE WHEN COALESCE(c.current_price,0) > 0 AND COALESCE(c.target_price,0) > 0
                        THEN ROUND((c.target_price / c.current_price - 1) * 100, 1)
                        ELSE 0 END AS upside,
                   COALESCE(c.atr_percent, 0)
            FROM stocks s
            LEFT JOIN stock_cache c ON c.ticker = s.ticker
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = (
                r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7));
        return map;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  UI 헬퍼
    // ─────────────────────────────────────────────────────────────────────────
    private void ShowLoading(bool show, string msg = "")
    {
        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TxtLoadingMsg.Text = show ? msg : "";
        TxtLoadingDetail.Text = show ? $"종목 {_returnsCache.Count:N0}개 대상" : "";
    }

    private void SetStatus(string msg, string color = "#6C7086")
    {
        TxtStatus.Text = msg;
        StatusChanged?.Invoke(msg, color);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  종목 검색 (ChartView / TargetPriceView 동일 패턴)
    // ─────────────────────────────────────────────────────────────────────────
    private List<StockItem> SearchStocks(string keyword)
    {
        return _db.StockItemList()
            .Select(x => new
            {
                Item = x,
                Score =
                    x.Ticker.Equals(keyword, StringComparison.OrdinalIgnoreCase) ? 100 :
                    x.Name.Equals(keyword, StringComparison.OrdinalIgnoreCase) ? 90 :
                    x.Ticker.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) ? 80 :
                    x.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) ? 70 :
                    x.Ticker.Contains(keyword, StringComparison.OrdinalIgnoreCase) ? 60 :
                    x.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ? 50 :
                    0
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Name)
            .Take(30)
            .Select(x => x.Item)
            .ToList();
    }

    private void SelectStock()
    {
        if (ListSearchResult.SelectedItem is not StockItem item) return;
        PopupSearch.IsOpen = false;
        LoadTicker(item.Ticker);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  이벤트 핸들러
    // ─────────────────────────────────────────────────────────────────────────
    private void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        => LoadTicker(TxtStockSearch.Text.Trim());

    private void TxtStockSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInternalTextChange) return;
        var keyword = TxtStockSearch.Text.Trim();
        if (keyword.Length < 1) { PopupSearch.IsOpen = false; return; }
        var list = SearchStocks(keyword);
        ListSearchResult.ItemsSource = list;
        PopupSearch.IsOpen = list.Count > 0;
    }

    private void TxtStockSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (ListSearchResult.Items.Count > 0)
                {
                    ListSearchResult.Focus();
                    ListSearchResult.SelectedIndex = 0;
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (ListSearchResult.SelectedItem is StockItem)
                    SelectStock();
                else
                {
                    LoadTicker(TxtStockSearch.Text);
                    PopupSearch.IsOpen = false;
                }
                e.Handled = true;
                break;
        }
    }

    private void ListSearchResult_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => SelectStock();

    private void ListSearchResult_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SelectStock();
    }

    // Correlation Grid 더블클릭 → 해당 종목 기준으로 재계산
    private void GridCorrelation_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridCorrelation.SelectedItem is not CorrelationRow row) return;

        _currentTicker = row.Ticker;
        TxtCurrentTicker.Text = row.Ticker;
        TxtCurrentName.Text = row.Name;

        _isInternalTextChange = true;
        TxtStockSearch.Text = string.IsNullOrEmpty(row.Name) ? row.Ticker : row.Name;
        _isInternalTextChange = false;

        _ = RunAnalysisAsync(row.Ticker);
    }

    // 비교대상 필터 체크박스 변경 → 캐시 재사용, 계산만 재실행
    private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;

        _filterKP = ChkKP.IsChecked == true;
        _filterKQ = ChkKQ.IsChecked == true;
        _filterUS = ChkUS.IsChecked == true;
        _filterETF = ChkETF.IsChecked == true;

        if (string.IsNullOrEmpty(_currentTicker)) return;
        _ = RunAnalysisAsync(_currentTicker);
    }
}
