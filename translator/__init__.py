"""
Data Translator Backend
All processing logic separated from UI
"""

from .file_loader import (
    get_data_files,
    load_single_file,
    detect_file_format,
    get_file_years
)

from .core import (
    process_data,
    root_symbol,
    infer_contract_from_filename
)

from .frequency_detector import (
    detect_data_frequency,
    is_tick_data,
    is_minute_data,
    get_data_type_summary
)

from .contract_rollover import (
    parse_contract_month,
    detect_multiple_contracts,
    create_continuous_series,
    needs_rollover
)

__all__ = [
    "get_data_files",
    "load_single_file",
    "detect_file_format",
    "get_file_years",
    "process_data",
    "root_symbol",
    "infer_contract_from_filename",
    "detect_data_frequency",
    "is_tick_data",
    "is_minute_data",
    "get_data_type_summary",
    "parse_contract_month",
    "detect_multiple_contracts",
    "create_continuous_series",
    "needs_rollover"
]

