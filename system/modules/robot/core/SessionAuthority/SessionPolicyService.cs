using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QTSW2.Robot.Core.SessionAuthority;

/// <summary>
/// Loads optional internal session calendar policy. When a row exists for (trading_date, calendar_group),
/// the engine materializes authoritative <see cref="SessionCloseResult"/> for that day (holiday or early close).
/// </summary>
public sealed class SessionPolicyService
{
    private readonly SessionCalendarDocument _doc;
    private readonly string _sourcePath;

    private SessionPolicyService(SessionCalendarDocument doc, string sourcePath)
    {
        _doc = doc;
        _sourcePath = sourcePath;
    }

    public string SourcePath => _sourcePath;

    public static SessionPolicyService? TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<SessionCalendarDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (doc == null)
                return null;
            return new SessionPolicyService(doc, path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// When the calendar defines an override for this day and group, builds <paramref name="result"/>.
    /// Returns false when there is no row (caller should use NT cache / spec fallback).
    /// </summary>
    public bool TryBuildSessionClose(
        DateOnly tradingDate,
        string calendarGroup,
        TimeService time,
        ParitySpec? spec,
        int fallbackBufferSeconds,
        string barsInstrument,
        out SessionCloseResult result)
    {
        result = new SessionCloseResult();
        var rows = _doc.Days;
        if (rows == null || rows.Count == 0)
            return false;

        var dayStr = tradingDate.ToString("yyyy-MM-dd");
        SessionCalendarDayRow? match = null;
        foreach (var r in rows)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.TradingDate)) continue;
            if (!string.Equals(r.TradingDate.Trim(), dayStr, StringComparison.Ordinal)) continue;
            var g = r.CalendarGroup?.Trim() ?? "";
            if (!string.Equals(g, calendarGroup, StringComparison.OrdinalIgnoreCase))
                continue;
            match = r;
            break;
        }

        if (match == null)
            return false;

        var kind = (match.Kind ?? "NORMAL").Trim().ToUpperInvariant();
        if (kind == "HOLIDAY")
        {
            result = new SessionCloseResult
            {
                HasSession = false,
                FailureReason = "HOLIDAY",
                BarsInstrument = barsInstrument,
                BufferSeconds = 0
            };
            return true;
        }

        var closeHhmm = !string.IsNullOrWhiteSpace(match.MarketCloseChicago)
            ? match.MarketCloseChicago!.Trim()
            : spec?.entry_cutoff?.market_close_time;
        var reopenHhmm = !string.IsNullOrWhiteSpace(match.MarketReopenChicago)
            ? match.MarketReopenChicago!.Trim()
            : SessionTimingPolicy.ResolveMarketReopenTime(spec);

        if (string.IsNullOrWhiteSpace(closeHhmm))
            closeHhmm = "16:00";
        if (string.IsNullOrWhiteSpace(reopenHhmm))
            reopenHhmm = SessionTimingPolicy.DefaultMarketReopenTime;

        if (kind == "NORMAL" &&
            string.IsNullOrWhiteSpace(match.MarketCloseChicago) &&
            string.IsNullOrWhiteSpace(match.MarketReopenChicago))
        {
            // Explicit NORMAL row without times: same as "no policy row" — do not override NT/spec.
            return false;
        }

        try
        {
            var closeChicago = time.ConstructChicagoTime(tradingDate, closeHhmm);
            var closeUtc = time.ConvertChicagoToUtc(closeChicago);
            var flattenTriggerUtc = SessionTimingPolicy.ResolveForcedFlattenTriggerUtc(tradingDate, closeUtc, time, spec, out var effectiveLeadSeconds);
            var reopenChicago = time.ConstructChicagoTime(tradingDate, reopenHhmm);
            var reopenUtc = time.ConvertChicagoToUtc(reopenChicago);
            result = new SessionCloseResult
            {
                HasSession = true,
                FlattenTriggerUtc = flattenTriggerUtc,
                ResolvedSessionCloseUtc = closeUtc,
                NextSessionBeginUtc = reopenUtc,
                BufferSeconds = effectiveLeadSeconds,
                BarsInstrument = barsInstrument
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Diagnostic: distinct groups referenced in the file.</summary>
    public IReadOnlyList<string> ListedCalendarGroups()
    {
        if (_doc.Days == null) return Array.Empty<string>();
        return _doc.Days
            .Select(d => d?.CalendarGroup?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }
}
