// Repositories/StockRepository.cs
using Dapper;
using Quant.Core.Infrastructure;
using Quant.Core.Models;

namespace Quant.Core.Repositories;

public class StockRepository
{
    private readonly DbConnectionFactory _factory;

    public StockRepository(DbConnectionFactory factory)
    {
        _factory = factory;
    }

    public IEnumerable<Stock> GetAll(bool activeOnly = true)
    {
        using var conn = _factory.Create();
        var sql = "SELECT * FROM stocks" + (activeOnly ? " WHERE is_active = TRUE" : "");
        return conn.Query<Stock>(sql);
    }

    public Stock? GetByTicker(string ticker)
    {
        using var conn = _factory.Create();
        return conn.QueryFirstOrDefault<Stock>(
            "SELECT * FROM stocks WHERE ticker = @Ticker", new { Ticker = ticker });
    }

    public void Upsert(Stock s)
    {
        using var conn = _factory.Create();
        conn.Execute(@"
            INSERT INTO stocks (ticker, name, market, security_type, listed_date, rating, is_active, updated_at)
            VALUES (@Ticker, @Name, @Market, @SecurityType, @ListedDate, @Rating, @IsActive, CURRENT_TIMESTAMP)
            ON CONFLICT (ticker) DO UPDATE SET
                name = excluded.name,
                market = excluded.market,
                security_type = excluded.security_type,
                listed_date = excluded.listed_date,
                rating = excluded.rating,
                is_active = excluded.is_active,
                updated_at = CURRENT_TIMESTAMP", s);
    }
}
