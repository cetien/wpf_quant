// Services/CacheUpdateService.cs
using Dapper;
using Quant.Core.Infrastructure;

namespace Quant.Core.Services;

/// <summary>
/// stock_cache 갱신 서비스
/// DuckDB 직접 집계로 대체 결정 시 이 서비스 제거 가능
/// </summary>
public class CacheUpdateService
{
    private readonly DbConnectionFactory _factory;

    public CacheUpdateService(DbConnectionFactory factory)
    {
        _factory = factory;
    }

    public void UpdateAll()
    {
        using var conn = _factory.Create();

        // ret_1m, ret_3m, ret_6m, rs (KOSPI 대비) 일괄 갱신
        // RS 기준: KOSPI ticker = 'KP001' (또는 실제 사용 ticker로 교체)
        conn.Execute(@"
            INSERT INTO stock_cache (ticker, last_date, ret_1m, ret_3m, ret_6m, rs, updated_at)
            SELECT
                p.ticker,
                MAX(p.date)                                         AS last_date,
                (MAX(p.adj_close) FILTER (WHERE p.date >= CURRENT_DATE - INTERVAL '30 days')
                 / FIRST(p.adj_close ORDER BY p.date) - 1) * 100   AS ret_1m,
                (MAX(p.adj_close) FILTER (WHERE p.date >= CURRENT_DATE - INTERVAL '90 days')
                 / FIRST(p.adj_close ORDER BY p.date) - 1) * 100   AS ret_3m,
                (MAX(p.adj_close) FILTER (WHERE p.date >= CURRENT_DATE - INTERVAL '180 days')
                 / FIRST(p.adj_close ORDER BY p.date) - 1) * 100   AS ret_6m,
                NULL                                                AS rs,  -- TODO: KOSPI 대비 계산
                CURRENT_TIMESTAMP
            FROM daily_prices p
            GROUP BY p.ticker
            ON CONFLICT (ticker) DO UPDATE SET
                last_date  = excluded.last_date,
                ret_1m     = excluded.ret_1m,
                ret_3m     = excluded.ret_3m,
                ret_6m     = excluded.ret_6m,
                updated_at = CURRENT_TIMESTAMP");
    }
}
