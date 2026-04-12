"""
Master Matrix and Timetable endpoints

NOTE: The actual matrix endpoints are implemented in main.py to avoid conflicts.
This router file is kept for potential future use but currently has no active endpoints.
"""
import logging
from pathlib import Path
from fastapi import APIRouter, HTTPException
from typing import List, Dict, Optional

router = APIRouter(prefix="/api/matrix", tags=["matrix"])
logger = logging.getLogger(__name__)

# Get project root
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent

# All matrix endpoints are implemented in main.py:
# - POST /api/matrix/build (line 1173)
# - GET /api/matrix/files (line 1466)
# - GET /api/matrix/data (line 1500)
# 
# The router endpoints below were placeholders that conflicted with the real implementations.
# They have been removed to prevent routing conflicts.
