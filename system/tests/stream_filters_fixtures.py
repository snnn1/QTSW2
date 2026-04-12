"""Test helpers: minimal configs/stream_filters.json for execution publish (passes require_non_empty)."""

import json
from pathlib import Path


def install_min_stream_filters(project_root: Path) -> None:
    cfg = Path(project_root) / "configs"
    cfg.mkdir(parents=True, exist_ok=True)
    (cfg / "stream_filters.json").write_text(
        json.dumps(
            {
                "master": {
                    "exclude_days_of_week": [],
                    "exclude_days_of_month": [],
                    "exclude_times": [],
                }
            }
        ),
        encoding="utf-8",
    )
