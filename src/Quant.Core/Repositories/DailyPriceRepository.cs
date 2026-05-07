// Repositories/DailyPriceRepository.cs
using DuckDB.NET.Data;
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
        using var conn = (DuckDBConnection)_factory.Create();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM daily_prices
            WHERE ticker = $1 AND date BETWEEN $2 AND $3
            ORDER BY date";
        cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
        cmd.Parameters.Add(new DuckDBParameter { Value = from.ToString("yyyy-MM-dd") });
        cmd.Parameters.Add(new DuckDBParameter { Value = to.ToString("yyyy-MM-dd") });
        using var reader = cmd.ExecuteReader();
        var result = new List<DailyPrice>();
        while (reader.Read()) result.Add(MapPrice(reader));
        return result;
    }

    public DateOnly? GetLastDate(string ticker)
    {
        using var conn = (DuckDBConnection)_factory.Create();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(date) FROM daily_prices WHERE ticker = $1";
        cmd.Parameters.Add(new DuckDBParameter { Value = ticker });
        var raw = cmd.ExecuteScalar();
        if (raw is null || raw is DBNull) return null;
        return DateOnly.TryParse(raw.ToString(), out var d) ? d : null;
    }

    public void BulkInsert(IEnumerable<DailyPrice> prices)
    {
        using var conn = (DuckDBConnection)_factory.Create();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO daily_prices
                (ticker, date, open, high, low, close, adj_close, volume, amount)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)";
        for (int i = 0; i < 9; i++)
            cmd.Parameters.Add(new DuckDBParameter());

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
            cmd.Parameters[8].Value = p.Amount.HasValue ? p.Amount.Value : DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private static DailyPrice MapPrice(System.Data.IDataReader r) => new()
    {
        Ticker   = r.GetString(r.GetOrdinal("ticker")),
        Date     = DateOnly.Parse(r.GetString(r.GetOrdinal("date"))),
        Open     = r.GetDouble(r.GetOrdinal("open")),
        High     = r.GetDouble(r.GetOrdinal("high")),
        Low      = r.GetDouble(r.GetOrdinal("low")),
        Close    = r.GetDouble(r.GetOrdinal("close")),
        AdjClose = r.GetDouble(r.GetOrdinal("adj_close")),
        Volume   = r.GetInt64(r.GetOrdinal("volume")),
        Amount   = r.IsDBNull(r.GetOrdinal("amount")) ? null : r.GetInt64(r.GetOrdinal("amount")),
    };
}
