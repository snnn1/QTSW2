"""
Breakdown service - profit breakdown calculations by day, dom, time, month, year.

Extracted from api.py for maintainability. Each breakdown type returns
{key: {stream: profit}} format matching frontend worker output.
"""

import logging
from typing import Dict, Any, Optional

import pandas as pd

logger = logging.getLogger(__name__)

DOW_NAMES = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']


def _apply_filters(
    df: pd.DataFrame,
    stream_filters: Optional[Dict[str, Any]],
    use_filtered: bool
) -> pd.DataFrame:
    """Apply stream filters to dataframe. Returns filtered copy."""
    if not use_filtered or not stream_filters:
        return df

    mask = pd.Series([True] * len(df))

    # Master filters apply to all rows
    master_filters = stream_filters.get('master')
    if master_filters:
        if getattr(master_filters, 'exclude_days_of_week', None):
            if 'dow_full' in df.columns:
                mask = mask & ~df['dow_full'].isin(master_filters.exclude_days_of_week)
            elif 'dow' in df.columns:
                dow_map = {d: i for i, d in enumerate(DOW_NAMES)}
                exclude_dow_nums = [dow_map.get(d, -1) for d in master_filters.exclude_days_of_week]
                mask = mask & ~df['dow'].isin(exclude_dow_nums)

        if getattr(master_filters, 'exclude_days_of_month', None) and 'day_of_month' in df.columns:
            mask = mask & ~df['day_of_month'].isin(master_filters.exclude_days_of_month)

        if getattr(master_filters, 'exclude_times', None) and 'Time' in df.columns:
            mask = mask & ~df['Time'].isin(master_filters.exclude_times)

    # Per-stream filters
    for stream_id, filter_config in stream_filters.items():
        if stream_id == 'master':
            continue

        stream_rows = df['Stream'] == stream_id
        stream_mask = pd.Series([True] * len(df))

        if getattr(filter_config, 'exclude_days_of_week', None):
            if 'dow_full' in df.columns:
                stream_mask = stream_mask & ~df['dow_full'].isin(filter_config.exclude_days_of_week)
            elif 'dow' in df.columns:
                dow_map = {d: i for i, d in enumerate(DOW_NAMES)}
                exclude_dow_nums = [dow_map.get(d, -1) for d in filter_config.exclude_days_of_week]
                stream_mask = stream_mask & ~df['dow'].isin(exclude_dow_nums)

        if getattr(filter_config, 'exclude_days_of_month', None) and 'day_of_month' in df.columns:
            stream_mask = stream_mask & ~df['day_of_month'].isin(filter_config.exclude_days_of_month)

        if getattr(filter_config, 'exclude_times', None) and 'Time' in df.columns:
            stream_mask = stream_mask & ~df['Time'].isin(filter_config.exclude_times)

        mask = mask & (~stream_rows | stream_mask)

    if 'final_allowed' in df.columns:
        mask = mask & (df['final_allowed'] != False)

    return df[mask].copy()


def _breakdown_by_day(df: pd.DataFrame) -> Dict[str, Dict[str, float]]:
    """Day of week breakdown: {dow_name: {stream: profit}}"""
    breakdown = {}
    if 'dow_full' in df.columns and 'Stream' in df.columns:
        grouped = df.groupby(['dow_full', 'Stream'])['ProfitDollars'].sum().reset_index()
        for row in grouped.itertuples(index=False):
            dow = str(row.dow_full)
            stream = str(row.Stream)
            profit = float(row.ProfitDollars)
            if dow not in breakdown:
                breakdown[dow] = {}
            breakdown[dow][stream] = profit
    elif 'dow' in df.columns and 'Stream' in df.columns:
        grouped = df.groupby(['dow', 'Stream'])['ProfitDollars'].sum().reset_index()
        for row in grouped.itertuples(index=False):
            dow_num = int(row.dow)
            dow_name = DOW_NAMES[dow_num] if 0 <= dow_num < len(DOW_NAMES) else f'Day{dow_num}'
            stream = str(row.Stream)
            profit = float(row.ProfitDollars)
            if dow_name not in breakdown:
                breakdown[dow_name] = {}
            breakdown[dow_name][stream] = profit
    return breakdown


def _breakdown_by_dom(df: pd.DataFrame) -> Dict[int, Dict[str, float]]:
    """Day of month breakdown: {dom: {stream: profit}}"""
    breakdown = {}
    if 'day_of_month' in df.columns and 'Stream' in df.columns:
        grouped = df.groupby(['day_of_month', 'Stream'])['ProfitDollars'].sum().reset_index()
        for row in grouped.itertuples(index=False):
            dom = int(row.day_of_month)
            stream = str(row.Stream)
            profit = float(row.ProfitDollars)
            if dom not in breakdown:
                breakdown[dom] = {}
            breakdown[dom][stream] = profit
    return breakdown


def _breakdown_by_time(df: pd.DataFrame) -> Dict[str, Dict[str, float]]:
    """Time breakdown: {time: {stream: profit}}"""
    breakdown = {}
    if 'Time' in df.columns and 'Stream' in df.columns:
        valid_df = df[df['Time'].notna() & (df['Time'] != 'NA') & (df['Time'] != '00:00')].copy()
        grouped = valid_df.groupby(['Time', 'Stream'])['ProfitDollars'].sum().reset_index()
        for _, row in grouped.iterrows():
            time = str(row['Time']).strip()
            stream = str(row['Stream'])
            profit = float(row['ProfitDollars'])
            if time not in breakdown:
                breakdown[time] = {}
            breakdown[time][stream] = profit
        logger.info(f"Time breakdown: {len(breakdown)} time slots")
    return breakdown


def _breakdown_by_month(df: pd.DataFrame) -> Dict[str, Dict[str, float]]:
    """Month breakdown: {YYYY-MM: {stream: profit}}"""
    breakdown = {}
    if 'trade_date' not in df.columns or 'Stream' not in df.columns:
        return breakdown
    if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
        raise ValueError(f"trade_date is {df['trade_date'].dtype}, cannot use .dt accessor")
    df_month = df.copy()
    df_month['year_month_str'] = df_month['trade_date'].dt.strftime('%Y-%m')
    grouped = df_month.groupby(['year_month_str', 'Stream'])['ProfitDollars'].sum().reset_index()
    for row in grouped.itertuples(index=False):
        year_month = str(row.year_month_str).strip()
        stream = str(row.Stream)
        profit = float(row.ProfitDollars)
        if year_month not in breakdown:
            breakdown[year_month] = {}
        breakdown[year_month][stream] = profit
    return breakdown


def _breakdown_by_year(df: pd.DataFrame) -> Dict[int, Dict[str, float]]:
    """Year breakdown: {year: {stream: profit}}"""
    breakdown = {}
    if 'trade_date' not in df.columns or 'Stream' not in df.columns:
        return breakdown
    if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
        raise ValueError(f"trade_date is {df['trade_date'].dtype}, cannot use .dt accessor")
    df = df.copy()
    df['year'] = df['trade_date'].dt.year
    grouped = df.groupby(['year', 'Stream'])['ProfitDollars'].sum().reset_index()
    for row in grouped.itertuples(index=False):
        year = int(row.year)
        stream = str(row.Stream)
        profit = float(row.ProfitDollars)
        if year not in breakdown:
            breakdown[year] = {}
        breakdown[year][stream] = profit
    return breakdown


def calculate_breakdown(
    df: pd.DataFrame,
    breakdown_type: str,
    stream_filters: Optional[Dict[str, Any]] = None,
    use_filtered: bool = False
) -> tuple:
    """
    Calculate profit breakdown by type.
    Returns (breakdown_dict, total_rows) where breakdown_dict is {key: {stream: profit}}.
    """
    df_filtered = _apply_filters(df, stream_filters, use_filtered)
    total_rows = len(df_filtered)

    if breakdown_type in ['day', 'dow']:
        breakdown = _breakdown_by_day(df_filtered)
    elif breakdown_type == 'dom':
        breakdown = _breakdown_by_dom(df_filtered)
    elif breakdown_type == 'time':
        breakdown = _breakdown_by_time(df_filtered)
    elif breakdown_type == 'month':
        breakdown = _breakdown_by_month(df_filtered)
    elif breakdown_type == 'year':
        breakdown = _breakdown_by_year(df_filtered)
    else:
        logger.warning(f"Unknown breakdown_type: {breakdown_type}")
        breakdown = {}

    return breakdown, total_rows
