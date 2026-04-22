from __future__ import annotations

from pathlib import Path
from typing import Optional, Union


PathLike = Union[str, Path]


def resolve_qtsw2_root(
    project_root: Optional[PathLike] = None,
    *,
    default_file: Optional[PathLike] = None,
) -> Path:
    """
    Resolve the QTSW2 repository root from either an explicit project path or a file path.

    Accepted inputs include the repo root, the ``system`` directory, paths beneath
    ``system/modules/...``, or isolated temp roots used by tests.
    """
    if project_root is not None:
        candidate = Path(project_root).resolve()
    elif default_file is not None:
        candidate = Path(default_file).resolve()
    else:
        raise ValueError("resolve_qtsw2_root requires project_root or default_file")

    if candidate.is_file():
        candidate = candidate.parent

    for current in (candidate, *candidate.parents):
        if (current / "system" / "modules").is_dir():
            return current
        if current.name.lower() == "system" and (current / "modules").is_dir():
            return current.parent

    return candidate
