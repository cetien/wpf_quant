// Models/Watchlist.cs
namespace Quant.Core.Models;

public class Watchlist
{
    public int WatchlistId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}

// Models/WatchlistItem.cs
public class WatchlistItem
{
    public int WatchlistId { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }
    public int Rating { get; set; } = 5;
    public bool IsActive { get; set; } = true;
    public string? TriggerReason { get; set; }
}

// Models/PdfReport.cs
public class PdfReport
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string? Ticker { get; set; }
    public string? Title { get; set; }
    public string? Writer { get; set; }
    public string? Filepath { get; set; }
    public string? FileHash { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Models/StockCache.cs
public class StockCache
{
    public string Ticker { get; set; } = string.Empty;
    public DateOnly LastDate { get; set; }
    public double? Ret1m { get; set; }
    public double? Ret3m { get; set; }
    public double? Ret6m { get; set; }
    public double? Rs { get; set; }
    public double? Beta60d { get; set; }
    public double? Per { get; set; }
    public double? Pbr { get; set; }
    public double? Roe { get; set; }
    public DateTime UpdatedAt { get; set; }
}
