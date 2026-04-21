from pathlib import Path
import importlib.util


SYSTEM_ROOT = Path(__file__).resolve().parent.parent
QTSW2_ROOT = SYSTEM_ROOT.parent


def test_standalone_runner_uses_repo_root():
    runner_path = QTSW2_ROOT / "tools" / "automation" / "run_pipeline_standalone.py"
    spec = importlib.util.spec_from_file_location("test_run_pipeline_standalone", runner_path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)

    assert module.qtsw2_root == QTSW2_ROOT
    assert module.system_root == QTSW2_ROOT / "system"
    assert module.tools_root == QTSW2_ROOT / "tools"


def test_standalone_runner_exposes_required_import_roots():
    runner_path = QTSW2_ROOT / "tools" / "automation" / "run_pipeline_standalone.py"
    spec = importlib.util.spec_from_file_location("test_run_pipeline_standalone_imports", runner_path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)

    assert str(QTSW2_ROOT / "system") in module.sys.path
    assert str(QTSW2_ROOT / "tools") in module.sys.path


def test_scheduler_setup_scripts_use_tools_module_path():
    ps1_text = (QTSW2_ROOT / "tools" / "automation" / "setup_task_scheduler.ps1").read_text(encoding="utf-8")
    bat_text = (QTSW2_ROOT / "tools" / "automation" / "setup_task_scheduler.bat").read_text(encoding="utf-8")

    expected = "-m tools.automation.run_pipeline_standalone"

    assert expected in ps1_text
    assert expected in bat_text
    assert "-m automation.run_pipeline_standalone" not in ps1_text
    assert "-m automation.run_pipeline_standalone" not in bat_text
