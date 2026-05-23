// Models/AppOptions.cs
using Chaen;

namespace Quant.Core.Models;

/// <summary>
/// options 테이블에서 로드한 앱 설정 값 묶음.
/// DbManager.LoadOptions() / SaveOptions()로 읽고 쓴다.
/// </summary>
public class AppOptions
{
    /// <summary>종목 조회 시 최근조회 history에 자동 추가</summary>
    public bool AutoAppendHistory { get; set; } = true;
    public bool QueryFilterPreferred { get; set; } = true;
    public bool QueryFilterCovered { get; set; } = true;
    public bool QueryFilterCheap { get; set; } = true;

    /// <summary>종목명에 '스팩' 포함된 종목 전역 제외</summary>
    public bool ExcludeSpac { get; set; } = true;
    /// <summary>우선주(ticker 끝자리 ≠ '0') 전역 제외</summary>
    public bool ExcludePrefStock { get; set; } = true;
    /// <summary>거래정지 종목(stock_cache.current_price = 0) 전역 제외</summary>
    public bool ExcludeHalted { get; set; } = true;

    /// <summary>PDF 리포트 감시 폴더 경로. 빈 문자열이면 미설정.</summary>
    public string ReportPdfFolder { get; set; } = string.Empty;

    /// <summary>마지막으로 수집한 리포트 날짜. 이 날짜 이전 파일은 skip.</summary>
    public DateOnly? LastReportDate { get; set; } = null;


    /////////// don't save to DB ///////////
    /// <summary>마지막으로 조회한 종목 티커.</summary>
    public string LastTicker { get; set; } = string.Empty;

    /// <summary>마지막으로 조회한 종목 그룹.</summary>
    public string LastGroup { get; set; } = string.Empty;


    public Dictionary<string, string> ToDictionary()
    {
        return new()
        {
            ["auto_append_history"] = ChaenHelper.BoolToString(AutoAppendHistory),
            ["query_filter_preferred"] = ChaenHelper.BoolToString(QueryFilterPreferred),
            ["query_filter_covered"] = ChaenHelper.BoolToString(QueryFilterCovered),
            ["query_filter_cheap"] = ChaenHelper.BoolToString(QueryFilterCheap),
            ["exclude_spac"] = ChaenHelper.BoolToString(ExcludeSpac),
            ["exclude_pref_stock"] = ChaenHelper.BoolToString(ExcludePrefStock),
            ["exclude_halted"] = ChaenHelper.BoolToString(ExcludeHalted),
            ["last_report_date"] = ChaenHelper.DateToString(LastReportDate),
            ["report_pdf_folder"] = ReportPdfFolder ?? "",
        };
    }

    public static AppOptions FromDictionary(IReadOnlyDictionary<string, string> dict)
    {
        return new AppOptions
        {
            AutoAppendHistory = ChaenHelper.GetBool(dict, "auto_append_history"),
            QueryFilterPreferred = ChaenHelper.GetBool(dict, "query_filter_preferred"),
            ExcludeHalted = ChaenHelper.GetBool(dict, "exclude_halted"),
            QueryFilterCovered = ChaenHelper.GetBool(dict, "query_filter_covered"),
            QueryFilterCheap = ChaenHelper.GetBool(dict, "query_filter_cheap"),
            ExcludeSpac = ChaenHelper.GetBool(dict, "exclude_spac"),
            ExcludePrefStock = ChaenHelper.GetBool(dict, "exclude_pref_stock"),
            LastReportDate = dict.TryGetValue("last_report_date", out var s) && DateOnly.TryParse(s, out var d) ? d : null,
            ReportPdfFolder = dict.TryGetValue("report_pdf_folder", out var folder) ? folder : "",
        };
    }
}
