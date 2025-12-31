"""
Statistics calculation and reporting for Master Matrix.

CRITICAL GUARANTEES (NEW):
- Performance/risk stats computed on EXECUTED TRADES ONLY (Win, Loss, BE, TIME)
- final_allowed is for UI visibility, not stats sample (unless include_filtered_executed=False)
- TIME results treated as executed trades everywhere
- Consistent units: per-trade metrics use trade sequence, Sharpe/Sortino use daily PnL series
- NoTrade excluded from performance stats
"""

import logging
from typing import Dict, Optional
import pandas as pd
import numpy as np

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------
# Definitions (AUTHORITATIVE)
# ---------------------------------------------------------------------

def _normalize_result(result_value) -> str:
    """
    Normalize Result once: ResultNorm = uppercase(trim(Result))
    
    Args:
        result_value: Result column value (can be str, NaN, None, etc.)
        
    Returns:
        Normalized result string (uppercase, stripped)
    """
    if pd.isna(result_value) or result_value is None:
        return ""
    return str(result_value).strip().upper()


def _is_executed_trade(result_norm: str) -> bool:
    """
    Check if result represents an executed trade.
    
    is_executed_trade = ResultNorm in {"WIN","LOSS","BE","BREAKEVEN","TIME"}
    """
    return result_norm in {"WIN", "LOSS", "BE", "BREAKEVEN", "TIME"}


def _is_notrade(result_norm: str) -> bool:
    """
    Check if result is NoTrade.
    
    is_notrade = ResultNorm == "NOTRADE"
    """
    return result_norm == "NOTRADE"


# ---------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------

def _normalize_results(df: pd.DataFrame) -> pd.DataFrame:
    """
    Normalize Result column for safe comparisons.
    Adds ResultNorm column (uppercase, stripped).
    """
    df = df.copy()
    df["ResultNorm"] = df["Result"].apply(_normalize_result)
    return df


def _ensure_final_allowed(df: pd.DataFrame) -> pd.DataFrame:
    """
    Ensure final_allowed exists and is boolean.
    """
    df = df.copy()
    if "final_allowed" in df.columns:
        df["final_allowed"] = df["final_allowed"].astype(bool)
    else:
        df["final_allowed"] = True
    return df


def _ensure_profit_column(df: pd.DataFrame) -> pd.DataFrame:
    """
    Ensure Profit column exists and is numeric.
    """
    df = df.copy()
    if "Profit" not in df.columns:
        df["Profit"] = 0.0
    df["Profit"] = pd.to_numeric(df["Profit"], errors='coerce').fillna(0.0)
    return df


def _ensure_profit_dollars_column(df: pd.DataFrame) -> pd.DataFrame:
    """
    Ensure ProfitDollars column exists. If not, create from Profit using contract multipliers.
    """
    df = df.copy()
    if "ProfitDollars" not in df.columns:
        # Try to compute from Profit and Instrument
        contract_values = {
            "ES": 50.0,
            "MES": 5.0,
            "NQ": 20.0,
            "MNQ": 2.0,
            "YM": 5.0,
            "MYM": 0.5,
            "RTY": 50.0,
        }
        
        def get_contract_value(instrument_str):
            if pd.isna(instrument_str) or instrument_str is None:
                return 50.0  # Default to ES
            inst_str = str(instrument_str).strip().upper()
            # Remove year suffix if present (e.g., "ES2" -> "ES")
            base_inst = inst_str.rstrip("0123456789")
            return contract_values.get(base_inst, 50.0)
        
        df["ProfitDollars"] = df.apply(
            lambda row: (row.get("Profit", 0.0) or 0.0) * get_contract_value(row.get("Instrument")),
            axis=1
        )
    df["ProfitDollars"] = pd.to_numeric(df["ProfitDollars"], errors='coerce').fillna(0.0)
    return df


def _ensure_date_column(df: pd.DataFrame) -> pd.DataFrame:
    """
    Ensure trade_date or Date column exists as datetime.
    """
    df = df.copy()
    if "trade_date" not in df.columns and "Date" in df.columns:
        df["trade_date"] = pd.to_datetime(df["Date"], errors='coerce')
    if "trade_date" in df.columns:
        df["trade_date"] = pd.to_datetime(df["trade_date"], errors='coerce')
    return df


# ---------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------

def calculate_summary_stats(
    df: pd.DataFrame,
    include_filtered_executed: bool = True
) -> Dict:
    """
    Calculate and log summary statistics for the master matrix.
    
    NEW BEHAVIOR:
    - Performance stats computed on EXECUTED TRADES ONLY (Win, Loss, BE, TIME)
    - final_allowed is for visibility/filtering reporting, not stats sample
    - include_filtered_executed: If True (default), include filtered executed trades in stats.
                                 If False, only include executed trades where final_allowed == True.
    
    Returns structured payload with:
    - sample_counts: Row counts by category
    - performance_trade_metrics: Per-trade metrics (computed on executed trades)
    - performance_daily_metrics: Daily aggregation metrics (computed on executed trades)
    """
    if df.empty:
        logger.warning("No data for summary statistics")
        return _empty_stats()
    
    # Prepare DataFrame
    df = _ensure_final_allowed(df)
    df = _normalize_results(df)
    df = _ensure_profit_column(df)
    df = _ensure_profit_dollars_column(df)
    df = _ensure_date_column(df)
    
    # Add is_executed_trade flag
    df["is_executed_trade"] = df["ResultNorm"].apply(_is_executed_trade)
    df["is_notrade"] = df["ResultNorm"].apply(_is_notrade)
    
    # ========================================================================
    # SAMPLE COUNTS
    # ========================================================================
    total_rows = len(df)
    filtered_rows = len(df[~df["final_allowed"]])
    allowed_rows = len(df[df["final_allowed"]])
    
    executed_df = df[df["is_executed_trade"]]
    executed_trades_total = len(executed_df)
    executed_trades_allowed = len(executed_df[executed_df["final_allowed"]])
    executed_trades_filtered = len(executed_df[~executed_df["final_allowed"]])
    
    notrade_total = len(df[df["is_notrade"]])
    
    sample_counts = {
        "total_rows": int(total_rows),
        "filtered_rows": int(filtered_rows),
        "allowed_rows": int(allowed_rows),
        "executed_trades_total": int(executed_trades_total),
        "executed_trades_allowed": int(executed_trades_allowed),
        "executed_trades_filtered": int(executed_trades_filtered),
        "notrade_total": int(notrade_total),
    }
    
    # ========================================================================
    # CALCULATE DAY COUNTS
    # - executed_trading_days: from ALL executed trades (for reporting)
    # - allowed_trading_days: from stats sample (days actually traded in stats)
    # ========================================================================
    executed_day_counts = _calculate_day_counts(executed_df)
    executed_trading_days = executed_day_counts["executed_trading_days"]
    
    # ========================================================================
    # SELECT STATS SAMPLE: Executed trades (optionally filtered by final_allowed)
    # ========================================================================
    if include_filtered_executed:
        stats_sample = executed_df
    else:
        stats_sample = executed_df[executed_df["final_allowed"]]
    
    if len(stats_sample) == 0:
        logger.warning("No executed trades in stats sample")
        return {
            "sample_counts": sample_counts,
            "performance_trade_metrics": _empty_trade_metrics(),
            "performance_daily_metrics": _empty_daily_metrics(),
            "day_counts": {"executed_trading_days": executed_trading_days, "allowed_trading_days": 0},
        }
    
    # ========================================================================
    # ACTIVE TRADING DAYS: Count unique days from stats sample ONLY
    # A day is active if and only if it contains ≥1 trade in the stats sample
    # When toggle OFF: Days with only filtered trades are NOT counted
    # When toggle ON: All days with executed trades are counted
    # ========================================================================
    active_trading_days = _count_unique_days(stats_sample)
    stats_sample_trade_count = len(stats_sample)
    
    # ========================================================================
    # PERFORMANCE TRADE METRICS (per-trade, computed on executed trades only)
    # ========================================================================
    performance_trade_metrics = _calculate_trade_metrics(stats_sample)
    
    # ========================================================================
    # PERFORMANCE DAILY METRICS (daily aggregation, computed on executed trades only)
    # Behavioral averages use active_trading_days (from stats sample ONLY)
    # Risk metrics (Sharpe/Sortino/Calmar) use daily PnL series from stats sample
    # ========================================================================
    performance_daily_metrics = _calculate_daily_metrics(
        stats_sample,
        active_trading_days=active_trading_days,
        total_profit=performance_trade_metrics["total_profit"],
        stats_sample_trade_count=stats_sample_trade_count
    )
    
    # ========================================================================
    # LOGGING
    # ========================================================================
    logger.info("=" * 80)
    logger.info("MASTER MATRIX SUMMARY STATISTICS")
    logger.info("=" * 80)
    logger.info(f"Total Rows: {total_rows} | Allowed: {allowed_rows} | Filtered: {filtered_rows}")
    logger.info(f"Executed Trades: Total={executed_trades_total} | Allowed={executed_trades_allowed} | Filtered={executed_trades_filtered}")
    logger.info(f"NoTrade Rows: {notrade_total}")
    logger.info(f"Stats Sample: {len(stats_sample)} executed trades (include_filtered={include_filtered_executed})")
    logger.info("Performance Trade Metrics:")
    logger.info(f"  Wins: {performance_trade_metrics['wins']} | Losses: {performance_trade_metrics['losses']} | BE: {performance_trade_metrics['be']} | TIME: {performance_trade_metrics['time']}")
    logger.info(f"  Win Rate: {performance_trade_metrics['win_rate']:.1f}%")
    logger.info(f"  Total Profit: {performance_trade_metrics['total_profit']:.2f}")
    logger.info(f"  Avg Profit / Trade: {performance_trade_metrics['mean_pnl_per_trade']:.2f}")
    logger.info(f"  Risk-Reward: {performance_trade_metrics['rr_ratio']:.2f}")
    logger.info("Performance Daily Metrics:")
    logger.info(f"  Executed Trading Days: {executed_trading_days} (all executed trades, for reference)")
    logger.info(f"  Active Trading Days: {active_trading_days} (days with ≥1 trade in stats sample)")
    logger.info(f"  Stats Sample: {stats_sample_trade_count} trades across {active_trading_days} active days")
    logger.info(f"  Avg Trades Per Active Day: {performance_daily_metrics['avg_trades_per_day']:.2f}")
    logger.info(f"  Profit Per Active Day: {performance_daily_metrics['profit_per_day']:.2f}")
    logger.info(f"  Sharpe Ratio: {performance_daily_metrics['sharpe_ratio']:.2f}")
    logger.info(f"  Sortino Ratio: {performance_daily_metrics['sortino_ratio']:.2f}")
    logger.info("=" * 80)
    
    return {
        "sample_counts": sample_counts,
        "performance_trade_metrics": performance_trade_metrics,
        "performance_daily_metrics": performance_daily_metrics,
        "day_counts": {
            "executed_trading_days": executed_trading_days,  # All executed trades (for reference)
            "allowed_trading_days": active_trading_days,  # Active days from stats sample (for behavioral metrics)
        },
    }


def _empty_stats() -> Dict:
    """Return empty stats structure."""
    return {
        "sample_counts": {
            "total_rows": 0,
            "filtered_rows": 0,
            "allowed_rows": 0,
            "executed_trades_total": 0,
            "executed_trades_allowed": 0,
            "executed_trades_filtered": 0,
            "notrade_total": 0,
        },
        "performance_trade_metrics": _empty_trade_metrics(),
        "performance_daily_metrics": _empty_daily_metrics(),
    }


def _empty_trade_metrics() -> Dict:
    """Return empty trade metrics structure."""
    return {
        "total_profit": 0.0,
        "wins": 0,
        "losses": 0,
        "be": 0,
        "time": 0,
        "win_rate": 0.0,
        "profit_factor": 0.0,
        "rr_ratio": 0.0,
        "mean_pnl_per_trade": 0.0,
        "median_pnl_per_trade": 0.0,
        "stddev_pnl_per_trade": 0.0,
        "max_consecutive_losses": 0,
        "max_drawdown": 0.0,
        "var95": 0.0,
        "cvar95": 0.0,
    }


def _empty_daily_metrics() -> Dict:
    """Return empty daily metrics structure."""
    return {
        "executed_trading_days": 0,  # Days in daily PnL series (for reference)
        "allowed_trading_days": 0,  # Active trading days (for behavioral averages)
        "avg_trades_per_day": 0.0,
        "profit_per_day": 0.0,
        "profit_per_week": 0.0,
        "profit_per_month": 0.0,
        "profit_per_year": 0.0,
        "sharpe_ratio": 0.0,
        "sortino_ratio": 0.0,
        "calmar_ratio": 0.0,
        "time_to_recovery_days": 0,
        "monthly_return_stddev": 0.0,
    }


def _calculate_trade_metrics(executed_df: pd.DataFrame) -> Dict:
    """
    Calculate per-trade performance metrics from executed trades DataFrame.
    
    Args:
        executed_df: DataFrame containing ONLY executed trades (Win, Loss, BE, TIME)
        
    Returns:
        Dictionary of trade metrics
    """
    if executed_df.empty:
        return _empty_trade_metrics()
    
    # Count by result type
    wins = len(executed_df[executed_df["ResultNorm"] == "WIN"])
    losses = len(executed_df[executed_df["ResultNorm"] == "LOSS"])
    be = len(executed_df[executed_df["ResultNorm"].isin(["BE", "BREAKEVEN"])])
    time = len(executed_df[executed_df["ResultNorm"] == "TIME"])
    
    # Win rate (wins / (wins + losses), excluding BE and TIME)
    win_loss_trades = wins + losses
    win_rate = (wins / win_loss_trades * 100) if win_loss_trades > 0 else 0.0
    
    # Profit metrics (use ProfitDollars if available, else Profit)
    profit_col = "ProfitDollars" if "ProfitDollars" in executed_df.columns else "Profit"
    profits = executed_df[profit_col].fillna(0.0)
    
    total_profit = float(profits.sum())
    
    # Profit factor (gross_profit / abs(gross_loss))
    gross_profit = profits[profits > 0].sum()
    gross_loss = abs(profits[profits < 0].sum())
    profit_factor = gross_profit / gross_loss if gross_loss > 0 else (float('inf') if gross_profit > 0 else 0.0)
    
    # PnL statistics
    mean_pnl = float(profits.mean())
    median_pnl = float(profits.median())
    stddev_pnl = float(profits.std()) if len(profits) > 1 else 0.0
    
    # Max consecutive losses
    max_consecutive_losses = 0
    current_streak = 0
    for result in executed_df["ResultNorm"]:
        if result == "LOSS":
            current_streak += 1
            max_consecutive_losses = max(max_consecutive_losses, current_streak)
        else:
            current_streak = 0
    
    # Max drawdown (trade equity curve)
    cumulative_profit = profits.cumsum()
    running_max = cumulative_profit.expanding().max()
    drawdown = cumulative_profit - running_max
    max_drawdown = float(abs(drawdown.min())) if not drawdown.empty else 0.0
    
    # VaR and CVaR (95%)
    sorted_profits = profits.sort_values().values
    if len(sorted_profits) > 0:
        var95_idx = int(len(sorted_profits) * 0.05)
        if var95_idx < len(sorted_profits):
            var95 = float(sorted_profits[var95_idx])
            cvar95_values = sorted_profits[:var95_idx + 1]
            cvar95 = float(cvar95_values.mean()) if len(cvar95_values) > 0 else 0.0
        else:
            var95 = float(sorted_profits[0]) if len(sorted_profits) > 0 else 0.0
            cvar95 = var95
    else:
        var95 = 0.0
        cvar95 = 0.0
    
    # Risk-Reward ratio (avg_win / avg_loss)
    winning_trades_df = executed_df[executed_df["ResultNorm"] == "WIN"]
    losing_trades_df = executed_df[executed_df["ResultNorm"] == "LOSS"]
    avg_win = float(winning_trades_df[profit_col].mean()) if not winning_trades_df.empty else 0.0
    avg_loss = abs(float(losing_trades_df[profit_col].mean())) if not losing_trades_df.empty else 0.0
    rr_ratio = avg_win / avg_loss if avg_loss > 0 else (float('inf') if avg_win > 0 else 0.0)
    
    return {
        "total_profit": round(total_profit, 2),
        "wins": int(wins),
        "losses": int(losses),
        "be": int(be),
        "time": int(time),
        "win_rate": round(win_rate, 1),
        "profit_factor": round(profit_factor, 2),
        "rr_ratio": round(rr_ratio, 2),
        "mean_pnl_per_trade": round(mean_pnl, 2),
        "median_pnl_per_trade": round(median_pnl, 2),
        "stddev_pnl_per_trade": round(stddev_pnl, 2),
        "max_consecutive_losses": int(max_consecutive_losses),
        "max_drawdown": round(max_drawdown, 2),
        "var95": round(var95, 2),
        "cvar95": round(cvar95, 2),
    }


def _calculate_day_counts(executed_df: pd.DataFrame) -> Dict[str, int]:
    """
    Calculate executed trading day count from ALL executed trades.
    
    Definition:
    - Executed Trading Day: A calendar day with ≥1 is_executed_trade == True
      (includes filtered and allowed trades)
    
    Args:
        executed_df: DataFrame containing ONLY executed trades, must have trade_date column
        
    Returns:
        Dictionary with 'executed_trading_days'
    """
    if executed_df.empty:
        return {"executed_trading_days": 0}
    
    if "trade_date" not in executed_df.columns:
        logger.warning("trade_date column missing, cannot calculate day counts")
        return {"executed_trading_days": 0}
    
    # Remove rows with invalid dates
    valid_dates = executed_df["trade_date"].notna()
    daily_df = executed_df[valid_dates].copy()
    
    if daily_df.empty:
        return {"executed_trading_days": 0}
    
    # Group by trading day (date only, not time)
    daily_df["trade_date_only"] = daily_df["trade_date"].dt.date
    
    # Executed trading days: all days with at least one executed trade
    executed_days = set(daily_df["trade_date_only"].unique())
    executed_trading_days = len(executed_days)
    
    return {
        "executed_trading_days": executed_trading_days,
    }


def _count_unique_days(df: pd.DataFrame) -> int:
    """
    Count unique trading days in a DataFrame.
    
    Args:
        df: DataFrame with trade_date column
        
    Returns:
        Count of unique trading days
    """
    if df.empty:
        return 0
    
    if "trade_date" not in df.columns:
        return 0
    
    # Remove rows with invalid dates
    valid_dates = df["trade_date"].notna()
    daily_df = df[valid_dates].copy()
    
    if daily_df.empty:
        return 0
    
    # Group by trading day (date only, not time)
    daily_df["trade_date_only"] = daily_df["trade_date"].dt.date
    unique_days = set(daily_df["trade_date_only"].unique())
    
    return len(unique_days)


def _calculate_daily_metrics(
    executed_df: pd.DataFrame,
    active_trading_days: int,
    total_profit: float,
    stats_sample_trade_count: int
) -> Dict:
    """
    Calculate daily aggregation performance metrics from executed trades DataFrame.
    
    Uses Option 1: Actual calendar months for monthly metrics.
    
    CRITICAL: Behavioral averages (profit_per_day, avg_trades_per_day) use active_trading_days,
    which is computed from the stats sample ONLY. A day is active if and only if it contains
    ≥1 trade in the stats sample. Days with zero trades in the stats sample are NOT counted.
    
    Risk metrics (Sharpe/Sortino/Calmar) use daily PnL series from executed_df (stats sample).
    
    Args:
        executed_df: DataFrame containing ONLY executed trades in stats sample, must have trade_date column
        active_trading_days: Count of active trading days (unique days in stats sample, for behavioral averages)
        total_profit: Total profit from stats sample (for profit_per_day calculation)
        stats_sample_trade_count: Count of trades in stats sample (for avg_trades_per_day)
        
    Returns:
        Dictionary of daily metrics
    """
    if executed_df.empty:
        return _empty_daily_metrics()
    
    # Ensure trade_date exists
    if "trade_date" not in executed_df.columns:
        logger.warning("trade_date column missing, cannot calculate daily metrics")
        return _empty_daily_metrics()
    
    # Remove rows with invalid dates
    valid_dates = executed_df["trade_date"].notna()
    daily_df = executed_df[valid_dates].copy()
    
    if daily_df.empty:
        return _empty_daily_metrics()
    
    # Profit column (use ProfitDollars if available, else Profit)
    profit_col = "ProfitDollars" if "ProfitDollars" in daily_df.columns else "Profit"
    
    # Group by trading day (date only, not time) - for risk metrics (daily PnL series)
    # OPTIMIZED: Use sort=False for faster groupby when order doesn't matter
    daily_df["trade_date_only"] = daily_df["trade_date"].dt.date
    daily_pnl = daily_df.groupby("trade_date_only", sort=False)[profit_col].sum().reset_index()
    daily_pnl.columns = ["date", "pnl"]
    
    # Behavioral averages use active_trading_days (days with ≥1 trade in stats sample)
    # avg_trades_per_active_day = stats_sample_trade_count / active_trading_days
    avg_trades_per_day = (stats_sample_trade_count / active_trading_days) if active_trading_days > 0 else 0.0
    
    # profit_per_active_day = total_profit / active_trading_days
    profit_per_day = (total_profit / active_trading_days) if active_trading_days > 0 else 0.0
    
    # Sharpe/Sortino ratios (annualized using 252 trading days)
    daily_returns = daily_pnl["pnl"].values
    mean_daily_return = float(np.mean(daily_returns))
    std_daily_return = float(np.std(daily_returns)) if len(daily_returns) > 1 else 0.0
    
    trading_days_per_year = 252
    annualized_return = mean_daily_return * trading_days_per_year
    annualized_volatility = std_daily_return * np.sqrt(trading_days_per_year)
    sharpe_ratio = annualized_return / annualized_volatility if annualized_volatility > 0 else 0.0
    
    # Sortino: only downside volatility
    downside_returns = daily_returns[daily_returns < 0]
    downside_std = float(np.std(downside_returns)) if len(downside_returns) > 1 else 0.0
    annualized_downside_vol = downside_std * np.sqrt(trading_days_per_year)
    sortino_ratio = annualized_return / annualized_downside_vol if annualized_downside_vol > 0 else 0.0
    
    # Time to recovery (trading days, not calendar days)
    # Count as difference in trading day index, not calendar day difference
    cumulative_pnl = daily_pnl["pnl"].cumsum()
    running_max = cumulative_pnl.expanding().max()
    drawdown = cumulative_pnl - running_max
    
    # Find longest recovery period (from drawdown start to new peak)
    time_to_recovery_days = 0
    in_drawdown = False
    drawdown_start_idx = None
    
    for idx in range(len(drawdown)):
        if drawdown.iloc[idx] < 0:
            if not in_drawdown:
                in_drawdown = True
                drawdown_start_idx = idx
        else:
            if in_drawdown and drawdown_start_idx is not None:
                recovery_days = idx - drawdown_start_idx
                time_to_recovery_days = max(time_to_recovery_days, recovery_days)
                in_drawdown = False
                drawdown_start_idx = None
    
    # Monthly return std dev (Option 1: actual calendar months)
    # OPTIMIZED: Use sort=False for faster groupby
    daily_df["year_month"] = daily_df["trade_date"].dt.to_period("M")
    monthly_pnl = daily_df.groupby("year_month", sort=False)[profit_col].sum()
    monthly_return_stddev = float(monthly_pnl.std()) if len(monthly_pnl) > 1 else 0.0
    
    # Calmar ratio (annualized return / max drawdown)
    # Annualized return from daily aggregation (use mean daily return from PnL series for consistency with Sharpe/Sortino)
    mean_daily_return_for_annualization = float(np.mean(daily_returns)) if len(daily_returns) > 0 else 0.0
    annual_return_from_daily = mean_daily_return_for_annualization * trading_days_per_year
    # Max drawdown from daily cumulative PnL series
    cumulative_pnl_for_calmar = daily_pnl["pnl"].cumsum()
    running_max_daily = cumulative_pnl_for_calmar.expanding().max()
    drawdown_daily = cumulative_pnl_for_calmar - running_max_daily
    max_drawdown_from_daily = float(abs(drawdown_daily.min())) if not drawdown_daily.empty else 0.0
    calmar_ratio = annual_return_from_daily / max_drawdown_from_daily if max_drawdown_from_daily > 0 else 0.0
    
    # Projected metrics (from profit_per_day)
    profit_per_week = profit_per_day * 5
    profit_per_month = profit_per_day * 21
    profit_per_year = profit_per_day * 252
    
    return {
        "executed_trading_days": int(len(daily_pnl)),  # Days in stats sample daily PnL series (for reference)
        "allowed_trading_days": int(active_trading_days),  # Active trading days (used for behavioral averages)
        "avg_trades_per_day": round(avg_trades_per_day, 2),
        "profit_per_day": round(profit_per_day, 2),
        "profit_per_week": round(profit_per_week, 2),
        "profit_per_month": round(profit_per_month, 2),
        "profit_per_year": round(profit_per_year, 2),
        "sharpe_ratio": round(sharpe_ratio, 2),
        "sortino_ratio": round(sortino_ratio, 2),
        "calmar_ratio": round(calmar_ratio, 2),
        "time_to_recovery_days": int(time_to_recovery_days),
        "monthly_return_stddev": round(monthly_return_stddev, 2),
    }


def calculate_stream_stats(
    df: pd.DataFrame,
    include_filtered_executed: bool = True
) -> Dict[str, Dict]:
    """
    Calculate per-stream statistics.
    
    NEW BEHAVIOR:
    - Stats computed on executed trades only
    - include_filtered_executed controls whether filtered executed trades are included
    """
    df = _ensure_final_allowed(df)
    df = _normalize_results(df)
    df = _ensure_profit_column(df)
    df = _ensure_profit_dollars_column(df)
    df["is_executed_trade"] = df["ResultNorm"].apply(_is_executed_trade)
    
    stream_stats: Dict[str, Dict] = {}
    
    for stream in sorted(df["Stream"].unique()):
        stream_df = df[df["Stream"] == stream]
        executed_df = stream_df[stream_df["is_executed_trade"]]
        
        if include_filtered_executed:
            stats_sample = executed_df
        else:
            stats_sample = executed_df[executed_df["final_allowed"]]
        
        total_rows = len(stream_df)
        filtered_rows = len(stream_df[~stream_df["final_allowed"]])
        allowed_rows = len(stream_df[stream_df["final_allowed"]])
        
        if len(stats_sample) == 0:
            stream_stats[stream] = {
                "total_rows": total_rows,
                "filtered_rows": int(filtered_rows),
                "allowed_rows": int(allowed_rows),
                "executed_trades": 0,
                "wins": 0,
                "losses": 0,
                "win_rate": 0.0,
                "profit": 0.0,
            }
            continue
        
        wins = len(stats_sample[stats_sample["ResultNorm"] == "WIN"])
        losses = len(stats_sample[stats_sample["ResultNorm"] == "LOSS"])
        win_loss = wins + losses
        win_rate = (wins / win_loss * 100) if win_loss > 0 else 0.0
        
        profit_col = "ProfitDollars" if "ProfitDollars" in stats_sample.columns else "Profit"
        profit = float(stats_sample[profit_col].sum())
        
        stream_stats[stream] = {
            "total_rows": total_rows,
            "filtered_rows": int(filtered_rows),
            "allowed_rows": int(allowed_rows),
            "executed_trades": len(stats_sample),
            "wins": int(wins),
            "losses": int(losses),
            "win_rate": round(win_rate, 1),
            "profit": round(profit, 2),
        }
    
    return stream_stats
