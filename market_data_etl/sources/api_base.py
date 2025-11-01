"""
Base API Source - Shared Utilities
Optional shared base class and utilities for API data sources
"""

from abc import ABC, abstractmethod
from typing import Optional, Dict, Any, List
from datetime import datetime
import pandas as pd
import requests
from pathlib import Path


class BaseAPISource(ABC):
    """Base class for API data sources - provides common functionality"""
    
    def __init__(self, name: str, config: Dict[str, Any]):
        """
        Initialize API source
        
        Args:
            name: Source name
            config: Configuration dictionary
        """
        self.name = name
        self.config = config
        self.base_url = config.get('base_url', '')
        self.api_key = config.get('api_key', '')
        self.timezone = config.get('timezone', 'America/Chicago')
        self.session = requests.Session()
    
    @abstractmethod
    def fetch_data(
        self,
        instrument: str,
        start_date: Optional[datetime] = None,
        end_date: Optional[datetime] = None,
        frequency: str = "1min"
    ) -> pd.DataFrame:
        """
        Fetch data from API
        
        Args:
            instrument: Instrument symbol
            start_date: Start date
            end_date: End date
            frequency: Data frequency
            
        Returns:
            DataFrame with standard columns
        """
        pass
    
    def _make_request(self, endpoint: str, params: Dict[str, Any] = None, 
                     method: str = "GET", data: Dict[str, Any] = None,
                     headers: Dict[str, str] = None) -> Dict[str, Any]:
        """
        Make HTTP request to API
        
        Args:
            endpoint: API endpoint
            params: Query parameters (for GET requests)
            method: HTTP method ("GET" or "POST")
            data: Request body data (for POST requests)
            headers: Additional headers
            
        Returns:
            JSON response
        """
        url = f"{self.base_url}{endpoint}"
        
        if headers is None:
            headers = {}
        
        if params is None:
            params = {}
        
        # Add API key if configured (can be in params or headers)
        if self.api_key:
            if method == "GET":
                params['apiKey'] = self.api_key
            else:
                if data is None:
                    data = {}
                data['apiKey'] = self.api_key
        
        # Make request based on method
        try:
            if method.upper() == "POST":
                response = self.session.post(url, json=data, params=params, headers=headers, timeout=30)
            else:
                response = self.session.get(url, params=params, headers=headers, timeout=30)
            
            # Don't suppress HTTP errors - bubble them up with full details
            try:
                response.raise_for_status()
            except requests.exceptions.HTTPError as http_err:
                # Print detailed error information
                print(f"HTTP Error {response.status_code} for {method} {url}")
                print(f"Request headers: {headers}")
                print(f"Request data: {data}")
                print(f"Response status: {response.status_code}")
                print(f"Response headers: {dict(response.headers)}")
                print(f"Response text: {response.text}")
                # Re-raise with full context
                raise
            
            # Parse JSON response
            try:
                return response.json()
            except ValueError as json_err:
                print(f"Failed to parse JSON response")
                print(f"Response status: {response.status_code}")
                print(f"Response text: {response.text}")
                raise ValueError(f"Invalid JSON response: {json_err}") from json_err
                
        except requests.exceptions.RequestException as req_err:
            # Print request error details
            print(f"Request exception for {method} {url}")
            print(f"Error type: {type(req_err).__name__}")
            print(f"Error message: {str(req_err)}")
            raise
    
    def _standardize_dataframe(self, df: pd.DataFrame, instrument: str) -> pd.DataFrame:
        """
        Standardize DataFrame to common format
        
        Args:
            df: Raw DataFrame from API
            instrument: Instrument symbol
            
        Returns:
            Standardized DataFrame with columns: timestamp, open, high, low, close, volume, instrument
        """
        # Ensure required columns exist
        required_cols = ['timestamp', 'open', 'high', 'low', 'close', 'volume']
        
        # Add instrument column
        df['instrument'] = instrument
        
        # Ensure timestamp is datetime and timezone-aware
        if 'timestamp' in df.columns:
            df['timestamp'] = pd.to_datetime(df['timestamp'])
            if df['timestamp'].dt.tz is None:
                df['timestamp'] = df['timestamp'].dt.tz_localize('UTC').dt.tz_convert(self.timezone)
        
        # Convert numeric columns
        numeric_cols = ['open', 'high', 'low', 'close', 'volume']
        for col in numeric_cols:
            if col in df.columns:
                df[col] = pd.to_numeric(df[col], errors='coerce')
        
        # Sort by timestamp
        df = df.sort_values('timestamp').reset_index(drop=True)
        
        return df
    
    def validate_connection(self) -> bool:
        """
        Validate API connection
        
        Returns:
            True if connection is valid
        """
        try:
            # Implementation depends on API - override in subclasses
            return True
        except Exception:
            return False
    
    def __repr__(self) -> str:
        return f"{self.__class__.__name__}(name={self.name})"

