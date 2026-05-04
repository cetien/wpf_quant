// Services/PdfWatcherService.cs
using Dapper;
using Quant.Core.Infrastructure;
using Quant.Core.Models;
using System.Security.Cryptography;

namespace Quant.Core.Services;

/// <summary>
/// 지정 폴더 감시 → 신규 PDF → pdf_reports 등록
/// 기존 BG 수집 프로그램과 병행 가능
/// </summary>
public class PdfWatcherService : IDisposable
{
    private readonly DbConnectionFactory _factory;
    private readonly FileSystemWatcher _watcher;

    public PdfWatcherService(DbConnectionFactory factory, string watchFolder)
    {
        _factory = factory;
        _watcher = new FileSystemWatcher(watchFolder, "*.pdf")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnPdfCreated;
    }

    private void OnPdfCreated(object sender, FileSystemEventArgs e)
    {
        var hash = ComputeHash(e.FullPath);
        using var conn = _factory.Create();
        conn.Execute(@"
            INSERT OR IGNORE INTO pdf_reports (date, filepath, file_hash, created_at)
            VALUES (CURRENT_DATE, @Filepath, @Hash, CURRENT_TIMESTAMP)",
            new { Filepath = e.FullPath, Hash = hash });
    }

    private static string ComputeHash(string path)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(stream));
    }

    public void Dispose() => _watcher.Dispose();
}
