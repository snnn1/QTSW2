from pathlib import Path
import sys


SYSTEM_ROOT = Path(__file__).resolve().parent.parent
QTSW2_ROOT = SYSTEM_ROOT.parent
sys.path.insert(0, str(SYSTEM_ROOT))


def test_dashboard_bundle_paths_exist():
    from modules.dashboard.backend.main import get_dashboard_bundle_paths

    index_file, assets_dir = get_dashboard_bundle_paths()

    assert index_file == QTSW2_ROOT / "system" / "modules" / "dashboard" / "frontend" / "dist-dashboard" / "index-dashboard.html"
    assert index_file.is_file()
    assert assets_dir.is_dir()


def test_admin_launcher_opens_static_pipeline_ui():
    launcher_text = (QTSW2_ROOT / "launch" / "START_DASHBOARD_ADMIN.bat").read_text(encoding="utf-8")

    assert "http://127.0.0.1:8000/pipeline" in launcher_text
    assert "npm run dev" not in launcher_text
