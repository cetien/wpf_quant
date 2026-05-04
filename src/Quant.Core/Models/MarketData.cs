// Models/DailyPrice.cs
namespace Quant.Core.Models;

public class DailyPrice
{
    public string Ticker { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double AdjClose { get; set; }
    public long Volume { get; set; }
    public long? Amount { get; set; }   // pykrx 제공, yfinance 미제공
}

// Models/Supply.cs
public class Supply
{
    public string Ticker { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public long? InstNetBuy { get; set; }       // 기관 순매수 (주)
    public long? ForeignNetBuy { get; set; }    // 외인 순매수 (주)
    public long? InstNetAmount { get; set; }    // 기관 순매수 (금액)
    public long? ForeignNetAmount { get; set; } // 외인 순매수 (금액)
    public DateTime IngestedAt { get; set; }
}

// Models/Fundamentals.cs
public class Fundamentals
{
    public string Ticker { get; set; } = string.Empty;
    public DateOnly ReportDate { get; set; }
    public DateOnly? AnnounceDate { get; set; }
    public string? FiscalQuarter { get; set; }
    public double? Eps { get; set; }
    public double? Per { get; set; }
    public double? Pbr { get; set; }
    public double? Roe { get; set; }
    public long? Revenue { get; set; }
    public long? OperatingIncome { get; set; }
    public long? NetIncome { get; set; }
    public double? DebtRatio { get; set; }
    public DateTime IngestedAt { get; set; }
}
