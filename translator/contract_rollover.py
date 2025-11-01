"""
Contract Rollover and Continuous Series
Merges multiple contract months into continuous, back-adjusted series
Only activates when multiple contracts detected per instrument
"""

from typing import Optional, Dict, List, Tuple
import pandas as pd
from datetime import datetime, timedelta
import re


# Futures contract month codes
FUTURES_MONTH_CODES = {
    'F': 1,   # January
    'G': 2,   # February
    'H': 3,   # March
    'J': 4,   # April
    'K': 5,   # May
    'M': 6,   # June
    'N': 7,   # July
    'Q': 8,   # August
    'U': 9,   # September
    'V': 10,  # October
    'X': 11,  # November
    'Z': 12   # December
}

# Reverse mapping
MONTH_TO_CODE = {v: k for k, v in FUTURES_MONTH_CODES.items()}


def parse_contract_month(contract_name: str) -> Optional[Tuple[str, int, int]]:
    """
    Parse contract name to extract instrument, month code, and year
    
    Args:
        contract_name: Contract name like "ES_U2024", "ESZ24", "MinuteDataExport_ES_U2024"
        
    Returns:
        Tuple of (instrument, month_code, year) or None if not parsable
        Example: ("ES", "U", 2024)
    """
    contract_upper = contract_name.upper()
    
    # Pattern 1: ES_U2024, NQ_Z2024 (instrument_month code_year)
    # Pattern 2: ESZ24, NQU24 (instrument_month code_year_short)
    # Pattern 3: ES_U2024_20250920 (instrument_month code_year_timestamp)
    
    # Try pattern: [A-Z]+_[A-Z][0-9]{2,4}
    pattern1 = r'([A-Z]+)[_\-]?([A-Z])([0-9]{2,4})'
    match = re.search(pattern1, contract_upper)
    
    if match:
        instrument = match.group(1)
        month_code = match.group(2)
        year_str = match.group(3)
        
        # Validate month code
        if month_code in FUTURES_MONTH_CODES:
            # Handle 2-digit years
            if len(year_str) == 2:
                year = 2000 + int(year_str)
            else:
                year = int(year_str)
            
            return (instrument, month_code, year)
    
    # Try pattern without separator: ESZ24
    pattern2 = r'([A-Z]+)([A-Z])([0-9]{2,4})$'
    match = re.search(pattern2, contract_upper)
    if match:
        instrument = match.group(1)
        month_code = match.group(2)
        year_str = match.group(3)
        
        if month_code in FUTURES_MONTH_CODES:
            if len(year_str) == 2:
                year = 2000 + int(year_str)
            else:
                year = int(year_str)
            
            return (instrument, month_code, year)
    
    return None


def detect_multiple_contracts(df: pd.DataFrame) -> Dict[str, List[str]]:
    """
    Detect if there are multiple contracts per instrument
    
    Args:
        df: DataFrame with 'instrument' and 'contract' columns
        
    Returns:
        Dictionary mapping instrument -> list of contract names
        Only includes instruments with multiple contracts
    """
    if 'contract' not in df.columns or 'instrument' not in df.columns:
        return {}
    
    # Group by instrument and get unique contracts
    contract_groups = df.groupby('instrument')['contract'].unique().to_dict()
    
    # Filter to only instruments with multiple contracts
    multiple_contracts = {
        inst: list(contracts) 
        for inst, contracts in contract_groups.items() 
        if len(contracts) > 1
    }
    
    return multiple_contracts


def calculate_rollover_date(contract_month_code: str, contract_year: int, 
                           days_before_expiration: int = 14) -> datetime:
    """
    Calculate rollover date for a futures contract
    
    Args:
        contract_month_code: Month code (U, Z, H, etc.)
        contract_year: Year
        days_before_expiration: Days before expiration to roll (default 14)
        
    Returns:
        Rollover date (usually 3rd Friday of month, 14 days before)
    """
    month = FUTURES_MONTH_CODES.get(contract_month_code, 1)
    
    # Futures typically expire 3rd Friday of month
    # For simplicity, we'll use the 15th of the month minus days_before_expiration
    # For ES/NQ/YM, expiration is typically 3rd Friday
    
    # Get first day of the contract month
    first_day = datetime(contract_year, month, 1)
    
    # Find 3rd Friday (typically expiration)
    # First Friday is 1 + (5 - first_day.weekday()) % 7
    first_weekday = first_day.weekday()  # 0=Monday, 4=Friday
    days_to_first_friday = (4 - first_weekday) % 7
    if days_to_first_friday == 0 and first_weekday != 4:
        days_to_first_friday = 7
    
    third_friday = first_day + timedelta(days=days_to_first_friday + 14)
    
    # Rollover date is typically 14 days before expiration
    rollover_date = third_friday - timedelta(days=days_before_expiration)
    
    return rollover_date


def create_continuous_series(
    df: pd.DataFrame, 
    rollover_days_before_exp: int = 14,
    back_adjust: bool = True
) -> pd.DataFrame:
    """
    Create continuous series from multiple contracts with back-adjustment
    
    Args:
        df: DataFrame with data from multiple contracts
        rollover_days_before_exp: Days before expiration to roll
        back_adjust: If True, back-adjust prices at rollover points
        
    Returns:
        Continuous series DataFrame with back-adjusted prices
    """
    if 'contract' not in df.columns or 'instrument' not in df.columns:
        return df  # Can't create continuous series without contract info
    
    # Check if multiple contracts exist
    multiple_contracts = detect_multiple_contracts(df)
    
    if not multiple_contracts:
        # No multiple contracts, return as-is
        return df
    
    # Process each instrument that has multiple contracts
    continuous_dfs = []
    
    for instrument, contracts in multiple_contracts.items():
        instrument_df = df[df['instrument'] == instrument].copy()
        
        # Parse contract months and sort chronologically
        contract_info = []
        for contract in contracts:
            parsed = parse_contract_month(contract)
            if parsed:
                inst, month_code, year = parsed
                rollover_date = calculate_rollover_date(month_code, year, rollover_days_before_exp)
                contract_info.append({
                    'contract': contract,
                    'month_code': month_code,
                    'year': year,
                    'rollover_date': rollover_date
                })
        
        if not contract_info:
            # Couldn't parse contracts, keep as-is
            continuous_dfs.append(instrument_df)
            continue
        
        # Sort by rollover date
        contract_info.sort(key=lambda x: x['rollover_date'])
        
        # Build continuous series
        continuous_data = []
        cumulative_adjustment = 0.0
        
        for i, contract_detail in enumerate(contract_info):
            contract_df = instrument_df[instrument_df['contract'] == contract_detail['contract']].copy()
            
            if contract_df.empty:
                continue
            
            # Determine which data to use (before/after rollover)
            if i > 0:
                # Use data before rollover date
                rollover_date = contract_detail['rollover_date']
                contract_df = contract_df[contract_df['timestamp'] < rollover_date]
            
            if contract_df.empty:
                continue
            
            if back_adjust and i > 0:
                # Calculate adjustment from previous contract's last price
                prev_contract_detail = contract_info[i-1]
                prev_contract_df = instrument_df[
                    instrument_df['contract'] == prev_contract_detail['contract']
                ].copy()
                
                if not prev_contract_df.empty:
                    # Get last price of previous contract (at rollover date or before)
                    rollover_date = contract_detail['rollover_date']
                    prev_data = prev_contract_df[prev_contract_df['timestamp'] <= rollover_date]
                    
                    if not prev_data.empty:
                        prev_last_close = prev_data.iloc[-1]['close']
                        
                        # Get first price of current contract (at rollover date or after)
                        current_data = instrument_df[
                            (instrument_df['contract'] == contract_detail['contract']) &
                            (instrument_df['timestamp'] >= rollover_date - timedelta(days=1))
                        ]
                        
                        if not current_data.empty:
                            current_first_close = current_data.iloc[0]['close']
                            
                            # Calculate adjustment
                            adjustment = prev_last_close - current_first_close
                            cumulative_adjustment += adjustment
                
                # Apply cumulative adjustment to prices
                price_cols = ['open', 'high', 'low', 'close']
                for col in price_cols:
                    if col in contract_df.columns:
                        contract_df[col] = contract_df[col] + cumulative_adjustment
            
            continuous_data.append(contract_df)
        
        if continuous_data:
            # Combine all contracts
            continuous_df = pd.concat(continuous_data, ignore_index=True)
            continuous_df = continuous_df.sort_values('timestamp').reset_index(drop=True)
            
            # Remove duplicate timestamps (keep closest to rollover)
            continuous_df = continuous_df.drop_duplicates(subset=['timestamp'], keep='first')
            
            # Remove contract column (or keep it for reference)
            # continuous_df = continuous_df.drop(columns=['contract'])
            
            continuous_dfs.append(continuous_df)
        else:
            # Fallback: keep original
            continuous_dfs.append(instrument_df)
    
    # Combine all instruments
    if continuous_dfs:
        # Get instruments that didn't need rollover
        instruments_processed = set(multiple_contracts.keys())
        remaining_df = df[~df['instrument'].isin(instruments_processed)].copy()
        
        if not remaining_df.empty:
            continuous_dfs.append(remaining_df)
        
        result = pd.concat(continuous_dfs, ignore_index=True)
        result = result.sort_values('timestamp').reset_index(drop=True)
        return result
    
    return df


def needs_rollover(df: pd.DataFrame) -> bool:
    """
    Check if data needs contract rollover processing
    
    Args:
        df: DataFrame to check
        
    Returns:
        True if multiple contracts detected per instrument
    """
    return len(detect_multiple_contracts(df)) > 0

