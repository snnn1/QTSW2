"""
Centralized logging configuration for Master Matrix.

This module provides a single place to configure logging for all matrix modules,
eliminating duplication and ensuring consistent log formatting.
"""

import logging
import sys
from pathlib import Path
from typing import Optional


def setup_matrix_logger(
    logger_name: str,
    log_file: Optional[Path] = None,
    console: bool = True,
    level: int = logging.INFO
) -> logging.Logger:
    """
    Set up a logger for matrix modules with file and/or console handlers.
    
    This function ensures consistent logging configuration across all matrix modules.
    It checks for existing handlers to avoid duplicates.
    
    Args:
        logger_name: Name for the logger (e.g., 'modules.matrix.sequencer_logic')
        log_file: Path to log file. If None, uses default master_matrix.log
        console: Whether to add console (stderr) handler
        level: Logging level (default: INFO)
        
    Returns:
        Configured logger instance
    """
    logger = logging.getLogger(logger_name)
    
    # Check for existing handlers
    has_file_handler = any(isinstance(h, logging.FileHandler) for h in logger.handlers)
    has_console_handler = any(isinstance(h, logging.StreamHandler) for h in logger.handlers)
    
    # Use default log file if not provided
    if log_file is None:
        log_file = Path(__file__).parent.parent.parent / "logs" / "master_matrix.log"
    
    # Ensure log directory exists
    log_file.parent.mkdir(parents=True, exist_ok=True)
    
    # Add file handler if missing
    if not has_file_handler:
        file_handler = logging.FileHandler(log_file, mode='a', encoding='utf-8')
        file_handler.setLevel(level)
        file_formatter = logging.Formatter('%(asctime)s - %(levelname)s - %(message)s')
        file_handler.setFormatter(file_formatter)
        logger.addHandler(file_handler)
    
    # Add console handler if requested and missing
    if console and not has_console_handler:
        console_handler = logging.StreamHandler(sys.stderr)
        console_handler.setLevel(level)
        console_formatter = logging.Formatter('%(asctime)s - %(levelname)s - %(message)s')
        console_handler.setFormatter(console_formatter)
        logger.addHandler(console_handler)
    
    # Set logger level if not already set
    if logger.level == logging.NOTSET:
        logger.setLevel(level)
    
    return logger

