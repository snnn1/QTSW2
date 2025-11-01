"""
Test script for Tradovate API Source
Example usage of fetch_data method
"""

from market_data_etl.sources import TradovateSource
from datetime import datetime

# Create Tradovate source
source = TradovateSource(
    name='tradovate',
    config={
        'username': 'YOUR_USERNAME',
        'password': 'YOUR_PASSWORD',
        'base_url': 'https://live.tradovateapi.com'
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

