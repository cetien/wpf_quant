// Models/Stock.cs
namespace Quant.Core.Models;

public class Stock
{
    public string Ticker { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty;       // KP | KQ | NYSE
    public string SecurityType { get; set; } = string.Empty; // stock | index | etf
    public DateOnly? ListedDate { get; set; }
    public int Rating { get; set; } = 5;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

public class StockItem
{
    public string Ticker { get; set; } = "";
    public string Name { get; set; } = "";
}

// Models/Group.cs
public class StockGroup
{
    public int GroupId { get; set; }
    public string Kind { get; set; } = string.Empty; // sector | theme
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Rating { get; set; } = 5;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Models/StockGroupMap.cs
public class StockGroupMap
{
    public string Ticker { get; set; } = string.Empty;
    public int GroupId { get; set; }
    public double Weight { get; set; } = 1.0;
    public DateTime CreatedAt { get; set; }
}

// Models/StockCache.cs
public class StockCache : Stock
{
    //public string Ticker { get; set; } = "";
    //public string Name { get; set; } = "";
    //public string Market { get; set; } = "";
    //public string SecurityType { get; set; } = "";
    //public int Rating { get; set; }
    //public bool IsActive { get; set; } = true;

    public bool IsETF => SecurityType == "ETF";
    public bool IsStock => SecurityType == "stock";
    public bool IsIndex => SecurityType == "index";

    public bool IsKOSPI => Market == "KP";
    public bool IsKOSDAQ => Market == "KQ";
    public bool IsNYSE => Market == "NYSE";

    public DateOnly AsofDate { get; set; }
    public double CurrentPrice { get; set; }

    public double Ret1M { get; set; }
    public double Ret3M { get; set; }
    public double Ret6M { get; set; }
    public double Ret1Y { get; set; }
    public double Rs { get; set; }

    public double Per { get; set; }
    public double Pbr { get; set; }
    public double Roe { get; set; }
    public double Eps { get; set; }

    public double Atr14 { get; set; }
    public double AtrPercent { get; set; }
    public double High52W { get; set; }
    public double Low52W { get; set; }
    public double VolumeAvg20D { get; set; }
    public double DistanceFromHigh { get; set; }

    public double Ma20 { get; set; }
    public double Ma60 { get; set; }
    public double Ma120 { get; set; }
    public double VolumeRatio { get; set; }
    public double High60D { get; set; }
    public double High120D { get; set; }
    public new DateTime UpdatedAt { get; set; }

    public int ReportCount { get; set; }
    public double TargetPrice { get; set; }   // pdf_reports + stock_tp 최신값
}
