from __future__ import annotations

import sys
from pathlib import Path

import pandas as pd

REPO_ROOT = Path(__file__).resolve().parents[4]
SYSTEM_ROOT = REPO_ROOT / "system"
sys.path.insert(0, str(SYSTEM_ROOT))

from modules.timetable.timetable_engine import TimetableEngine  # noqa: E402


def test_execution_timetable_blocks_streams_outside_master_include_streams():
    matrix_df = pd.DataFrame(
        [
            {
                "Stream": "ES1",
                "trade_date": pd.Timestamp("2026-05-11"),
                "Time": "08:00",
                "final_allowed": True,
                "filter_reasons": "",
            },
            {
                "Stream": "NQ1",
                "trade_date": pd.Timestamp("2026-05-11"),
                "Time": "08:00",
                "final_allowed": True,
                "filter_reasons": "",
            },
        ]
    )
    engine = TimetableEngine(project_root=str(REPO_ROOT))
    engine.streams = ["ES1", "NQ1"]

    streams = engine.build_streams_from_master_matrix(
        matrix_df,
        trade_date="2026-05-12",
        stream_filters={"master": {"include_streams": ["ES1"]}},
        execution_mode=True,
        matrix_as_of_session=True,
    )

    by_stream = {row["stream"]: row for row in streams}
    assert by_stream["ES1"]["enabled"] is True
    assert by_stream["NQ1"]["enabled"] is False
    assert by_stream["NQ1"]["block_reason"] == "master_stream_not_included"
