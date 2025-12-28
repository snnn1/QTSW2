"""
Configuration Loader

Loads and provides access to JSON configuration files from configs/ directory.
All configuration should be stored in JSON files, not hardcoded in Python.
"""

from pathlib import Path
from typing import Dict, Any, Optional
import json
import logging

logger = logging.getLogger(__name__)


class ConfigLoader:
    """
    Loads JSON configuration files from configs/ directory.
    
    Usage:
        loader = ConfigLoader()
        configs = loader.load()
        qtsw2_root = Path(configs['paths']['base_paths']['qtsw2_root'])
    """
    
    def __init__(self, config_dir: Optional[Path] = None):
        """
        Initialize config loader.
        
        Args:
            config_dir: Path to configs directory. Defaults to configs/ relative to this file.
        """
        if config_dir is None:
            config_dir = Path(__file__).parent
        self.config_dir = Path(config_dir)
        
        if not self.config_dir.exists():
            raise FileNotFoundError(
                f"Config directory not found: {self.config_dir}. "
                f"Expected configs/ folder at project root."
            )
    
    def load(self) -> Dict[str, Any]:
        """
        Load all configuration files.
        
        Returns:
            Dictionary with keys: 'paths', 'pipeline', 'instruments', 'schedule'
        
        Raises:
            FileNotFoundError: If required config files are missing
            json.JSONDecodeError: If config files contain invalid JSON
        """
        configs = {}
        
        # Load each config file
        config_files = {
            'paths': 'paths.json',
            'pipeline': 'pipeline.json',
            'instruments': 'instruments.json',
            'schedule': 'schedule.json'
        }
        
        for key, filename in config_files.items():
            try:
                configs[key] = self._load_json(filename)
                logger.debug(f"Loaded config: {filename}")
            except FileNotFoundError as e:
                logger.warning(f"Config file not found: {filename}. Using defaults.")
                configs[key] = {}
            except json.JSONDecodeError as e:
                logger.error(f"Invalid JSON in {filename}: {e}")
                raise
        
        return configs
    
    def _load_json(self, filename: str) -> Dict[str, Any]:
        """
        Load a single JSON config file.
        
        Args:
            filename: Name of JSON file in configs/ directory
            
        Returns:
            Parsed JSON as dictionary
            
        Raises:
            FileNotFoundError: If file doesn't exist
            json.JSONDecodeError: If JSON is invalid
        """
        filepath = self.config_dir / filename
        
        if not filepath.exists():
            raise FileNotFoundError(f"Config file not found: {filepath}")
        
        with open(filepath, 'r', encoding='utf-8') as f:
            try:
                return json.load(f)
            except json.JSONDecodeError as e:
                raise json.JSONDecodeError(
                    f"Invalid JSON in {filename}: {e.msg}",
                    e.doc,
                    e.pos
                )
    
    def get_path(self, *keys: str, default: Any = None) -> Any:
        """
        Get a nested config value using dot notation keys.
        
        Args:
            *keys: Path to value (e.g., 'paths', 'base_paths', 'qtsw2_root')
            default: Default value if path doesn't exist
            
        Returns:
            Config value or default
            
        Example:
            loader.get_path('paths', 'base_paths', 'qtsw2_root')
            loader.get_path('pipeline', 'timeouts', 'translator_seconds')
        """
        configs = self.load()
        value = configs
        
        try:
            for key in keys:
                value = value[key]
            return value
        except (KeyError, TypeError):
            if default is not None:
                return default
            raise KeyError(f"Config path not found: {'/'.join(keys)}")


# Global instance for convenience
_global_loader = None


def get_config_loader() -> ConfigLoader:
    """Get or create global config loader instance."""
    global _global_loader
    if _global_loader is None:
        _global_loader = ConfigLoader()
    return _global_loader


def load_configs() -> Dict[str, Any]:
    """
    Convenience function to load all configs.
    
    Returns:
        Dictionary with all configuration loaded from JSON files
    """
    return get_config_loader().load()


















