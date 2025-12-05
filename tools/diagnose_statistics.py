#!/usr/bin/env python3
"""
Statistics Diagnostic Tool
Verifies calculations for trading statistics and identifies discrepancies
"""

import sys
import pandas as pd
import numpy as np
from pathlib import Path
from typing import Dict, List, Tuple
import json

# Base paths
QTSW2_ROOT = Path(__file__).parent.parent
DATA_PROCESSED = QTSW2_ROOT / "data" / "data_processed"
DATA_MERGED = QTSW2_ROOT / "data" / "data_merged"

# Contract values (must match frontend)
CONTRACT_VALUES = {
    'ES': 50,
    'NQ': 10,
    'YM': 5,
    'CL': 1000,
    'NG': 10000,
    'GC': 100
}

def get_contract_value(trade: pd.Series) -> float:
    """Get contract value for a trade."""
    symbol = str(trade.get('Symbol', trade.get('Instrument', 'ES')))
    base_symbol = symbol.replace(r'\d+$', '') if pd.notna(symbol) else 'ES'
    return CONTRACT_VALUES.get(base_symbol, 50)

def parse_date(date_value) -> pd.Timestamp:
    """Parse date from various formats."""
    if pd.isna(date_value):
        return None
    try:
        if isinstance(date_value, str) and '/' in date_value:
            parts = date_value.split('/')
            if len(parts) == 3:
                return pd.Timestamp(int(parts[2]), int(parts[1]), int(parts[0]))
        return pd.Timestamp(date_value)
    except:
        return None

def calculate_statistics(df: pd.DataFrame, contract_multiplier: float = 1.0) -> Dict:
    """Calculate all statistics independently."""
    if df.empty:
        return {}
    
    # Profit calculations first (needed for all trades)
    df['profit'] = pd.to_numeric(df['Profit'], errors='coerce').fillna(0)
    df['contract_value'] = df.apply(get_contract_value, axis=1)
    df['profit_dollars'] = df['profit'] * df['contract_value'] * contract_multiplier
    
    # Parse dates
    df['date_parsed'] = df['Date'].apply(parse_date)
    
    # Basic counts
    # Exclude NoTrade from total trades (matches worker logic)
    valid_trades = df[df['Result'] != 'NoTrade'].copy()
    total_trades = len(valid_trades)
    wins = len(df[df['Result'] == 'Win'])
    losses = len(df[df['Result'] == 'Loss'])
    break_even = len(df[df['Result'] == 'BE'])
    no_trade = len(df[df['Result'] == 'NoTrade'])
    
    win_loss_trades = wins + losses
    win_rate = (wins / win_loss_trades * 100) if win_loss_trades > 0 else 0
    
    total_profit_dollars = df['profit_dollars'].sum()
    avg_profit = df['profit'].mean()
    
    winning_trades = df[df['Result'] == 'Win']
    losing_trades = df[df['Result'] == 'Loss']
    avg_win = winning_trades['profit'].mean() if len(winning_trades) > 0 else 0
    avg_loss = abs(losing_trades['profit'].mean()) if len(losing_trades) > 0 else 0
    rr_ratio = avg_win / avg_loss if avg_loss > 0 else (float('inf') if avg_win > 0 else 0)
    
    # Total Days
    df['date_parsed'] = df['Date'].apply(parse_date)
    unique_dates = df['date_parsed'].dropna().dt.date.unique()
    total_days = len(unique_dates)
    avg_trades_per_day = total_trades / total_days if total_days > 0 else 0
    
    # Profit Factor
    gross_profit = winning_trades['profit_dollars'].sum()
    gross_loss = abs(losing_trades['profit_dollars'].sum())
    profit_factor = gross_profit / gross_loss if gross_loss > 0 else (float('inf') if gross_profit > 0 else 0)
    
    # Sort valid trades chronologically for all calculations
    valid_sorted = valid_trades.sort_values(['date_parsed', 'Time']).copy()
    
    # Rolling Drawdown (use valid trades only)
    valid_sorted['running_profit'] = valid_sorted['profit_dollars'].cumsum()
    valid_sorted['peak'] = valid_sorted['running_profit'].cummax()
    valid_sorted['drawdown'] = valid_sorted['running_profit'] - valid_sorted['peak']
    max_drawdown_dollars = abs(valid_sorted['drawdown'].min())
    max_drawdown = max_drawdown_dollars / 50  # Convert to points (approximate)
    
    # Sharpe Ratio (use valid trades only)
    trading_days_per_year = 252
    daily_returns = valid_sorted.groupby(valid_sorted['date_parsed'].dt.date)['profit_dollars'].sum()
    mean_daily_return = daily_returns.mean()
    std_daily_return = daily_returns.std()
    annualized_return = mean_daily_return * trading_days_per_year
    annualized_volatility = std_daily_return * np.sqrt(trading_days_per_year) if std_daily_return > 0 else 0
    sharpe_ratio = annualized_return / annualized_volatility if annualized_volatility > 0 else 0
    
    # Sortino Ratio
    downside_returns = daily_returns[daily_returns < 0]
    downside_std = downside_returns.std() if len(downside_returns) > 1 else 0
    annualized_downside_vol = downside_std * np.sqrt(trading_days_per_year) if downside_std > 0 else 0
    sortino_ratio = annualized_return / annualized_downside_vol if annualized_downside_vol > 0 else 0
    
    # Calmar Ratio
    annual_return = (total_profit_dollars / total_days) * trading_days_per_year if total_days > 0 else 0
    calmar_ratio = annual_return / max_drawdown_dollars if max_drawdown_dollars > 0 else 0
    
    # Profit per Day
    profit_per_day = daily_returns.mean() if len(daily_returns) > 0 else 0
    
    # Profit per Week
    profit_per_week = profit_per_day * 5  # 5 trading days per week
    
    # Profit per Month
    profit_per_month = profit_per_day * 21  # ~21 trading days per month
    
    # Profit per Year
    profit_per_year = profit_per_day * trading_days_per_year
    
    # Per-trade PnL statistics (exclude NoTrade) - use sorted for consistency
    per_trade_pnl = valid_sorted['profit_dollars'].values
    mean_pnl = np.mean(per_trade_pnl) if len(per_trade_pnl) > 0 else 0
    median_pnl = np.median(per_trade_pnl) if len(per_trade_pnl) > 0 else 0
    std_pnl = np.std(per_trade_pnl, ddof=1) if len(per_trade_pnl) > 1 else 0
    
    # Max Consecutive Losses
    max_consecutive_losses = 0
    current_streak = 0
    for pnl in per_trade_pnl:
        if pnl < 0:
            current_streak += 1
            max_consecutive_losses = max(max_consecutive_losses, current_streak)
        else:
            current_streak = 0
    
    # VaR 95%
    sorted_pnl = np.sort(per_trade_pnl)
    var95_index = int(len(sorted_pnl) * 0.05)
    var95 = sorted_pnl[var95_index] if var95_index < len(sorted_pnl) else 0
    
    # CVaR 95%
    cvar95 = np.mean(sorted_pnl[:var95_index + 1]) if var95_index < len(sorted_pnl) else 0
    
    # Time-to-Recovery (use valid trades only)
    time_to_recovery = 0
    peak_value = -np.inf
    peak_date = None
    in_drawdown = False
    
    for idx, row in valid_sorted.iterrows():
        running_profit = row['running_profit']
        trade_date = row['date_parsed']
        if pd.isna(trade_date):
            continue
        
        if running_profit > peak_value:
            if in_drawdown and peak_date:
                days_diff = (trade_date - peak_date).days
                time_to_recovery = max(time_to_recovery, days_diff)
                in_drawdown = False
            peak_value = running_profit
            peak_date = trade_date
        elif running_profit < peak_value:
            in_drawdown = True
    
    # Monthly Return Std Dev (use valid trades only)
    monthly_returns = valid_sorted.groupby([
        valid_sorted['date_parsed'].dt.year,
        valid_sorted['date_parsed'].dt.month
    ])['profit_dollars'].sum()
    monthly_return_std_dev = monthly_returns.std() if len(monthly_returns) > 1 else 0
    
    # Skewness
    skewness = 0
    if len(per_trade_pnl) > 2 and std_pnl > 0:
        skewness = ((len(per_trade_pnl) / ((len(per_trade_pnl) - 1) * (len(per_trade_pnl) - 2))) *
                    np.sum(((per_trade_pnl - mean_pnl) / std_pnl) ** 3))
    
    # Kurtosis
    kurtosis = 0
    if len(per_trade_pnl) > 3 and std_pnl > 0:
        kurtosis = (((len(per_trade_pnl) * (len(per_trade_pnl) + 1)) / 
                    ((len(per_trade_pnl) - 1) * (len(per_trade_pnl) - 2) * (len(per_trade_pnl) - 3))) *
                   np.sum(((per_trade_pnl - mean_pnl) / std_pnl) ** 4) -
                   (3 * (len(per_trade_pnl) - 1) ** 2) / ((len(per_trade_pnl) - 2) * (len(per_trade_pnl) - 3)))
    
    return {
        'total_profit_dollars': total_profit_dollars,
        'total_trades': total_trades,
        'total_days': total_days,
        'avg_trades_per_day': avg_trades_per_day,
        'profit_per_day': profit_per_day,
        'profit_per_week': profit_per_week,
        'profit_per_month': profit_per_month,
        'profit_per_year': profit_per_year,
        'profit_per_trade_mean': mean_pnl,
        'win_rate': win_rate,
        'wins': wins,
        'losses': losses,
        'break_even': break_even,
        'sharpe_ratio': sharpe_ratio,
        'sortino_ratio': sortino_ratio,
        'calmar_ratio': calmar_ratio,
        'profit_factor': profit_factor,
        'risk_reward': rr_ratio,
        'max_drawdown_dollars': max_drawdown_dollars,
        'time_to_recovery_days': time_to_recovery,
        'max_consecutive_losses': max_consecutive_losses,
        'monthly_return_std_dev': monthly_return_std_dev,
        'median_pnl_per_trade': median_pnl,
        'std_dev_pnl': std_pnl,
        'var95': var95,
        'cvar95': cvar95,
        'skewness': skewness,
        'kurtosis': kurtosis,
    }

def load_master_matrix() -> pd.DataFrame:
    """Load master matrix data."""
    # Try multiple locations
    master_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
    master_matrix_debug_dir = QTSW2_ROOT / "data" / "master_matrix_debug"
    
    # Try JSON files first (master matrix format)
    json_files = []
    if master_matrix_dir.exists():
        json_files.extend(list(master_matrix_dir.glob("*.json")))
    if master_matrix_debug_dir.exists():
        json_files.extend(list(master_matrix_debug_dir.glob("*.json")))
    
    if json_files:
        latest_file = max(json_files, key=lambda p: p.stat().st_mtime)
        print(f"Loading JSON: {latest_file.name}")
        try:
            with open(latest_file, 'r') as f:
                data = json.load(f)
            # Handle different JSON structures
            if isinstance(data, list):
                df = pd.DataFrame(data)
            elif isinstance(data, dict):
                if 'matrix' in data:
                    df = pd.DataFrame(data['matrix'])
                elif 'data' in data:
                    df = pd.DataFrame(data['data'])
                else:
                    # Try to use the dict itself as a single row
                    df = pd.DataFrame([data])
            else:
                print(f"ERROR: Unknown JSON structure in {latest_file.name}")
                return pd.DataFrame()
            
            print(f"Loaded {len(df)} rows from {latest_file.name}")
            return df
        except Exception as e:
            print(f"ERROR loading {latest_file}: {e}")
            import traceback
            traceback.print_exc()
    
    # Try parquet files in data_merged
    merged_files = list(DATA_MERGED.glob("*.parquet"))
    if merged_files:
        latest_file = max(merged_files, key=lambda p: p.stat().st_mtime)
        print(f"Loading Parquet: {latest_file.name}")
        try:
            df = pd.read_parquet(latest_file)
            print(f"Loaded {len(df)} rows from {latest_file.name}")
            return df
        except Exception as e:
            print(f"ERROR loading {latest_file}: {e}")
    
    print(f"ERROR: No master matrix files found")
    print(f"  Checked: {master_matrix_dir}")
    print(f"  Checked: {master_matrix_debug_dir}")
    print(f"  Checked: {DATA_MERGED}")
    return pd.DataFrame()

def format_currency(value: float) -> str:
    """Format as currency."""
    return f"${value:,.0f}"

def main():
    print("=" * 80)
    print("STATISTICS DIAGNOSTIC TOOL")
    print("=" * 80)
    print()
    
    # Load data
    df = load_master_matrix()
    if df.empty:
        print("ERROR: No data loaded. Exiting.")
        sys.exit(1)
    
    print(f"Data loaded: {len(df)} trades")
    print(f"Date range: {df['Date'].min()} to {df['Date'].max()}")
    print()
    
    # Calculate statistics
    print("Calculating statistics...")
    stats = calculate_statistics(df, contract_multiplier=1.0)
    
    print()
    print("=" * 80)
    print("CALCULATED STATISTICS")
    print("=" * 80)
    print()
    
    print("Core Performance:")
    print(f"  Total Profit ($):        {format_currency(stats['total_profit_dollars'])}")
    print(f"  Total Trades:            {stats['total_trades']:,}")
    print(f"  Total Days:              {stats['total_days']:,}")
    print(f"  Avg Trades/Day:          {stats['avg_trades_per_day']:.2f}")
    print(f"  Profit per Day:          {format_currency(stats['profit_per_day'])}")
    print(f"  Profit per Week:         {format_currency(stats['profit_per_week'])}")
    print(f"  Profit per Month:        {format_currency(stats['profit_per_month'])}")
    print(f"  Profit per Year:         {format_currency(stats['profit_per_year'])}")
    print(f"  Profit per Trade (Mean): {format_currency(stats['profit_per_trade_mean'])}")
    print()
    
    print("Win/Loss Statistics:")
    print(f"  Win Rate:                {stats['win_rate']:.1f}%")
    print(f"  Wins:                    {stats['wins']:,}")
    print(f"  Losses:                  {stats['losses']:,}")
    print(f"  Break-Even:              {stats['break_even']:,}")
    print()
    
    print("Risk-Adjusted Performance:")
    print(f"  Sharpe Ratio:            {stats['sharpe_ratio']:.2f}")
    print(f"  Sortino Ratio:           {stats['sortino_ratio']:.2f}")
    print(f"  Calmar Ratio:            {stats['calmar_ratio']:.2f}")
    print(f"  Profit Factor:          {stats['profit_factor']:.2f}")
    print(f"  Risk-Reward:             {stats['risk_reward']:.2f}")
    print()
    
    print("Drawdowns & Stability:")
    print(f"  Max Drawdown ($):        {format_currency(stats['max_drawdown_dollars'])}")
    print(f"  Time-to-Recovery (Days): {stats['time_to_recovery_days']}")
    print(f"  Max Consecutive Losses:   {stats['max_consecutive_losses']}")
    print(f"  Monthly Return Std Dev:  {format_currency(stats['monthly_return_std_dev'])}")
    print()
    
    print("PnL Distribution & Tail Risk:")
    print(f"  Median PnL per Trade:   {format_currency(stats['median_pnl_per_trade'])}")
    print(f"  Std Dev of PnL:          {format_currency(stats['std_dev_pnl'])}")
    print(f"  95% VaR (per trade):     {format_currency(stats['var95'])}")
    print(f"  Expected Shortfall:      {format_currency(stats['cvar95'])}")
    print(f"  Skewness:                {stats['skewness']:.3f}")
    print(f"  Kurtosis:                {stats['kurtosis']:.3f}")
    print()
    
    print("=" * 80)
    print("DIAGNOSTIC COMPLETE")
    print("=" * 80)
    print()
    print("Compare these values with what's displayed in the dashboard.")
    print("Any significant discrepancies indicate calculation errors.")
    
    # Save to JSON for comparison
    output_file = QTSW2_ROOT / "tools" / "statistics_diagnostic.json"
    with open(output_file, 'w') as f:
        json.dump(stats, f, indent=2, default=str)
    print(f"\nResults saved to: {output_file}")

if __name__ == "__main__":
    main()

