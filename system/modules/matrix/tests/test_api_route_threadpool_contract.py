import inspect
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
SYSTEM_ROOT = REPO_ROOT / "system"
sys.path.insert(0, str(SYSTEM_ROOT))

from modules.matrix import api


def test_matrix_api_heavy_routes_are_sync_for_fastapi_threadpool():
    """Matrix routes do synchronous CPU/disk work, so they must not block the event loop."""
    route_functions = [
        api.build_master_matrix,
        api.resequence_master_matrix,
        api.reload_latest_matrix,
        api.get_matrix_freshness,
        api.get_matrix_data,
        api.calculate_profit_breakdown,
        api.get_stream_stats,
        api.get_matrix_performance,
        api.diff_matrices,
        api.get_stream_health,
        api.list_matrix_files,
    ]

    for fn in route_functions:
        assert not inspect.iscoroutinefunction(fn), fn.__name__
