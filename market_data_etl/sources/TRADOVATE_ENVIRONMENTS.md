# Tradovate API Environments

Tradovate operates **two completely separate API hosts** that must be matched to your account type.

## Account Types

| Account Type | Base URL |
|-------------|----------|
| **Live funded account** | `https://live.tradovateapi.com` |
| **Demo / SIM account** | `https://demo.tradovateapi.com` |

## Important Notes

⚠️ **You must use the correct base_url for your account type.**
- A demo account will **not work** with the live API URL
- A live account will **not work** with the demo API URL

## Usage Examples

### Using Demo Account (Default)

```python
from market_data_etl.sources import TradovateSource

source = TradovateSource(
    name='tradovate',
    config={
        'username': 'your_username',
        'password': 'your_password',
        # Demo is default, but explicit is better:
        'base_url': 'https://demo.tradovateapi.com'
        # OR use shortcut:
        # 'account_type': 'demo'
    }
)
```

### Using Live Account

```python
source = TradovateSource(
    name='tradovate',
    config={
        'username': 'your_username',
        'password': 'your_password',
        'base_url': 'https://live.tradovateapi.com'
        # OR use shortcut:
        # 'account_type': 'live'
    }
)
```

## Authentication

Both environments use the same endpoint:
- `/auth/accesstokenrequest` (lowercase)

The difference is only in the base URL.

