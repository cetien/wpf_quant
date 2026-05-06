// Infrastructure/DbConnectionFactory.cs
using DuckDB.NET.Data;
using System.Data;

namespace Quant.Core.Infrastructure;

/// <summary>
/// Repository 계층이 IDbConnection을 필요로 할 때 사용하는 팩토리.
/// DB 경로는 DbManager에서 가져오므로 직접 지정하지 않는다.
/// </summary>
public class DbConnectionFactory
{
    public IDbConnection Create()
    {
        var conn = new DuckDBConnection($"Data Source={DbManager.DbPath}");
        conn.Open();
        return conn;
    }
}
