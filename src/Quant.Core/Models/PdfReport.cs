// Models/PdfReport.cs
namespace Quant.Core.Models;

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

