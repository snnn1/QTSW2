"""data_stall_threshold_seconds_for_execution_instrument overrides."""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.config import (
    DATA_STALL_THRESHOLD_SECONDS,
    data_stall_threshold_seconds_for_execution_instrument,
)


def test_default_threshold_for_unknown_root():
    assert data_stall_threshold_seconds_for_execution_instrument("MES 06-26") == float(
        DATA_STALL_THRESHOLD_SECONDS
    )


def test_mng_override():
    assert data_stall_threshold_seconds_for_execution_instrument("MNG 05-26") == 420.0


def test_empty_string_falls_back():
    assert data_stall_threshold_seconds_for_execution_instrument("") == float(
        DATA_STALL_THRESHOLD_SECONDS
    )
