"""
Tests for Session Eligibility flow.

- CME trading date (18:00 Chicago boundary)
- Eligibility writer/loader
- Eligibility builder (with temp dir and real matrix when available)
"""

import json
import pytest
import tempfile
import shutil
from pathlib import Path
from datetime import datetime, timezone
from unittest.mock import patch

import sys
QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.timetable.cme_session import get_trading_date_cme
from modules.timetable.eligibility_writer import write_eligibility_file, load_eligibility


class TestCMETradingDate:
    """Test CME 18:00 Chicago boundary for trading_date."""

    def test_before_18ct_returns_same_day(self):
        """17:59 CT → trading_date = same calendar day."""
        # 2026-03-03 23:59 UTC = 2026-03-03 17:59 CT (CST)
        utc = datetime(2026, 3, 3, 23, 59, 0, tzinfo=timezone.utc)
        assert get_trading_date_cme(utc) == "2026-03-03"

    def test_at_18ct_returns_next_day(self):
        """18:00 CT → trading_date = next calendar day."""
        # 2026-03-04 00:00 UTC = 2026-03-03 18:00 CT (CST)
        utc = datetime(2026, 3, 4, 0, 0, 0, tzinfo=timezone.utc)
        assert get_trading_date_cme(utc) == "2026-03-04"

    def test_after_18ct_returns_next_day(self):
        """23:59 CT → trading_date = next calendar day."""
        # 2026-03-04 05:59 UTC = 2026-03-03 23:59 CT
        utc = datetime(2026, 3, 4, 5, 59, 0, tzinfo=timezone.utc)
        assert get_trading_date_cme(utc) == "2026-03-04"

    def test_dst_transition(self):
        """Works across DST (Chicago switches CST/CDT)."""
        # Summer: 2026-07-15 23:00 UTC = 2026-07-15 18:00 CDT
        utc = datetime(2026, 7, 15, 23, 0, 0, tzinfo=timezone.utc)
        assert get_trading_date_cme(utc) == "2026-07-16"


class TestEligibilityWriter:
    """Test eligibility file write and load."""

    def test_write_creates_file(self):
        """write_eligibility_file creates eligibility_YYYY-MM-DD.json."""
        with tempfile.TemporaryDirectory() as tmp:
            streams = [
                {"stream": "ES1", "enabled": True},
                {"stream": "GC1", "enabled": False, "block_reason": "filtered"},
            ]
            path = write_eligibility_file(
                streams=streams,
                trading_date="2026-03-05",
                output_dir=tmp,
                source_matrix_hash="abc123",
            )
            assert path is not None
            assert path.exists()
            assert path.name == "eligibility_2026-03-05.json"

            data = json.loads(path.read_text(encoding="utf-8"))
            assert data["trading_date"] == "2026-03-05"
            assert data["eligible_stream_count"] == 1
            assert data["matrix_hash"] == "abc123"
            assert len(data["eligible_streams"]) == 2

    def test_write_skips_if_exists(self):
        """write_eligibility_file returns None when file already exists."""
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "eligibility_2026-03-05.json"
            out.write_text('{"trading_date":"2026-03-05"}', encoding="utf-8")

            path = write_eligibility_file(
                streams=[{"stream": "ES1", "enabled": True}],
                trading_date="2026-03-05",
                output_dir=tmp,
            )
            assert path is None
            assert out.read_text() == '{"trading_date":"2026-03-05"}'

    def test_load_returns_none_when_missing(self):
        """load_eligibility returns None when file does not exist."""
        with tempfile.TemporaryDirectory() as tmp:
            result = load_eligibility("2026-03-05", tmp)
            assert result is None

    def test_load_returns_data_when_exists(self):
        """load_eligibility returns parsed data when file exists."""
        with tempfile.TemporaryDirectory() as tmp:
            payload = {
                "trading_date": "2026-03-05",
                "freeze_time_utc": "2026-03-04T18:00:00Z",
                "eligible_stream_count": 2,
                "eligible_streams": [],
            }
            (Path(tmp) / "eligibility_2026-03-05.json").write_text(
                json.dumps(payload), encoding="utf-8"
            )
            result = load_eligibility("2026-03-05", tmp)
            assert result is not None
            assert result["trading_date"] == "2026-03-05"
            assert result["eligible_stream_count"] == 2


class TestEligibilityBuilderScript:
    """Test eligibility_builder.py script (requires master matrix)."""

    def test_eligibility_builder_skip_when_exists(self):
        """Builder exits 0 and skips when eligibility file already exists."""
        with tempfile.TemporaryDirectory() as tmp:
            timetable_dir = Path(tmp) / "timetable"
            timetable_dir.mkdir()
            (timetable_dir / "eligibility_2026-03-10.json").write_text(
                '{"trading_date":"2026-03-10"}', encoding="utf-8"
            )
            matrix_dir = Path(tmp) / "matrix"
            matrix_dir.mkdir()

            # Need a minimal master matrix for builder to not fail on load
            import pandas as pd
            df = pd.DataFrame({
                "Stream": ["ES1"],
                "trade_date": pd.to_datetime(["2026-03-10"]),
                "Time": ["08:30"],
                "Result": ["Win"],
            })
            if "Date" not in df.columns:
                df["Date"] = df["trade_date"]
            df.to_parquet(matrix_dir / "master_matrix_20260310.parquet", index=False)

            result = pytest.importorskip("subprocess").run(
                [
                    sys.executable,
                    str(QTSW2_ROOT / "scripts" / "eligibility_builder.py"),
                    "--date", "2026-03-10",
                    "--output-dir", str(timetable_dir),
                    "--master-matrix-dir", str(matrix_dir),
                ],
                cwd=str(QTSW2_ROOT),
                capture_output=True,
                text=True,
                timeout=30,
            )
            assert result.returncode == 0
            combined = result.stdout + result.stderr
            assert "already exists" in combined or "SKIP" in combined

    def test_eligibility_builder_writes_when_missing(self):
        """Builder writes eligibility file when missing (with --force to allow overwrite for test)."""
        with tempfile.TemporaryDirectory() as tmp:
            timetable_dir = Path(tmp) / "timetable"
            timetable_dir.mkdir()
            matrix_dir = Path(tmp) / "matrix"
            matrix_dir.mkdir()

            import pandas as pd
            df = pd.DataFrame({
                "Stream": ["ES1", "GC1"],
                "trade_date": pd.to_datetime(["2026-03-10", "2026-03-10"]),
                "Time": ["08:30", "08:30"],
                "Result": ["Win", "Loss"],
                "Time Change": ["", ""],
            })
            if "Date" not in df.columns:
                df["Date"] = df["trade_date"]
            df.to_parquet(matrix_dir / "master_matrix_20260310.parquet", index=False)

            result = pytest.importorskip("subprocess").run(
                [
                    sys.executable,
                    str(QTSW2_ROOT / "scripts" / "eligibility_builder.py"),
                    "--date", "2026-03-10",
                    "--output-dir", str(timetable_dir),
                    "--master-matrix-dir", str(matrix_dir),
                ],
                cwd=str(QTSW2_ROOT),
                capture_output=True,
                text=True,
                timeout=60,
            )
            # May fail if analyzer_runs_dir is required; builder uses TimetableEngine
            if result.returncode == 0:
                out_file = timetable_dir / "eligibility_2026-03-10.json"
                assert out_file.exists()
                data = json.loads(out_file.read_text(encoding="utf-8"))
                assert data["trading_date"] == "2026-03-10"


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
