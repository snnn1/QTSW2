from pathlib import Path
import sys


SYSTEM_ROOT = Path(__file__).resolve().parent.parent
QTSW2_ROOT = SYSTEM_ROOT.parent
sys.path.insert(0, str(SYSTEM_ROOT))


def test_merger_uses_repo_level_data_and_logs():
    from modules.merger import merger

    assert merger.BASE_DIR == QTSW2_ROOT
    assert merger.DATA_DIR == QTSW2_ROOT / "data"
    assert merger.LOGS_DIR == QTSW2_ROOT / "logs"
    assert merger.ANALYZER_TEMP_DIR == QTSW2_ROOT / "data" / "analyzer_temp"
    assert merger.ANALYZER_RUNS_DIR == QTSW2_ROOT / "data" / "analyzed"
