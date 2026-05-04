// Infrastructure/SchemaInitializer.cs
using Dapper;
using System.Reflection;

namespace Quant.Core.Infrastructure;

/// <summary>
/// 앱 시작 시 migration SQL 파일을 순서대로 실행하여 스키마 보장
/// </summary>
public class SchemaInitializer
{
    private readonly DbConnectionFactory _factory;

    public SchemaInitializer(DbConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Run(string migrationsDir)
    {
        using var conn = _factory.Create();
        var files = Directory.GetFiles(migrationsDir, "*.sql")
                             .OrderBy(f => f)
                             .ToList();
        foreach (var file in files)
        {
            var sql = File.ReadAllText(file);
            conn.Execute(sql);
        }
    }
}
