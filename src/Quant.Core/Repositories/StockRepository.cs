// Repositories/StockRepository.cs
using DuckDB.NET.Data;
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
        using var conn = (DuckDBConnection)_factory.Create();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM stocks" + (activeOnly ? " WHERE is_active = TRUE" : "");
        using var reader = cmd.ExecuteReader();
        var result = new List<Stock>();
        while (reader.Read()) result.Add(MapStock(reader));
        return result;
    }

    public Stock? GetByTicker(string ticker)
    {
        using var conn = (DuckDBConnection)_factory.Create();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM stocks WHERE ticker = $1";
        cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapStock(reader) : null;
    }

    private static Stock MapStock(System.Data.IDataReader r) => new()
    {
        Ticker       = r.GetString(r.GetOrdinal("ticker")),
        Name         = r.IsDBNull(r.GetOrdinal("name"))         ? string.Empty : r.GetString(r.GetOrdinal("name")),
        Market       = r.IsDBNull(r.GetOrdinal("market"))        ? string.Empty : r.GetString(r.GetOrdinal("market")),
        SecurityType = r.IsDBNull(r.GetOrdinal("security_type")) ? string.Empty : r.GetString(r.GetOrdinal("security_type")),
        ListedDate   = r.IsDBNull(r.GetOrdinal("listed_date"))   ? null
                       : DateOnly.Parse(r.GetString(r.GetOrdinal("listed_date"))),
        Rating       = r.IsDBNull(r.GetOrdinal("rating"))        ? 5 : r.GetInt32(r.GetOrdinal("rating")),
        IsActive     = r.GetBoolean(r.GetOrdinal("is_active")),
        UpdatedAt    = r.IsDBNull(r.GetOrdinal("updated_at"))    ? default : r.GetDateTime(r.GetOrdinal("updated_at")),
    };

    public void Upsert(Stock s)
    {
        using var conn = (DuckDBConnection)_factory.Create();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO stocks (ticker, name, market, security_type, listed_date, rating, is_active, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, CURRENT_TIMESTAMP)
            ON CONFLICT (ticker) DO UPDATE SET
                name          = excluded.name,
                market        = excluded.market,
                security_type = excluded.security_type,
                listed_date   = excluded.listed_date,
                rating        = excluded.rating,
                is_active     = excluded.is_active,
                updated_at    = CURRENT_TIMESTAMP";
        cmd.Parameters.Add(new DuckDBParameter { Value = s.Ticker });
        cmd.Parameters.Add(new DuckDBParameter { Value = s.Name });
        cmd.Parameters.Add(new DuckDBParameter { Value = s.Market });
        cmd.Parameters.Add(new DuckDBParameter { Value = s.SecurityType });
        cmd.Parameters.Add(new DuckDBParameter { Value = s.ListedDate is null ? DBNull.Value : s.ListedDate.Value.ToString("yyyy-MM-dd") });
        cmd.Parameters.Add(new DuckDBParameter { Value = s.Rating });
        cmd.Parameters.Add(new DuckDBParameter { Value = s.IsActive });
        cmd.ExecuteNonQuery();
    }
}
