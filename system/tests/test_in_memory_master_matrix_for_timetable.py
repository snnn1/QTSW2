"""In-memory master matrix is the single source for API timetable publish (per save_master_matrix)."""

import sys
from pathlib import Path

import pandas as pd

QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.matrix import file_manager as fm


def test_set_get_current_master_matrix_df_copy_isolation():
    df = pd.DataFrame([{"a": 1}])
    fm.set_current_master_matrix_df(df)
    got = fm.get_current_master_matrix_df()
    assert got is not None
    assert got["a"].iloc[0] == 1
    df["a"] = 99
    assert fm.get_current_master_matrix_df()["a"].iloc[0] == 1
    fm.set_current_master_matrix_df(None)
    assert fm.get_current_master_matrix_df() is None
