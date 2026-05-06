using Microsoft.Extensions.DependencyInjection;
using Quant.Core.Infrastructure;
using Quant.Core.Repositories;
using Quant.Core.Services;
using Quant.UI.Views;
using System.Windows;

namespace Quant.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        RegisterServices(services);
        Services = services.BuildServiceProvider();

        // DbManager 생성자에서 migrations 자동 실행
        _ = Services.GetRequiredService<DbManager>();

        var win = new MainWindow();
        win.Show();
    }

    private static void RegisterServices(IServiceCollection services)
    {
        // DbManager: 경로·연결·스키마 초기화 모두 내부 처리
        services.AddSingleton<DbManager>();

        // Repository 계층은 DbConnectionFactory 경유 (기존 코드 호환)
        services.AddSingleton<DbConnectionFactory>();
        services.AddSingleton<SchemaInitializer>();
        services.AddTransient<StockRepository>();
        services.AddTransient<DailyPriceRepository>();
        services.AddSingleton<CacheUpdateService>();
    }
}
