// Repositories/DailyPriceRepository.cs
using Dapper;
using Quant.Core.Infrastructure;
using Quant.Core.Models;

namespace Quant.Core.Repositories;

public class DailyPriceRepository
{
    private readonly DbConnectionFactory _factory;

    public DailyPriceRepository(DbConnectionFactory factory)
    {
        _factory = factory;
    }

    public IEnumerable<DailyPrice> GetRange(string ticker, DateOnly from, DateOnly to)
    {
        using var conn = _factory.Create();
        return conn.Query<DailyPrice>(
            "SELECT * FROM daily_prices WHERE ticker = @Ticker AND date BETWEEN @From AND @To ORDER BY date",
            new { Ticker = ticker, From = from, To = to });
    }

    public DateOnly? GetLastDate(string ticker)
    {
        using var conn = _factory.Create();
        return conn.QueryFirstOrDefault<DateOnly?>(
            "SELECT MAX(date) FROM daily_prices WHERE ticker = @Ticker",
            new { Ticker = ticker });
    }

    public void BulkInsert(IEnumerable<DailyPrice> prices)
    {
        using var conn = _factory.Create();
        conn.Execute(@"
            INSERT OR IGNORE INTO daily_prices
                (ticker, date, open, high, low, close, adj_close, volume, amount)
            VALUES
                (@Ticker, @Date, @Open, @High, @Low, @Close, @AdjClose, @Volume, @Amount)",
            prices);
    }
}
