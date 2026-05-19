using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Quant.UI;

/// <summary>
/// DataTable(DBNull 포함) / double? 바인딩용 범용 포맷 Converter.
/// ConverterParameter 로 포맷 문자열 지정 (기본 "F2").
/// price = 0 인 거래정지 종목은 "-" 표시.
/// </summary>
[ValueConversion(typeof(object), typeof(string))]
public sealed class DbNullDoubleConverter : IValueConverter
{
    public static readonly DbNullDoubleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || value == DBNull.Value) return "-";

        double d;
        if (value is double dbl)       d = dbl;
        else if (value is float f)     d = f;
        else if (value is decimal dec) d = (double)dec;
        else if (!double.TryParse(value.ToString(), NumberStyles.Any, culture, out d)) return "-";

        var fmt = parameter as string ?? "F2";

        // price=0 → 거래정지 종목: vol 포맷만 0 허용, 나머지는 "-"
        if (d == 0.0 && fmt != "vol") return "-";

        return fmt == "vol" ? (d == 0.0 ? "-" : d.ToString("N0", culture))
                            : d.ToString(fmt, culture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}


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

    public static readonly Regex TickerRegex = new("^[A-Za-z0-9]{6}$", RegexOptions.Compiled);

    public static void HighlightButton(Button btn)
    {
        btn.Foreground = (System.Windows.Media.Brush)(btn.TryFindResource("AccentBlueBrush")
            ?? System.Windows.Media.Brushes.LightBlue);
    }

    public static void HighlightButton(Button? active, IEnumerable<Button> all)
    {
        foreach (var b in all)
            b.Foreground = System.Windows.Media.Brushes.Gray;
        if (active is null) return;
        HighlightButton(active);
    }
}
