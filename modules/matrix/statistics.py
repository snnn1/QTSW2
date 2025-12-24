"""
Statistics calculation and reporting for Master Matrix.

This module handles calculating and logging summary statistics
for the master matrix data.
"""

import logging
from typing import Dict
import pandas as pd

logger = logging.getLogger(__name__)


def calculate_summary_stats(df: pd.DataFrame) -> Dict:
    """
    Calculate and log summary statistics similar to sequential processor.
    
    Args:
        df: Master matrix DataFrame
        
    Returns:
        Dictionary with summary statistics
    """
    if df.empty:
        logger.warning("No data for summary statistics")
        return {}
    
    # Overall stats
    total_trades = len(df)
    wins = len(df[df['Result'] == 'Win'])
    losses = len(df[df['Result'] == 'Loss'])
    break_even = len(df[df['Result'] == 'BE'])
    no_trade = len(df[df['Result'] == 'NoTrade'])
    
    # Win rate (excludes BE trades, only wins vs losses)
    win_loss_trades = wins + losses
    win_rate = (wins / win_loss_trades * 100) if win_loss_trades > 0 else 0
    
    # Profit stats
    total_profit = df['Profit'].sum()
    avg_profit = df['Profit'].mean()
    
    # Risk-Reward ratio
    winning_trades = df[df['Result'] == 'Win']
    losing_trades = df[df['Result'] == 'Loss']
    avg_win = winning_trades['Profit'].mean() if len(winning_trades) > 0 else 0
    avg_loss = abs(losing_trades['Profit'].mean()) if len(losing_trades) > 0 else 0
    rr_ratio = avg_win / avg_loss if avg_loss > 0 else float('inf') if avg_win > 0 else 0
    
    # Filtered trades stats
    allowed_trades = df['final_allowed'].sum() if 'final_allowed' in df.columns else total_trades
    blocked_trades = total_trades - allowed_trades
    
    # Per-stream stats
    stream_stats = calculate_stream_stats(df)
    
    # Log summary
    logger.info("=" * 80)
    logger.info("MASTER MATRIX SUMMARY STATISTICS")
    logger.info("=" * 80)
    logger.info(f"Total Trades: {total_trades}")
    logger.info(f"  Wins: {wins} | Losses: {losses} | Break-Even: {break_even} | No Trade: {no_trade}")
    logger.info(f"Win Rate: {win_rate:.1f}% (excluding BE)")
    logger.info(f"Total Profit: {total_profit:.2f}")
    logger.info(f"Average Profit per Trade: {avg_profit:.2f}")
    logger.info(f"Risk-Reward Ratio: {rr_ratio:.2f} (Avg Win: {avg_win:.1f} / Avg Loss: {avg_loss:.1f})")
    logger.info(f"Allowed Trades: {int(allowed_trades)} | Blocked Trades: {int(blocked_trades)}")
    logger.info("")
    logger.info("Per-Stream Statistics:")
    for stream, stats in stream_stats.items():
        logger.info(f"  {stream}: {stats['trades']} trades | "
                   f"Win Rate: {stats['win_rate']:.1f}% | "
                   f"Profit: {stats['profit']:.2f} | "
                   f"Allowed: {stats['allowed']}")
    logger.info("=" * 80)
    
    return {
        'total_trades': total_trades,
        'wins': wins,
        'losses': losses,
        'break_even': break_even,
        'no_trade': no_trade,
        'win_rate': round(win_rate, 1),
        'total_profit': round(total_profit, 2),
        'avg_profit': round(avg_profit, 2),
        'rr_ratio': round(rr_ratio, 2),
        'avg_win': round(avg_win, 2),
        'avg_loss': round(avg_loss, 2),
        'allowed_trades': int(allowed_trades),
        'blocked_trades': int(blocked_trades),
        'stream_stats': stream_stats
    }


def calculate_stream_stats(df: pd.DataFrame) -> Dict[str, Dict]:
    """
    Calculate per-stream statistics.
    
    Args:
        df: Master matrix DataFrame
        
    Returns:
        Dictionary mapping stream IDs to their statistics
    """
    stream_stats = {}
    for stream in sorted(df['Stream'].unique()):
        stream_df = df[df['Stream'] == stream]
        stream_wins = len(stream_df[stream_df['Result'] == 'Win'])
        stream_losses = len(stream_df[stream_df['Result'] == 'Loss'])
        stream_win_loss = stream_wins + stream_losses
        stream_win_rate = (stream_wins / stream_win_loss * 100) if stream_win_loss > 0 else 0
        stream_profit = stream_df['Profit'].sum()
        stream_allowed = stream_df['final_allowed'].sum() if 'final_allowed' in stream_df.columns else len(stream_df)
        
        stream_stats[stream] = {
            'trades': len(stream_df),
            'wins': stream_wins,
            'losses': stream_losses,
            'win_rate': round(stream_win_rate, 1),
            'profit': round(stream_profit, 2),
            'allowed': int(stream_allowed)
        }
    
    return stream_stats



