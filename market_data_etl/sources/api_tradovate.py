"""
Tradovate API Source
Fetches market data from Tradovate API
"""

from typing import Optional, Dict, Any
from datetime import datetime
import pandas as pd

from .api_base import BaseAPISource


class TradovateSource(BaseAPISource):
    """
    Tradovate API data source
    
    Tradovate operates two completely separate API hosts:
    - Live funded account: https://live.tradovateapi.com
    - Demo / SIM account: https://demo.tradovateapi.com (default)
    
    You must use the correct base_url for your account type.
    """
    
    # Tradovate API host URLs
    LIVE_API_URL = 'https://live.tradovateapi.com'
    DEMO_API_URL = 'https://demo.tradovateapi.com'
    
    def __init__(self, name: str, config: Dict[str, Any]):
        """
        Initialize Tradovate source
        
        Args:
            name: Source name
            config: Configuration with:
                - api_key: Tradovate API key (optional)
                - base_url: API base URL 
                    - Demo/SIM: https://demo.tradovateapi.com (default)
                    - Live funded: https://live.tradovateapi.com
                - username: Username for authentication
                - password: Password for authentication
                - account_type: Optional shortcut: 'demo' or 'live' (overrides base_url)
        """
        # Handle account_type shortcut
        if 'account_type' in config:
            account_type = config.pop('account_type').lower()
            if account_type == 'live':
                config['base_url'] = self.LIVE_API_URL
            elif account_type == 'demo':
                config['base_url'] = self.DEMO_API_URL
            else:
                raise ValueError(f"Invalid account_type: {account_type}. Must be 'demo' or 'live'")
        
        # Set default base URL if not provided (demo environment by default)
        if 'base_url' not in config:
            config['base_url'] = self.DEMO_API_URL
        
        super().__init__(name, config)
        
        self.username = config.get('username', '')
        self.password = config.get('password', '')
        self.access_token = config.get('access_token', '')
        self.authenticated = False
    
    def login(self) -> bool:
        """
        Login to Tradovate API using username and password
        
        Uses the base _make_request() utility to authenticate.
        
        Returns:
            True if login successful, False otherwise
        
        Raises:
            ConnectionError: If authentication fails
            requests.exceptions.HTTPError: If HTTP request fails
        """
        if self.authenticated and self.access_token:
            print("Tradovate: Already authenticated")
            return True
        
        if not self.username or not self.password:
            raise ValueError("Tradovate: Username and password required for login")
        
        # Verify base URL matches expected format
        if self.base_url not in [self.LIVE_API_URL, self.DEMO_API_URL]:
            print(f"Tradovate: Warning - base_url '{self.base_url}' may not be correct")
            print(f"  Expected: {self.LIVE_API_URL} or {self.DEMO_API_URL}")
        
        print(f"Tradovate: Attempting login to {self.base_url}")
        print(f"Tradovate: Username: {self.username}")
        print(f"Tradovate: Endpoint: /auth/accesstokenrequest")
        
        try:
            # Tradovate login endpoint (same for both live and demo)
            # Official format requires: name, password, appId, appVersion, cid
            login_data = {
                'name': self.username,
                'password': self.password,
                'appId': 'Trader',
                'appVersion': '1.0',
                'cid': 0
            }
            
            print(f"Tradovate: Login payload: {login_data}")
            
            # Use _make_request() utility with POST method
            # Endpoint is lowercase: accesstokenrequest
            response = self._make_request(
                endpoint='/auth/accesstokenrequest',
                method='POST',
                data=login_data
            )
            
            # Debug: Print full response
            print(f"Tradovate: Response received:")
            print(f"  Type: {type(response)}")
            print(f"  Keys: {list(response.keys()) if isinstance(response, dict) else 'Not a dict'}")
            print(f"  Full response: {response}")
            
            # Extract access token from response
            # Tradovate typically returns: {'accessToken': '...', 'userId': ..., 'expirationTime': ...}
            if 'accessToken' in response:
                self.access_token = response['accessToken']
                self.authenticated = True
                
                # Store token expiration if provided
                if 'expirationTime' in response:
                    self.token_expiration = response['expirationTime']
                
                # Store user ID if provided
                if 'userId' in response:
                    self.user_id = response['userId']
                
                # Update session headers with token for future requests
                self.session.headers.update({
                    'Authorization': f'Bearer {self.access_token}'
                })
                
                print(f"Tradovate: ✓ Successfully authenticated as {self.username}")
                print(f"Tradovate: Access token: {self.access_token[:20]}...")
                return True
            else:
                error_msg = f"Tradovate: Login failed - no access token in response"
                print(error_msg)
                print(f"Tradovate: Response keys: {list(response.keys()) if isinstance(response, dict) else 'N/A'}")
                print(f"Tradovate: Full response body: {response}")
                raise ConnectionError(error_msg)
                
        except Exception as e:
            # Don't suppress exceptions - bubble them up clearly
            print(f"Tradovate: ✗ Login error occurred")
            print(f"Tradovate: Error type: {type(e).__name__}")
            print(f"Tradovate: Error message: {str(e)}")
            
            # If it's an HTTP error, print more details
            if hasattr(e, 'response'):
                print(f"Tradovate: HTTP Status: {e.response.status_code}")
                print(f"Tradovate: Response text: {e.response.text if hasattr(e.response, 'text') else 'N/A'}")
            
            self.authenticated = False
            # Re-raise the exception so it's visible
            raise
    
    def _authenticate(self):
        """Authenticate with Tradovate API (calls login if needed)"""
        if self.authenticated:
            return
        
        # If we have an access token in config, use it
        if self.access_token and not self.authenticated:
            self.session.headers.update({
                'Authorization': f'Bearer {self.access_token}'
            })
            self.authenticated = True
            return
        
        # Otherwise, try to login with username/password
        if self.username and self.password:
            self.login()
    
    def fetch_data(
        self,
        instrument: str,
        start_date: Optional[datetime] = None,
        end_date: Optional[datetime] = None,
        frequency: str = "1min"
    ) -> pd.DataFrame:
        """
        Fetch historical data from Tradovate after authentication
        
        Args:
            instrument: Symbol (ES, NQ, etc.) - alias for symbol parameter
            start_date: Start date (datetime or None)
            end_date: End date (datetime or None)
            frequency: Data timeframe ("Minute", "Tick", etc.)
            
        Returns:
            DataFrame with columns: timestamp, open, high, low, close, volume, instrument
        """
        # Ensure authenticated
        if not getattr(self, "access_token", None):
            if not self.login():
                raise ConnectionError("Tradovate authentication failed")
        
        # Map instrument to Tradovate symbol
        symbol = self._map_instrument(instrument)
        
        # Map frequency to Tradovate timeframe
        timeframe_map = {
            "1min": "Minute",
            "5min": "Minute",
            "15min": "Minute",
            "tick": "Tick",
            "Tick": "Tick",
            "Minute": "Minute"
        }
        timeframe = timeframe_map.get(frequency, "Minute")
        
        # Convert dates to strings if provided, otherwise use defaults
        if start_date:
            start = start_date.strftime("%Y-%m-%d")
        else:
            start = "2024-01-01"
        
        if end_date:
            end = end_date.strftime("%Y-%m-%d")
        else:
            end = "2024-01-02"
        
        # Build endpoint URL
        endpoint = f"/api/marketdata/history/{symbol}/{timeframe}?startTime={start}&endTime={end}"
        
        try:
            # Use _make_request() utility with GET method
            response = self._make_request(
                endpoint=endpoint,
                method='GET'
            )
            
            # Handle both tick and bar data formats
            if "priceBars" in response:
                key = "priceBars"
            elif "ticks" in response:
                key = "ticks"
            else:
                # Try other common keys
                if "data" in response:
                    key = "data"
                elif "bars" in response:
                    key = "bars"
                else:
                    print(f"Tradovate: No data returned for {symbol} - unexpected response format")
                    return pd.DataFrame()
            
            records = response.get(key, [])
            if not records:
                print(f"Tradovate: No data returned for {symbol}")
                return pd.DataFrame()
            
            df = pd.DataFrame(records)
            
            # Parse timestamp
            if "timestamp" in df.columns:
                df["timestamp"] = pd.to_datetime(df["timestamp"], utc=True)
            elif "time" in df.columns:
                df["timestamp"] = pd.to_datetime(df["time"], utc=True)
            elif "t" in df.columns:
                df["timestamp"] = pd.to_datetime(df["t"], utc=True)
            
            # Handle price bars format (OHLCV)
            if key == "priceBars" or timeframe == "Minute":
                # Map columns if needed
                column_mapping = {}
                if "open" not in df.columns:
                    if "o" in df.columns:
                        column_mapping["o"] = "open"
                if "high" not in df.columns:
                    if "h" in df.columns:
                        column_mapping["h"] = "high"
                if "low" not in df.columns:
                    if "l" in df.columns:
                        column_mapping["l"] = "low"
                if "close" not in df.columns:
                    if "c" in df.columns:
                        column_mapping["c"] = "close"
                if "volume" not in df.columns:
                    if "v" in df.columns:
                        column_mapping["v"] = "volume"
                
                if column_mapping:
                    df = df.rename(columns=column_mapping)
                
                # Ensure all required columns exist
                for col in ["open", "high", "low", "close"]:
                    if col not in df.columns:
                        # If we have price column, use it for OHLC
                        if "price" in df.columns:
                            df[col] = df["price"]
                        else:
                            df[col] = 0.0
                
                if "volume" not in df.columns:
                    df["volume"] = 0
            
            # Handle tick data format (price/volume)
            else:  # key == "ticks" or tick data
                # Map price column if needed
                if "price" not in df.columns:
                    if "p" in df.columns:
                        df = df.rename(columns={"p": "price"})
                
                # For tick data, create OHLC from price
                if "price" in df.columns:
                    # Group by timestamp or use price as OHLC
                    df["open"] = df["price"]
                    df["high"] = df["price"]
                    df["low"] = df["price"]
                    df["close"] = df["price"]
                else:
                    # Fallback
                    df["open"] = 0.0
                    df["high"] = 0.0
                    df["low"] = 0.0
                    df["close"] = 0.0
                
                if "volume" not in df.columns:
                    if "v" in df.columns:
                        df = df.rename(columns={"v": "volume"})
                    else:
                        df["volume"] = 1  # Default volume for ticks
            
            # Standardize to common format using base class method
            df = self._standardize_dataframe(df, instrument)
            
            return df
            
        except Exception as e:
            print(f"Tradovate: Error fetching data for {symbol}: {e}")
            return pd.DataFrame()
    
    def _map_instrument(self, instrument: str) -> str:
        """
        Map instrument symbol to Tradovate symbol
        
        Args:
            instrument: Standard symbol (ES, NQ, etc.)
            
        Returns:
            Tradovate symbol format
        """
        # Tradovate might use different format
        # This is a placeholder - adjust based on actual Tradovate requirements
        mapping = {
            'ES': 'ES',
            'NQ': 'NQ',
            'YM': 'YM',
            'CL': 'CL',
            'NG': 'NG',
            'GC': 'GC',
        }
        return mapping.get(instrument.upper(), instrument.upper())
    
    def validate_connection(self) -> bool:
        """Validate Tradovate API connection"""
        try:
            # Try to authenticate
            if self.access_token:
                # Already have token, just verify it works
                self._authenticate()
                return self.authenticated
            elif self.username and self.password:
                # Try to login
                return self.login()
            elif self.api_key:
                # API key authentication (if supported)
                return True
            else:
                return False
        except Exception:
            return False

