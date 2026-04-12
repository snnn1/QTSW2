"""Thin wrapper — canonical script is batch/launch_dashboard_api.py."""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

_script = Path(__file__).resolve().parent.parent / "batch" / "launch_dashboard_api.py"
sys.exit(subprocess.call([sys.executable, str(_script)]))
