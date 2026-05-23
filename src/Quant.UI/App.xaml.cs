using Microsoft.Extensions.DependencyInjection;

using Quant.Core.Infrastructure;
using Quant.Core.Models;
using Quant.Core.Repositories;
using Quant.UI.Views;

using System.Windows;

namespace Quant.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static DbManager DB => DbManager.Instance;
    public static AppOptions Config() => DB.Options();

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

        // Repository 계층 (필요 시사용)
        services.AddTransient<StockRepository>();
        services.AddTransient<DailyPriceRepository>();
        // CacheUpdateService 제거 — DbManager.RebuildStockCache()로 통합
        // DbConnectionFactory, SchemaInitializer 제거 — 미사용
    }
}
