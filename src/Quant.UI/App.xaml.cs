using Microsoft.Extensions.DependencyInjection;
using Quant.Core.Infrastructure;
using Quant.Core.Repositories;
using Quant.Core.Services;
using Quant.UI.Views;
using System.IO;
using System.Windows;

namespace Quant.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "quant", "quant.db");

    private static readonly string MigrationsDir =
        Path.Combine(AppContext.BaseDirectory, "migrations");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        var services = new ServiceCollection();
        RegisterServices(services);
        Services = services.BuildServiceProvider();

        if (Directory.Exists(MigrationsDir))
            Services.GetRequiredService<SchemaInitializer>().Run(MigrationsDir);

        var win = new MainWindow();
        win.Show();
    }

    private static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(_ => new DbConnectionFactory(DbPath));
        services.AddSingleton<SchemaInitializer>();
        services.AddTransient<StockRepository>();
        services.AddTransient<DailyPriceRepository>();
        services.AddSingleton<CacheUpdateService>();
    }
}
