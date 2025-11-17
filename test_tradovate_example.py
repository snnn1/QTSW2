"""
Example: Using Tradovate API Source to fetch historical data
Run this from QTSW2 root directory
"""

import sys
from pathlib import Path

# Add QTSW2 root to path
QTSW2_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(QTSW2_ROOT))

from market_data_etl.sources import TradovateSource
from datetime import datetime

# Create Tradovate source
source = TradovateSource(
    name='tradovate',
    config={
        'username': 'jakechurchill',
        'password': 'Virginman$1',
        'base_url': 'https://demo.tradovateapi.com'  # Demo environment
    }
)

# Fetch historical data
df = source.fetch_data(
    instrument='ES',
    start_date=datetime(2024, 1, 1),
    end_date=datetime(2024, 1, 2),
    frequency='Minute'
)

print(df.head())

