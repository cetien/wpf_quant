using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Quant.UI;

public static class Helpers
{
    public static (bool ok, string message) OpenWithChrome(string? filepath)
    {
        if (string.IsNullOrWhiteSpace(filepath)) return (false, "파일 경로 없음");
        if (!File.Exists(filepath)) return (false, $"파일 없음: {filepath}");

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        ];

        var chrome = candidates.FirstOrDefault(File.Exists);
        try
        {
            if (chrome is not null) Process.Start(chrome, $"\"{filepath}\"");
            else Process.Start(new ProcessStartInfo(filepath) { UseShellExecute = true });
            return (true, $"열기: {Path.GetFileName(filepath)}");
        }
        catch
        {
            return (false, $"열기 오류: {Path.GetFileName(filepath)}");
        }
    }

    public static void OpenExternalLink(string? ticker)
    {
        if (string.IsNullOrEmpty(ticker)) return;
        var url = $"https://finance.naver.com/item/coinfo.naver?code={ticker}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch {}
    }
}
