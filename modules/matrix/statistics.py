"""
Statistics calculation and reporting for Master Matrix.

CRITICAL GUARANTEES:
================================================================================
FILTERING AFFECTS ALL METRICS (RISK AND BEHAVIORAL):
- All metrics follow the include_filtered_executed toggle:
  → If include_filtered_executed == True: Metrics computed on ALL executed trades (executed_all)
  → If include_filtered_executed == False: Metrics computed on ALLOWED executed trades only (executed_selected)

RISK METRICS (now follow filtering):
- Sharpe ratio: Computed on executed_selected (follows toggle)
- Sortino ratio: Computed on executed_selected (follows toggle)
- Calmar ratio: Computed on executed_selected (follows toggle)
- Daily volatility: Computed on executed_selected (follows toggle)
- Daily max drawdown: Computed on executed_selected (follows toggle)
- Time-to-recovery: Computed on executed_selected (follows toggle)
- Monthly return std dev: Computed on executed_selected (follows toggle)

BEHAVIORAL METRICS (follow filtering):
- Win rate: Computed on executed_selected (follows toggle)
- Avg profit per trade: Computed on executed_selected
- Avg trades per day: Computed on executed_selected / active_trading_days
- Profit per active day: Computed on executed_selected / active_trading_days
- Profit projections (week/month/year): Computed on executed_selected / active_trading_days

DATASET DEFINITIONS:
- executed_all: ALL rows where is_executed_trade == True (includes filtered + allowed)
  
- executed_selected: 
  → If include_filtered_executed == True: Same as executed_all
  → If include_filtered_executed == False: executed_all where final_allowed == True
  → Used for ALL metrics (risk and behavioral) - follows the toggle

DAY COUNT:
- active_trading_days: Derived from executed_selected (activity-based)
- All metrics use active_trading_days for day-based calculations
================================================================================

ADDITIONAL GUARANTEES:
- Performance/risk stats computed on EXECUTED TRADES ONLY (Win, Loss, BE, TIME)
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
    
    NOTE: Returns copy for backward compatibility. Use _normalize_results_inplace for better performance.
    """
    df = df.copy()
    df["ResultNorm"] = df["Result"].apply(_normalize_result)
    return df


def _normalize_results_inplace(df: pd.DataFrame) -> None:
    """
    Normalize Result column for safe comparisons (in-place).
    Adds ResultNorm column (uppercase, stripped).
    """
    df["ResultNorm"] = df["Result"].apply(_normalize_result)


def _ensure_final_allowed(df: pd.DataFrame) -> pd.DataFrame:
    """
    Ensure final_allowed exists and is boolean.
    
    NOTE: Returns copy for backward compatibility. Use _ensure_final_allowed_inplace for better performance.
    """
    df = df.copy()
    if "final_allowed" in df.columns:
        df["final_allowed"] = df["final_allowed"].astype(bool)
    else:
        df["final_allowed"] = True
    return df


def _ensure_final_allowed_inplace(df: pd.DataFrame) -> None:
    """
    Ensure final_allowed exists and is boolean (in-place).
    """
    if "final_allowed" in df.columns:
        df["final_allowed"] = df["final_allowed"].astype(bool)
    else:
        df["final_allowed"] = True


def _ensure_profit_column(df: pd.DataFrame) -> pd.DataFrame:
    """
    Ensure Profit column exists and is numeric.
    
    NOTE: Returns copy for backward compatibility. Use _ensure_profit_column_inplace for better performance.
    """
    df = df.copy()
    if "Profit" not in df.columns:
        df["Profit"] = 0.0
    df["Profit"] = pd.to_numeric(df["Profit"], errors='coerce').fillna(0.0)
    return df


def _ensure_profit_column_inplace(df: pd.DataFrame) -> None:
    """
    Ensure Profit column exists and is numeric (in-place).
    """
    if "Profit" not in df.columns:
        df["Profit"] = 0.0
    df["Profit"] = pd.to_numeric(df["Profit"], errors='coerce').fillna(0.0)


def _ensure_profit_dollars_column(df: pd.DataFrame, contract_multiplier: float = 1.0) -> pd.DataFrame:
    """
    Ensure ProfitDollars column exists. ALWAYS recompute from Profit to ensure contract_multiplier is applied correctly.
    
    Args:
        df: DataFrame to process
        contract_multiplier: Multiplier for contract size (e.g., 2.0 for trading 2 contracts)
    """
    df = df.copy()
    
    # Complete contract value map (dollars per point)
    contract_values = {
        "ES": 50.0,
        "MES": 5.0,
        "NQ": 10.0,
        "MNQ": 2.0,
        "YM": 5.0,
        "MYM": 0.5,
        "RTY": 50.0,
        "CL": 1000.0,  # Crude Oil
        "NG": 10000.0,  # Natural Gas
        "GC": 100.0,  # Gold
    }
    
    def get_contract_value(instrument_str):
        if pd.isna(instrument_str) or instrument_str is None:
            return 50.0  # Default to ES
        inst_str = str(instrument_str).strip().upper()
        # Remove trailing digits if present (e.g., "ES2" -> "ES", "NQ1" -> "NQ")
        base_inst = inst_str.rstrip("0123456789")
        contract_val = contract_values.get(base_inst, 50.0)
        # Debug logging for NQ streams to verify contract value
        if base_inst == "NQ" and contract_val != 10.0:
            logger.warning(f"NQ contract value mismatch: got {contract_val}, expected 10.0 for instrument {instrument_str}")
        return contract_val
    
    # ALWAYS recompute ProfitDollars from Profit to ensure contract_multiplier is applied correctly
    # This ensures that even if ProfitDollars exists in the DataFrame (from previous calculations),
    # we always use the current multiplier value
    df["ProfitDollars"] = df.apply(
        lambda row: (row.get("Profit", 0.0) or 0.0) * get_contract_value(row.get("Instrument")) * contract_multiplier,
        axis=1
    )
    
    df["ProfitDollars"] = pd.to_numeric(df["ProfitDollars"], errors='coerce').fillna(0.0)
    return df


def _ensure_profit_dollars_column_inplace(df: pd.DataFrame, contract_multiplier: float = 1.0) -> None:
    """
    Ensure ProfitDollars column exists (in-place). ALWAYS recompute from Profit to ensure contract_multiplier is applied correctly.
    
    Args:
        df: DataFrame to process (modified in-place)
        contract_multiplier: Multiplier for contract size (e.g., 2.0 for trading 2 contracts)
    """
    # Complete contract value map (dollars per point)
    contract_values = {
        "ES": 50.0,
        "MES": 5.0,
        "NQ": 10.0,
        "MNQ": 2.0,
        "YM": 5.0,
        "MYM": 0.5,
        "RTY": 50.0,
        "CL": 1000.0,  # Crude Oil
        "NG": 10000.0,  # Natural Gas
        "GC": 100.0,  # Gold
    }
    
    def get_contract_value(instrument_str):
        if pd.isna(instrument_str) or instrument_str is None:
            return 50.0  # Default to ES
        inst_str = str(instrument_str).strip().upper()
        # Remove trailing digits if present (e.g., "ES2" -> "ES", "NQ1" -> "NQ")
        base_inst = inst_str.rstrip("0123456789")
        contract_val = contract_values.get(base_inst, 50.0)
        # Debug logging for NQ streams to verify contract value
        if base_inst == "NQ" and contract_val != 10.0:
            logger.warning(f"NQ contract value mismatch: got {contract_val}, expected 10.0 for instrument {instrument_str}")
        return contract_val
    
    # ALWAYS recompute ProfitDollars from Profit to ensure contract_multiplier is applied correctly
    df["ProfitDollars"] = df.apply(
        lambda row: (row.get("Profit", 0.0) or 0.0) * get_contract_value(row.get("Instrument")) * contract_multiplier,
        axis=1
    )
    
    df["ProfitDollars"] = pd.to_numeric(df["ProfitDollars"], errors='coerce').fillna(0.0)


def _ensure_date_column(df: pd.DataFrame) -> pd.DataFrame:
    """
    Ensure trade_date column exists and has correct dtype.
    
    DATE OWNERSHIP: DataLoader owns date normalization.
    This function validates dtype/presence but does NOT parse dates.
    """
    df = df.copy()
    
    # DATE OWNERSHIP: DataLoader should have normalized trade_date
    # Validate presence and dtype, but don't parse
    if "trade_date" not in df.columns:
        # For backward compatibility, try to use Date if available
        # But log this as a warning since it violates single ownership
        if "Date" in df.columns:
            logger.warning(
                "Statistics: trade_date missing, using Date as fallback. "
                "This violates single ownership - DataLoader should have normalized dates."
            )
            # Validate Date dtype first
            from .data_loader import _validate_trade_date_dtype
            try:
                # Temporarily create trade_date from Date for validation
                temp_df = df.copy()
                temp_df['trade_date'] = temp_df['Date']
                _validate_trade_date_dtype(temp_df, "statistics_fallback")
                df["trade_date"] = df["Date"]
            except ValueError:
                logger.error("Statistics: Cannot create trade_date from Date - Date is not datetime dtype")
                df["trade_date"] = pd.NaT
        else:
            logger.error("Statistics: Missing trade_date column - DataLoader should have normalized dates")
            df["trade_date"] = pd.NaT
    else:
        # Validate trade_date dtype (should already be datetime from DataLoader)
        from .data_loader import _validate_trade_date_dtype
        try:
            _validate_trade_date_dtype(df, "statistics")
        except ValueError as e:
            logger.error(f"Statistics: trade_date validation failed: {e}")
            # Don't fail here - log error but continue with NaT
    
    return df


def _ensure_date_column_inplace(df: pd.DataFrame) -> None:
    """
    Ensure trade_date column exists and has correct dtype (in-place).
    
    DATE OWNERSHIP: DataLoader owns date normalization.
    This function validates dtype/presence but does NOT parse dates.
    """
    # DATE OWNERSHIP: DataLoader should have normalized trade_date
    # Validate presence and dtype, but don't parse
    if "trade_date" not in df.columns:
        # For backward compatibility, try to use Date if available
        # But log this as a warning since it violates single ownership
        if "Date" in df.columns:
            logger.warning(
                "Statistics: trade_date missing, using Date as fallback. "
                "This violates single ownership - DataLoader should have normalized dates."
            )
            # Validate Date dtype first
            from .data_loader import _validate_trade_date_dtype
            try:
                # Temporarily create trade_date from Date for validation
                temp_df = df.copy()
                temp_df['trade_date'] = temp_df['Date']
                _validate_trade_date_dtype(temp_df, "statistics_fallback")
                df["trade_date"] = df["Date"]
            except ValueError:
                logger.error("Statistics: Cannot create trade_date from Date - Date is not datetime dtype")
                df["trade_date"] = pd.NaT
        else:
            logger.error("Statistics: Missing trade_date column - DataLoader should have normalized dates")
            df["trade_date"] = pd.NaT
    else:
        # Validate trade_date dtype (should already be datetime from DataLoader)
        from .data_loader import _validate_trade_date_dtype
        try:
            _validate_trade_date_dtype(df, "statistics")
        except ValueError as e:
            logger.error(f"Statistics: trade_date validation failed: {e}")
            # Don't fail here - log error but continue with NaT


# ---------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------

def calculate_summary_stats(
    df: pd.DataFrame,
    include_filtered_executed: bool = True,
    contract_multiplier: float = 1.0
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
    
    # PERFORMANCE OPTIMIZATION: Copy DataFrame once at the start instead of in each helper
    # All helper functions modify the DataFrame, so we need one copy to avoid mutating input
    df = df.copy()
    
    # Prepare DataFrame (helpers now modify in-place)
    _ensure_final_allowed_inplace(df)
    _normalize_results_inplace(df)
    _ensure_profit_column_inplace(df)
    _ensure_profit_dollars_column_inplace(df, contract_multiplier=contract_multiplier)
    _ensure_date_column_inplace(df)
    
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
    # STEP 1: ESTABLISH TWO EXPLICIT DATASETS
    # ========================================================================
    # executed_all: ALL executed trades (filtered + allowed) - AUTHORITATIVE RISK UNIVERSE
    executed_all = executed_df.copy()
    
    # executed_selected: Selected executed trades based on include_filtered_executed flag
    # - If include_filtered_executed == True: Same as executed_all
    # - If include_filtered_executed == False: Only allowed executed trades
    # This is the BEHAVIORAL / REPORTING SAMPLE
    if include_filtered_executed:
        executed_selected = executed_all.copy()
    else:
        executed_selected = executed_all[executed_all["final_allowed"]].copy()
    
    # ========================================================================
    # PHASE 5.2: Population Alignment Diagnostics
    # ========================================================================
    logger.info("=" * 80)
    logger.info("POPULATION ALIGNMENT DIAGNOSTICS")
    logger.info("=" * 80)
    
    # For each population, log count, sum ProfitDollars, and count of each Result class
    def log_population_diagnostics(population_df, population_name):
        """Log diagnostics for a population DataFrame."""
        count = len(population_df)
        profit_sum = population_df['ProfitDollars'].sum()
        
        # Count each Result class (vectorized)
        result_norm = population_df['ResultNorm'].astype(str)
        wins = (result_norm == 'WIN').sum()
        losses = (result_norm == 'LOSS').sum()
        be = (result_norm.isin(['BE', 'BREAKEVEN'])).sum()
        time = (result_norm == 'TIME').sum()
        notrade = (result_norm == 'NOTRADE').sum()
        other = count - (wins + losses + be + time + notrade)
        
        # Check if final_allowed was applied
        if 'final_allowed' in population_df.columns:
            allowed_count = population_df['final_allowed'].sum()
            filtered_count = count - allowed_count
        else:
            allowed_count = None
            filtered_count = None
        
        logger.info(f"{population_name}:")
        logger.info(f"  count={count}, sum ProfitDollars={profit_sum:.2f}")
        logger.info(f"  Result breakdown: WIN={wins}, LOSS={losses}, BE={be}, TIME={time}, NoTrade={notrade}, other={other}")
        if allowed_count is not None:
            logger.info(f"  final_allowed: allowed={allowed_count}, filtered={filtered_count}")
        
        # Hard check: Result classes should sum to count (if all are accounted for)
        result_sum = wins + losses + be + time + notrade + other
        if result_sum != count:
            logger.error(f"  RESULT COUNT MISMATCH: {result_sum} != {count}")
        
        return {
            'count': count,
            'profit_sum': profit_sum,
            'wins': wins,
            'losses': losses,
            'be': be,
            'time': time,
            'notrade': notrade,
            'other': other
        }
    
    # Log executed_all population
    executed_all_diag = log_population_diagnostics(executed_all, "executed_all")
    
    # Log executed_selected population
    executed_selected_diag = log_population_diagnostics(executed_selected, "executed_selected")
    
    # Hard check: allowed + filtered == executed_total (for same population definition)
    if sample_counts['executed_trades_allowed'] + sample_counts['executed_trades_filtered'] != sample_counts['executed_trades_total']:
        logger.error(
            f"CATEGORY COUNT MISMATCH: allowed ({sample_counts['executed_trades_allowed']}) + "
            f"filtered ({sample_counts['executed_trades_filtered']}) != "
            f"total ({sample_counts['executed_trades_total']})"
        )
    
    # Hard check: If headline says "Executed Trades 25005" but W/L/BE/TIME sums to 9842, that's a label mismatch
    executed_result_sum = executed_all_diag['wins'] + executed_all_diag['losses'] + executed_all_diag['be'] + executed_all_diag['time']
    if executed_result_sum != executed_all_diag['count']:
        logger.warning(
            f"POPULATION LABEL MISMATCH: Executed trades count ({executed_all_diag['count']}) != "
            f"sum of WIN/LOSS/BE/TIME ({executed_result_sum}). "
            f"Check if NoTrade or other results are incorrectly included."
        )
    
    logger.info("=" * 80)
    
    if len(executed_selected) == 0:
        logger.warning("No executed trades in executed_selected")
        # Still calculate risk metrics from executed_all even if selected is empty
        executed_day_counts = _calculate_day_counts(executed_all)
        executed_trading_days = executed_day_counts["executed_trading_days"]
        return {
            "sample_counts": sample_counts,
            "performance_trade_metrics": _empty_trade_metrics(),
            "performance_daily_metrics": _empty_daily_metrics(),
            "day_counts": {
                "executed_trading_days": executed_trading_days,  # Risk calendar days
                "active_trading_days": 0,  # Behavioral activity days
            },
        }
    
    # ========================================================================
    # STEP 2: DAY COUNTING
    # ========================================================================
    # active_trading_days: Derived from executed_selected (follows toggle)
    # All metrics (risk and behavioral) use active_trading_days
    active_trading_days = _count_unique_days(executed_selected)
    executed_selected_trade_count = len(executed_selected)
    
    # Also calculate executed_trading_days from executed_all for reference/comparison
    executed_day_counts = _calculate_day_counts(executed_all)
    executed_trading_days = executed_day_counts["executed_trading_days"]
    
    # ========================================================================
    # PERFORMANCE TRADE METRICS (per-trade, computed on executed_selected)
    # ========================================================================
    performance_trade_metrics = _calculate_trade_metrics(executed_selected)
    
    # ========================================================================
    # STEP 3: DAILY METRIC COMPUTATION
    # ========================================================================
    # A) Risk daily metrics - now computed from executed_selected (follows toggle)
    risk_daily_metrics = _calculate_risk_daily_metrics(executed_selected)
    
    # B) Behavioral daily metrics - computed from executed_selected
    behavioral_daily_metrics = _calculate_behavioral_daily_metrics(
        executed_selected,
        active_trading_days=active_trading_days,
        total_profit=performance_trade_metrics["total_profit"],
        executed_selected_trade_count=executed_selected_trade_count
    )
    
    # Merge risk and behavioral metrics
    performance_daily_metrics = {**risk_daily_metrics, **behavioral_daily_metrics}
    
    # ========================================================================
    # VALIDATION ASSERTIONS
    # ========================================================================
    # Basic sanity checks
    assert len(executed_all) >= len(executed_selected), "executed_all must contain all executed trades"
    assert active_trading_days == _count_unique_days(executed_selected), "active_trading_days must be from executed_selected"
    assert executed_trading_days == _count_unique_days(executed_all), "executed_trading_days must be from executed_all"
    assert executed_trading_days >= active_trading_days, "Total trading days must be >= active trading days"
    
    # ========================================================================
    # LOGGING
    # ========================================================================
    logger.info("=" * 80)
    logger.info("MASTER MATRIX SUMMARY STATISTICS")
    logger.info("=" * 80)
    logger.info(f"Total Rows: {total_rows} | Allowed: {allowed_rows} | Filtered: {filtered_rows}")
    logger.info(f"Executed Trades: Total={executed_trades_total} | Allowed={executed_trades_allowed} | Filtered={executed_trades_filtered}")
    logger.info(f"NoTrade Rows: {notrade_total}")
    logger.info(f"Executed Selected: {executed_selected_trade_count} executed trades (include_filtered={include_filtered_executed})")
    logger.info("Performance Trade Metrics:")
    logger.info(f"  Wins: {performance_trade_metrics['wins']} | Losses: {performance_trade_metrics['losses']} | BE: {performance_trade_metrics['be']} | TIME: {performance_trade_metrics['time']}")
    logger.info(f"  Win Rate: {performance_trade_metrics['win_rate']:.1f}%")
    logger.info(f"  Total Profit: {performance_trade_metrics['total_profit']:.2f}")
    logger.info(f"  Avg Profit / Trade: {performance_trade_metrics['mean_pnl_per_trade']:.2f}")
    logger.info(f"  Risk-Reward: {performance_trade_metrics['rr_ratio']:.2f}")
    logger.info("Performance Daily Metrics:")
    logger.info(f"  Executed Trading Days (all): {executed_trading_days} (from executed_all, for reference)")
    logger.info(f"  Active Trading Days (selected): {active_trading_days} (from executed_selected, used for metrics)")
    logger.info(f"  Executed Selected: {executed_selected_trade_count} trades across {active_trading_days} active days")
    logger.info(f"  Avg Trades Per Active Day: {performance_daily_metrics['avg_trades_per_day']:.2f}")
    logger.info(f"  Profit Per Active Day: {performance_daily_metrics['profit_per_day']:.2f}")
    logger.info(f"  Sharpe Ratio: {performance_daily_metrics['sharpe_ratio']:.2f} (computed on executed_selected, follows toggle)")
    logger.info(f"  Sortino Ratio: {performance_daily_metrics['sortino_ratio']:.2f} (computed on executed_selected, follows toggle)")
    logger.info(f"  Calmar Ratio: {performance_daily_metrics['calmar_ratio']:.2f} (computed on executed_selected, follows toggle)")
    logger.info("=" * 80)
    
    return {
        "sample_counts": sample_counts,
        "performance_trade_metrics": performance_trade_metrics,
        "performance_daily_metrics": performance_daily_metrics,
        "day_counts": {
            "executed_trading_days": executed_trading_days,  # Total trading days (from executed_all, for reference)
            "active_trading_days": active_trading_days,  # Active trading days (from executed_selected, used for metrics)
            "allowed_trading_days": active_trading_days,  # Alias for active_trading_days (for frontend compatibility)
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
        "avg_drawdown_daily": 0.0,
        "avg_drawdown_duration_days": 0.0,
        "drawdown_episodes_per_year": 0.0,
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
    # PERFORMANCE: Copy only when we need to add column, not for filtering
    valid_dates = executed_df["trade_date"].notna()
    daily_df = executed_df[valid_dates].copy()  # Need copy because we add "trade_date_only" column
    
    if daily_df.empty:
        return {"executed_trading_days": 0}
    
    # Dtype guard: assert trade_date is datetime-like before .dt accessor
    if not pd.api.types.is_datetime64_any_dtype(daily_df["trade_date"]):
        raise ValueError(
            f"statistics._count_executed_trading_days: trade_date is {daily_df['trade_date'].dtype}, "
            f"cannot use .dt accessor. No fallback to Date column."
        )
    
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
    # PERFORMANCE: Copy only when we need to add column, not for filtering
    valid_dates = df["trade_date"].notna()
    daily_df = df[valid_dates].copy()  # Need copy because we add "trade_date_only" column
    
    if daily_df.empty:
        return 0
    
    # Dtype guard: assert trade_date is datetime-like before .dt accessor
    if not pd.api.types.is_datetime64_any_dtype(daily_df["trade_date"]):
        raise ValueError(
            f"statistics._count_unique_days: trade_date is {daily_df['trade_date'].dtype}, "
            f"cannot use .dt accessor. No fallback to Date column."
        )
    
    # Group by trading day (date only, not time)
    daily_df["trade_date_only"] = daily_df["trade_date"].dt.date
    unique_days = set(daily_df["trade_date_only"].unique())
    
    return len(unique_days)


def _calculate_risk_daily_metrics(
    executed_selected: pd.DataFrame
) -> Dict:
    """
    Calculate RISK daily metrics from SELECTED executed trades (executed_selected).
    
    These metrics now follow the include_filtered_executed toggle:
    - Sharpe ratio
    - Sortino ratio
    - Calmar ratio
    - Daily volatility
    - Daily max drawdown
    - Time-to-recovery
    - Monthly return std dev
    
    Args:
        executed_selected: DataFrame containing SELECTED executed trades (may be filtered based on toggle), must have trade_date column
        
    Returns:
        Dictionary of RISK daily metrics (follows filtering toggle)
    """
    if executed_selected.empty:
        return {
            "sharpe_ratio": 0.0,
            "sortino_ratio": 0.0,
            "calmar_ratio": 0.0,
            "time_to_recovery_days": 0,
            "avg_drawdown_daily": 0.0,
            "avg_drawdown_duration_days": 0.0,
            "drawdown_episodes_per_year": 0.0,
            "monthly_return_stddev": 0.0,
            "max_drawdown_daily": 0.0,
        }
    
    # Ensure trade_date exists
    if "trade_date" not in executed_selected.columns:
        logger.warning("trade_date column missing, cannot calculate risk daily metrics")
        return {
            "sharpe_ratio": 0.0,
            "sortino_ratio": 0.0,
            "calmar_ratio": 0.0,
            "time_to_recovery_days": 0,
            "avg_drawdown_daily": 0.0,
            "avg_drawdown_duration_days": 0.0,
            "drawdown_episodes_per_year": 0.0,
            "monthly_return_stddev": 0.0,
            "max_drawdown_daily": 0.0,
        }
    
    # Remove rows with invalid dates
    # PERFORMANCE: Copy only when we need to add columns, not for filtering
    valid_dates = executed_selected["trade_date"].notna()
    daily_df = executed_selected[valid_dates].copy()  # Need copy because we add "trade_date_only" and "year_month" columns
    
    if daily_df.empty:
        return {
            "sharpe_ratio": 0.0,
            "sortino_ratio": 0.0,
            "calmar_ratio": 0.0,
            "time_to_recovery_days": 0,
            "avg_drawdown_daily": 0.0,
            "avg_drawdown_duration_days": 0.0,
            "drawdown_episodes_per_year": 0.0,
            "monthly_return_stddev": 0.0,
            "max_drawdown_daily": 0.0,
        }
    
    # Profit column (use ProfitDollars if available, else Profit)
    profit_col = "ProfitDollars" if "ProfitDollars" in daily_df.columns else "Profit"
    
    # Dtype guard: assert trade_date is datetime-like before .dt accessor
    if not pd.api.types.is_datetime64_any_dtype(daily_df["trade_date"]):
        raise ValueError(
            f"statistics._calculate_risk_daily_metrics: trade_date is {daily_df['trade_date'].dtype}, "
            f"cannot use .dt accessor. No fallback to Date column."
        )
    
    # Group by trading day (date only, not time) - RISK METRICS NOW FOLLOW TOGGLE (use executed_selected)
    daily_df["trade_date_only"] = daily_df["trade_date"].dt.date
    daily_pnl = daily_df.groupby("trade_date_only", sort=False)[profit_col].sum().reset_index()
    daily_pnl.columns = ["date", "pnl"]
    
    # Sharpe/Sortino ratios (annualized using 252 trading days)
    daily_returns = daily_pnl["pnl"].values
    mean_daily_return = float(np.mean(daily_returns)) if len(daily_returns) > 0 else 0.0
    std_daily_return = float(np.std(daily_returns)) if len(daily_returns) > 1 else 0.0
    
    trading_days_per_year = 252
    annualized_return = mean_daily_return * trading_days_per_year
    annualized_volatility = std_daily_return * np.sqrt(trading_days_per_year)
    sharpe_ratio = annualized_return / annualized_volatility if annualized_volatility > 0 else 0.0
    
    # Sortino: only downside volatility (standard definition: variance around zero)
    downside_returns = daily_returns[daily_returns < 0]
    # Calculate downside deviation around zero (standard Sortino definition)
    downside_variance = float(np.mean(downside_returns ** 2)) if len(downside_returns) > 0 else 0.0
    downside_std = float(np.sqrt(downside_variance))
    annualized_downside_vol = downside_std * np.sqrt(trading_days_per_year)
    sortino_ratio = annualized_return / annualized_downside_vol if annualized_downside_vol > 0 else 0.0
    
    # Time to recovery (trading days, not calendar days)
    cumulative_pnl = daily_pnl["pnl"].cumsum()
    running_max = cumulative_pnl.expanding().max()
    drawdown = cumulative_pnl - running_max
    
    # Find longest recovery period (from drawdown start to new peak)
    # Also track all drawdown episodes for average duration and frequency calculations
    time_to_recovery_days = 0
    in_drawdown = False
    drawdown_start_idx = None
    drawdown_episodes = []  # List of episode durations in days
    
    for idx in range(len(drawdown)):
        if drawdown.iloc[idx] < 0:
            if not in_drawdown:
                in_drawdown = True
                drawdown_start_idx = idx
        else:
            if in_drawdown and drawdown_start_idx is not None:
                recovery_days = idx - drawdown_start_idx
                time_to_recovery_days = max(time_to_recovery_days, recovery_days)
                drawdown_episodes.append(recovery_days)  # Track this episode
                in_drawdown = False
                drawdown_start_idx = None
    
    # Handle case where drawdown extends to end of data
    if in_drawdown and drawdown_start_idx is not None:
        recovery_days = len(drawdown) - drawdown_start_idx
        time_to_recovery_days = max(time_to_recovery_days, recovery_days)
        drawdown_episodes.append(recovery_days)
    
    # Average drawdown: mean of absolute drawdown values on drawdown days only
    drawdown_days_only = drawdown[drawdown < 0]
    avg_drawdown_daily = float(abs(drawdown_days_only).mean()) if len(drawdown_days_only) > 0 else 0.0
    
    # Average drawdown duration: mean of all drawdown episode durations
    avg_drawdown_duration_days = float(np.mean(drawdown_episodes)) if len(drawdown_episodes) > 0 else 0.0
    
    # Drawdown frequency: episodes per year
    # Calculate total trading years from date range
    if len(daily_pnl) > 0 and "date" in daily_pnl.columns:
        # Convert date objects to datetime for calculation
        from datetime import datetime as dt
        dates = pd.to_datetime(daily_pnl["date"])
        date_range = dates.max() - dates.min()
        total_days = date_range.days + 1  # +1 to include both start and end days
        total_trading_years = total_days / 365.25  # Account for leap years
        drawdown_episodes_per_year = len(drawdown_episodes) / total_trading_years if total_trading_years > 0 else 0.0
    else:
        drawdown_episodes_per_year = 0.0
    
    # Dtype guard: assert trade_date is datetime-like before .dt accessor
    if not pd.api.types.is_datetime64_any_dtype(daily_df["trade_date"]):
        raise ValueError(
            f"statistics._calculate_risk_daily_metrics: trade_date is {daily_df['trade_date'].dtype}, "
            f"cannot use .dt.to_period accessor. No fallback to Date column."
        )
    
    # Monthly return std dev (Option 1: actual calendar months)
    daily_df["year_month"] = daily_df["trade_date"].dt.to_period("M")
    monthly_pnl = daily_df.groupby("year_month", sort=False)[profit_col].sum()
    monthly_return_stddev = float(monthly_pnl.std()) if len(monthly_pnl) > 1 else 0.0
    
    # Calmar ratio (annualized return / max drawdown)
    mean_daily_return_for_annualization = float(np.mean(daily_returns)) if len(daily_returns) > 0 else 0.0
    annual_return_from_daily = mean_daily_return_for_annualization * trading_days_per_year
    # Max drawdown from daily cumulative PnL series
    cumulative_pnl_for_calmar = daily_pnl["pnl"].cumsum()
    running_max_daily = cumulative_pnl_for_calmar.expanding().max()
    drawdown_daily = cumulative_pnl_for_calmar - running_max_daily
    max_drawdown_from_daily = float(abs(drawdown_daily.min())) if not drawdown_daily.empty else 0.0
    calmar_ratio = annual_return_from_daily / max_drawdown_from_daily if max_drawdown_from_daily > 0 else 0.0
    
    return {
        "sharpe_ratio": round(sharpe_ratio, 2),
        "sortino_ratio": round(sortino_ratio, 2),
        "calmar_ratio": round(calmar_ratio, 2),
        "time_to_recovery_days": int(time_to_recovery_days),
        "avg_drawdown_daily": round(avg_drawdown_daily, 2),
        "avg_drawdown_duration_days": round(avg_drawdown_duration_days, 1),
        "drawdown_episodes_per_year": round(drawdown_episodes_per_year, 2),
        "monthly_return_stddev": round(monthly_return_stddev, 2),
        "max_drawdown_daily": round(max_drawdown_from_daily, 2),
    }


def _calculate_behavioral_daily_metrics(
    executed_selected: pd.DataFrame,
    active_trading_days: int,
    total_profit: float,
    executed_selected_trade_count: int
) -> Dict:
    """
    Calculate BEHAVIORAL daily metrics from SELECTED executed trades (executed_selected).
    
    These metrics MAY be affected by filtering:
    - Avg trades per day
    - Profit per active day
    - Profit projections (week/month/year)
    
    Args:
        executed_selected: DataFrame containing SELECTED executed trades (may be filtered), must have trade_date column
        active_trading_days: Count of active trading days (activity-based, from executed_selected)
        total_profit: Total profit from executed_selected (for profit_per_day calculation)
        executed_selected_trade_count: Count of trades in executed_selected (for avg_trades_per_day)
        
    Returns:
        Dictionary of BEHAVIORAL daily metrics (may vary with filtering)
    """
    if executed_selected.empty or active_trading_days == 0:
        return {
            "avg_trades_per_day": 0.0,
            "profit_per_day": 0.0,
            "profit_per_week": 0.0,
            "profit_per_month": 0.0,
            "profit_per_year": 0.0,
        }
    
    # Behavioral averages use active_trading_days (days with ≥1 trade in executed_selected)
    avg_trades_per_day = (executed_selected_trade_count / active_trading_days) if active_trading_days > 0 else 0.0
    profit_per_day = (total_profit / active_trading_days) if active_trading_days > 0 else 0.0
    
    # Projected metrics (from profit_per_day)
    profit_per_week = profit_per_day * 5
    profit_per_month = profit_per_day * 21
    profit_per_year = profit_per_day * 252
    
    return {
        "avg_trades_per_day": round(avg_trades_per_day, 2),
        "profit_per_day": round(profit_per_day, 2),
        "profit_per_week": round(profit_per_week, 2),
        "profit_per_month": round(profit_per_month, 2),
        "profit_per_year": round(profit_per_year, 2),
    }


def calculate_stream_stats(
    df: pd.DataFrame,
    include_filtered_executed: bool = True
) -> Dict[str, Dict]:
    """
    Calculate per-stream statistics.
    
    IMPORTANT: STREAM STATS ARE BEHAVIORAL SUMMARIES ONLY
    
    Stream stats are behavioral summaries that reflect profitability and trade counts
    per stream. They are NOT risk-adjusted metrics.
    
    CRITICAL WARNINGS:
    - Stream stats are affected by filtering (include_filtered_executed flag)
    - Stream stats do NOT include Sharpe/Sortino/Calmar ratios
    - Stream stats MUST NOT be compared to system-level risk metrics
    - Filtering affects stream stats by design (this is intentional)
    
    These stats are useful for:
    - Comparing profitability across streams
    - Understanding trade volume per stream
    - Identifying which streams contribute most to overall profit
    
    These stats are NOT useful for:
    - Risk-adjusted performance comparison
    - Volatility analysis
    - Drawdown analysis
    
    For risk-adjusted metrics, use calculate_summary_stats() which provides
    system-level Sharpe/Sortino/Calmar computed on the full executed universe.
    
    Args:
        df: DataFrame containing trades
        include_filtered_executed: If True, include filtered executed trades in stats.
                                   If False, only include allowed executed trades.
    """
    df = _ensure_final_allowed(df)
    df = _normalize_results(df)
    df = _ensure_profit_column(df)
    df = _ensure_profit_dollars_column(df)
    df["is_executed_trade"] = df["ResultNorm"].apply(_is_executed_trade)
    
    stream_stats: Dict[str, Dict] = {}
    
    # Filter out None values before sorting to avoid comparison errors
    stream_values = [s for s in df["Stream"].unique() if s is not None and pd.notna(s)]
    for stream in sorted(stream_values):
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
