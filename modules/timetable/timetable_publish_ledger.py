"""
Append-only ledger for every successful execution timetable publish (timetable_current.json).

Path: <project_root>/logs/timetable_publish.jsonl
"""

from __future__ import annotations

import json
import logging
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict

logger = logging.getLogger(__name__)


def append_timetable_publish_ledger(project_root: Path, entry: Dict[str, Any]) -> None:
    """
    Append one JSON line. Fails softly (log error) so publish success is not blocked by ledger I/O.
    """
    try:
        logs_dir = project_root / "logs"
        logs_dir.mkdir(parents=True, exist_ok=True)
        path = logs_dir / "timetable_publish.jsonl"
        line = json.dumps(entry, ensure_ascii=False, default=str)
        with open(path, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception as e:
        logger.error("TIMETABLE_PUBLISH_LEDGER_APPEND_FAILED: %s", e)
