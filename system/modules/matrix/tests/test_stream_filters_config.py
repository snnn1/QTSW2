from pathlib import Path
import sys

REPO_ROOT = Path(__file__).resolve().parents[4]
SYSTEM_ROOT = REPO_ROOT / "system"
sys.path.insert(0, str(SYSTEM_ROOT))

from modules.matrix import api


def test_stream_filters_config_round_trip_preserves_master_include_streams():
    original_path = api.STREAM_FILTERS_CONFIG_PATH
    test_path = Path(__file__).with_name("_stream_filters_config_test.json")
    api.STREAM_FILTERS_CONFIG_PATH = test_path
    try:
        saved = api._write_stream_filters_config(
            {
                "master": {
                    "exclude_days_of_week": ["Monday"],
                    "exclude_days_of_month": [11],
                    "exclude_times": ["07:30"],
                    "include_streams": ["es1", "NQ2"],
                    "include_years": [2026],
                },
                "ES1": {
                    "exclude_days_of_week": [],
                    "exclude_days_of_month": [],
                    "exclude_times": ["08:00"],
                },
            }
        )

        assert saved["master"]["include_streams"] == ["ES1", "NQ2"]
        assert "include_years" not in saved["master"]

        loaded = api._read_stream_filters_config()
        assert loaded == saved
        assert loaded["ES1"]["exclude_times"] == ["08:00"]
    finally:
        api.STREAM_FILTERS_CONFIG_PATH = original_path
        test_path.unlink(missing_ok=True)
        test_path.with_suffix(".json.tmp").unlink(missing_ok=True)


def test_stream_filters_to_dict_does_not_drop_master_include_streams():
    normalized = api._stream_filters_to_dict(
        {
            "master": {
                "exclude_days_of_week": [],
                "exclude_days_of_month": [],
                "exclude_times": [],
                "include_streams": ["ym1"],
            }
        }
    )

    assert normalized == {
        "master": {
            "exclude_days_of_week": [],
            "exclude_days_of_month": [],
            "exclude_times": [],
            "include_streams": ["YM1"],
        }
    }


def test_empty_filter_config_write_keeps_master_anchor():
    original_path = api.STREAM_FILTERS_CONFIG_PATH
    test_path = Path(__file__).with_name("_stream_filters_empty_config_test.json")
    api.STREAM_FILTERS_CONFIG_PATH = test_path
    try:
        saved = api._write_stream_filters_config({})
        assert saved == {
            "master": {
                "exclude_days_of_week": [],
                "exclude_days_of_month": [],
                "exclude_times": [],
                "include_streams": [],
            }
        }
    finally:
        api.STREAM_FILTERS_CONFIG_PATH = original_path
        test_path.unlink(missing_ok=True)
        test_path.with_suffix(".json.tmp").unlink(missing_ok=True)
