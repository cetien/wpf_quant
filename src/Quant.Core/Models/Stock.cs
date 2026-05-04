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

// Models/Group.cs
public class Group
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
