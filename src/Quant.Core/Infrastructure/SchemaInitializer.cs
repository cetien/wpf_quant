// Infrastructure/SchemaInitializer.cs
using Dapper;

namespace Quant.Core.Infrastructure;

/// <summary>
/// Migration SQL 파일을 순서대로 실행하여 스키마를 보장한다.
/// DbManager 생성자에서 자동 호출되므로 직접 호출은 불필요.
/// App.xaml.cs 등에서 명시적으로 재실행이 필요한 경우에만 사용.
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
        foreach (var file in Directory.GetFiles(migrationsDir, "*.sql").OrderBy(f => f))
            conn.Execute(File.ReadAllText(file));
    }
}
