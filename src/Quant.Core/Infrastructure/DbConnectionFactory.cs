// Infrastructure/DbConnectionFactory.cs
using DuckDB.NET.Data;
using System.Data;

namespace Quant.Core.Infrastructure;

public class DbConnectionFactory
{
    private readonly string _dbPath;

    public DbConnectionFactory(string dbPath)
    {
        _dbPath = dbPath;
    }

    public IDbConnection Create()
    {
        var conn = new DuckDBConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }
}
