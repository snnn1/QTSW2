using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Read-only P&L aggregation layer (derived from execution journals).
/// Provides fast queries for stream-level, day-level, and portfolio-level P&L summaries.
/// 
/// Design Principle: Journals are authoritative, aggregation is convenience.
/// All aggregation is derived from journal files - no separate ledger maintained.
/// </summary>
public sealed class StreamPnLAggregator
{
    private readonly string _journalDir;
    private readonly RobotLogger _log;

    public StreamPnLAggregator(string projectRoot, RobotLogger log)
    {
        _journalDir = Path.Combine(projectRoot, "data", "execution_journals");
        _log = log;
    }

    /// <summary>
    /// Get P&L summary for a specific stream on a specific trading date.
    /// </summary>
    public StreamPnLSummary GetStreamSummary(string tradingDate, string stream)
    {
        var pattern = $"{tradingDate}_{stream}_*.json";
        var entries = LoadJournalEntries(pattern);
        
        var completedTrades = entries.Where(e => e.TradeCompleted).ToList();
        
        return new StreamPnLSummary
        {
            TradingDate = tradingDate,
            Stream = stream,
            TotalPnLNet = completedTrades.Sum(e => e.RealizedPnLNet ?? 0),
            TotalPnLGross = completedTrades.Sum(e => e.RealizedPnLGross ?? 0),
            TotalPnLPoints = completedTrades.Sum(e => e.RealizedPnLPoints ?? 0),
            CompletedTrades = completedTrades.Count,
            WinCount = completedTrades.Count(e => e.RealizedPnLNet > 0),
            LossCount = completedTrades.Count(e => e.RealizedPnLNet < 0),
            BreakEvenCount = completedTrades.Count(e => e.RealizedPnLNet == 0),
            TotalSlippageDollars = completedTrades.Sum(e => e.SlippageDollars ?? 0),
            TotalCommission = completedTrades.Sum(e => e.Commission ?? 0),
            TotalFees = completedTrades.Sum(e => e.Fees ?? 0),
                Trades = completedTrades.Select(e => new TradeSummary
                {
                    IntentId = e.IntentId,
                    Direction = e.Direction ?? "UNKNOWN",
                    EntryAvgFillPrice = e.EntryAvgFillPrice ?? 0,
                    ExitAvgFillPrice = e.ExitAvgFillPrice ?? 0,
                    EntryFilledQuantityTotal = e.EntryFilledQuantityTotal,
                    ExitFilledQuantityTotal = e.ExitFilledQuantityTotal,
                    RealizedPnLNet = e.RealizedPnLNet ?? 0,
                    RealizedPnLGross = e.RealizedPnLGross ?? 0,
                    RealizedPnLPoints = e.RealizedPnLPoints ?? 0,
                    CompletionReason = e.CompletionReason,
                    CompletedAtUtc = e.CompletedAtUtc
                }).ToList()
        };
    }

    /// <summary>
    /// Get P&L summary for all streams on a specific trading date.
    /// </summary>
    public DayPnLSummary GetDaySummary(string tradingDate)
    {
        var pattern = $"{tradingDate}_*.json";
        var entries = LoadJournalEntries(pattern);
        
        var completedTrades = entries.Where(e => e.TradeCompleted).ToList();
        
        // Group by stream
        var streamSummaries = completedTrades
            .GroupBy(e => e.Stream)
            .Select(g => new StreamPnLSummary
            {
                TradingDate = tradingDate,
                Stream = g.Key,
                TotalPnLNet = g.Sum(e => e.RealizedPnLNet ?? 0),
                TotalPnLGross = g.Sum(e => e.RealizedPnLGross ?? 0),
                TotalPnLPoints = g.Sum(e => e.RealizedPnLPoints ?? 0),
                CompletedTrades = g.Count(),
                WinCount = g.Count(e => e.RealizedPnLNet > 0),
                LossCount = g.Count(e => e.RealizedPnLNet < 0),
                BreakEvenCount = g.Count(e => e.RealizedPnLNet == 0),
                TotalSlippageDollars = g.Sum(e => e.SlippageDollars ?? 0),
                TotalCommission = g.Sum(e => e.Commission ?? 0),
                TotalFees = g.Sum(e => e.Fees ?? 0),
                Trades = g.Select(e => new TradeSummary
                {
                    IntentId = e.IntentId,
                    Direction = e.Direction ?? "UNKNOWN",
                    EntryAvgFillPrice = e.EntryAvgFillPrice ?? 0,
                    ExitAvgFillPrice = e.ExitAvgFillPrice ?? 0,
                    EntryFilledQuantityTotal = e.EntryFilledQuantityTotal,
                    ExitFilledQuantityTotal = e.ExitFilledQuantityTotal,
                    RealizedPnLNet = e.RealizedPnLNet ?? 0,
                    RealizedPnLGross = e.RealizedPnLGross ?? 0,
                    RealizedPnLPoints = e.RealizedPnLPoints ?? 0,
                    CompletionReason = e.CompletionReason,
                    CompletedAtUtc = e.CompletedAtUtc
                }).ToList()
            })
            .OrderBy(s => s.Stream)
            .ToList();

        return new DayPnLSummary
        {
            TradingDate = tradingDate,
            TotalPnLNet = completedTrades.Sum(e => e.RealizedPnLNet ?? 0),
            TotalPnLGross = completedTrades.Sum(e => e.RealizedPnLGross ?? 0),
            TotalPnLPoints = completedTrades.Sum(e => e.RealizedPnLPoints ?? 0),
            TotalCompletedTrades = completedTrades.Count,
            TotalWinCount = completedTrades.Count(e => e.RealizedPnLNet > 0),
            TotalLossCount = completedTrades.Count(e => e.RealizedPnLNet < 0),
            TotalBreakEvenCount = completedTrades.Count(e => e.RealizedPnLNet == 0),
            TotalSlippageDollars = completedTrades.Sum(e => e.SlippageDollars ?? 0),
            TotalCommission = completedTrades.Sum(e => e.Commission ?? 0),
            TotalFees = completedTrades.Sum(e => e.Fees ?? 0),
            StreamSummaries = streamSummaries
        };
    }

    /// <summary>
    /// Get P&L summary for a date range (portfolio-level).
    /// </summary>
    public PortfolioPnLSummary GetPortfolioSummary(DateOnly startDate, DateOnly endDate)
    {
        var allEntries = new List<ExecutionJournalEntry>();
        
        // Scan all journal files in date range
        if (!Directory.Exists(_journalDir))
        {
            return new PortfolioPnLSummary
            {
                StartDate = startDate.ToString("yyyy-MM-dd"),
                EndDate = endDate.ToString("yyyy-MM-dd"),
                TotalPnLNet = 0,
                TotalPnLGross = 0,
                TotalPnLPoints = 0,
                TotalCompletedTrades = 0,
                DaySummaries = new List<DayPnLSummary>()
            };
        }

        var files = Directory.GetFiles(_journalDir, "*.json");
        foreach (var file in files)
        {
            try
            {
                var fileName = Path.GetFileName(file);
                // Extract trading date from filename: YYYY-MM-DD_STREAM_INTENTID.json
                var parts = fileName.Split('_');
                if (parts.Length >= 3)
                {
                    if (DateOnly.TryParse(parts[0], out var tradingDate))
                    {
                        if (tradingDate >= startDate && tradingDate <= endDate)
                        {
                            var json = File.ReadAllText(file);
                            var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                            if (entry != null && entry.TradeCompleted)
                            {
                                allEntries.Add(entry);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Skip corrupted files
            }
        }

        // Group by trading date
        var daySummaries = allEntries
            .GroupBy(e => e.TradingDate ?? "")
            .Select(g => new DayPnLSummary
            {
                TradingDate = g.Key,
                TotalPnLNet = g.Sum(e => e.RealizedPnLNet ?? 0),
                TotalPnLGross = g.Sum(e => e.RealizedPnLGross ?? 0),
                TotalPnLPoints = g.Sum(e => e.RealizedPnLPoints ?? 0),
                TotalCompletedTrades = g.Count(),
                TotalWinCount = g.Count(e => e.RealizedPnLNet > 0),
                TotalLossCount = g.Count(e => e.RealizedPnLNet < 0),
                TotalBreakEvenCount = g.Count(e => e.RealizedPnLNet == 0),
                TotalSlippageDollars = g.Sum(e => e.SlippageDollars ?? 0),
                TotalCommission = g.Sum(e => e.Commission ?? 0),
                TotalFees = g.Sum(e => e.Fees ?? 0),
                StreamSummaries = g.GroupBy(e => e.Stream)
                    .Select(sg => new StreamPnLSummary
                    {
                        TradingDate = g.Key,
                        Stream = sg.Key,
                        TotalPnLNet = sg.Sum(e => e.RealizedPnLNet ?? 0),
                        TotalPnLGross = sg.Sum(e => e.RealizedPnLGross ?? 0),
                        TotalPnLPoints = sg.Sum(e => e.RealizedPnLPoints ?? 0),
                        CompletedTrades = sg.Count(),
                        WinCount = sg.Count(e => e.RealizedPnLNet > 0),
                        LossCount = sg.Count(e => e.RealizedPnLNet < 0),
                        BreakEvenCount = sg.Count(e => e.RealizedPnLNet == 0),
                        TotalSlippageDollars = sg.Sum(e => e.SlippageDollars ?? 0),
                        TotalCommission = sg.Sum(e => e.Commission ?? 0),
                        TotalFees = sg.Sum(e => e.Fees ?? 0),
                        Trades = sg.Select(e => new TradeSummary
                        {
                            IntentId = e.IntentId,
                            Direction = e.Direction ?? "UNKNOWN",
                            EntryAvgFillPrice = e.EntryAvgFillPrice ?? 0,
                            ExitAvgFillPrice = e.ExitAvgFillPrice ?? 0,
                            EntryFilledQuantityTotal = e.EntryFilledQuantityTotal,
                            ExitFilledQuantityTotal = e.ExitFilledQuantityTotal,
                            RealizedPnLNet = e.RealizedPnLNet ?? 0,
                            RealizedPnLGross = e.RealizedPnLGross ?? 0,
                            RealizedPnLPoints = e.RealizedPnLPoints ?? 0,
                            CompletionReason = e.CompletionReason,
                            CompletedAtUtc = e.CompletedAtUtc
                        }).ToList()
                    })
                    .OrderBy(s => s.Stream)
                    .ToList()
            })
            .OrderBy(d => d.TradingDate)
            .ToList();

        return new PortfolioPnLSummary
        {
            StartDate = startDate.ToString("yyyy-MM-dd"),
            EndDate = endDate.ToString("yyyy-MM-dd"),
            TotalPnLNet = allEntries.Sum(e => e.RealizedPnLNet ?? 0),
            TotalPnLGross = allEntries.Sum(e => e.RealizedPnLGross ?? 0),
            TotalPnLPoints = allEntries.Sum(e => e.RealizedPnLPoints ?? 0),
            TotalCompletedTrades = allEntries.Count,
            TotalWinCount = allEntries.Count(e => e.RealizedPnLNet > 0),
            TotalLossCount = allEntries.Count(e => e.RealizedPnLNet < 0),
            TotalBreakEvenCount = allEntries.Count(e => e.RealizedPnLNet == 0),
            TotalSlippageDollars = allEntries.Sum(e => e.SlippageDollars ?? 0),
            TotalCommission = allEntries.Sum(e => e.Commission ?? 0),
            TotalFees = allEntries.Sum(e => e.Fees ?? 0),
            DaySummaries = daySummaries
        };
    }

    /// <summary>
    /// Load journal entries matching the given pattern.
    /// </summary>
    private List<ExecutionJournalEntry> LoadJournalEntries(string pattern)
    {
        var entries = new List<ExecutionJournalEntry>();
        
        if (!Directory.Exists(_journalDir))
        {
            return entries;
        }

        try
        {
            var files = Directory.GetFiles(_journalDir, pattern);
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }
                catch
                {
                    // Skip corrupted files
                }
            }
        }
        catch
        {
            // Fail-safe: return empty list on error
        }

        return entries;
    }
}

/// <summary>
/// P&L summary for a single stream.
/// </summary>
public sealed class StreamPnLSummary
{
    public string TradingDate { get; set; } = "";
    public string Stream { get; set; } = "";
    public decimal TotalPnLNet { get; set; }
    public decimal TotalPnLGross { get; set; }
    public decimal TotalPnLPoints { get; set; }
    public int CompletedTrades { get; set; }
    public int WinCount { get; set; }
    public int LossCount { get; set; }
    public int BreakEvenCount { get; set; }
    public decimal TotalSlippageDollars { get; set; }
    public decimal? TotalCommission { get; set; }
    public decimal? TotalFees { get; set; }
    public List<TradeSummary> Trades { get; set; } = new();
}

/// <summary>
/// P&L summary for a single trading day (all streams).
/// </summary>
public sealed class DayPnLSummary
{
    public string TradingDate { get; set; } = "";
    public decimal TotalPnLNet { get; set; }
    public decimal TotalPnLGross { get; set; }
    public decimal TotalPnLPoints { get; set; }
    public int TotalCompletedTrades { get; set; }
    public int TotalWinCount { get; set; }
    public int TotalLossCount { get; set; }
    public int TotalBreakEvenCount { get; set; }
    public decimal TotalSlippageDollars { get; set; }
    public decimal? TotalCommission { get; set; }
    public decimal? TotalFees { get; set; }
    public List<StreamPnLSummary> StreamSummaries { get; set; } = new();
}

/// <summary>
/// P&L summary for a date range (portfolio-level).
/// </summary>
public sealed class PortfolioPnLSummary
{
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public decimal TotalPnLNet { get; set; }
    public decimal TotalPnLGross { get; set; }
    public decimal TotalPnLPoints { get; set; }
    public int TotalCompletedTrades { get; set; }
    public int TotalWinCount { get; set; }
    public int TotalLossCount { get; set; }
    public int TotalBreakEvenCount { get; set; }
    public decimal TotalSlippageDollars { get; set; }
    public decimal? TotalCommission { get; set; }
    public decimal? TotalFees { get; set; }
    public List<DayPnLSummary> DaySummaries { get; set; } = new();
}

/// <summary>
/// Summary of a single completed trade.
/// </summary>
public sealed class TradeSummary
{
    public string IntentId { get; set; } = "";
    public string Direction { get; set; } = "";
    public decimal EntryAvgFillPrice { get; set; }
    public decimal ExitAvgFillPrice { get; set; }
    public int EntryFilledQuantityTotal { get; set; }
    public int ExitFilledQuantityTotal { get; set; }
    public decimal RealizedPnLNet { get; set; }
    public decimal RealizedPnLGross { get; set; }
    public decimal RealizedPnLPoints { get; set; }
    public string? CompletionReason { get; set; }
    public string? CompletedAtUtc { get; set; }
}
