// Models/AppOptions.cs
namespace Quant.Core.Models;

/// <summary>
/// options 테이블에서 로드한 앱 설정 값 묶음.
/// DbManager.LoadOptions() / SaveOptions()로 읽고 쓴다.
/// </summary>
public class AppOptions
{
    /// <summary>종목 조회 시 최근조회 history에 자동 추가</summary>
    public bool AutoAppendHistory { get; set; } = true;

    /// <summary>PDF 리포트 감시 폴더 경로. 빈 문자열이면 미설정.</summary>
    public string ReportPdfFolder { get; set; } = string.Empty;

    /// <summary>마지막으로 수집한 리포트 날짜. 이 날짜 이전 파일은 skip.</summary>
    public DateOnly? LastReportDate { get; set; } = null;
}
