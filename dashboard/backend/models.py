"""
Pydantic models for API requests and responses
"""
from pydantic import BaseModel
from typing import Optional, List, Dict, Any


class ScheduleConfig(BaseModel):
    schedule_time: str  # HH:MM format


class PipelineStartRequest(BaseModel):
    wait_for_export: bool = False
    launch_ninjatrader: bool = False


class PipelineStartResponse(BaseModel):
    run_id: str
    event_log_path: str
    status: str


class MatrixBuildRequest(BaseModel):
    rebuild: bool = False
    rebuild_stream: Optional[str] = None
    stream_filters: Optional[Dict[str, Dict[str, List]]] = None
    visible_years: Optional[List[int]] = None
    warmup_months: int = 1
    streams: Optional[List[str]] = None


class TimetableGenerateRequest(BaseModel):
    rebuild: bool = False
    stream_filters: Optional[Dict[str, Dict[str, List]]] = None
    visible_years: Optional[List[int]] = None


class StreamFilterConfig(BaseModel):
    exclude_days_of_week: List[str] = []  # e.g., ["Wednesday", "Friday"]
    exclude_days_of_month: List[int] = []  # e.g., [4, 16, 30]
    exclude_times: List[str] = []  # e.g., ["07:30", "08:00"]


class MatrixBuildRequest(BaseModel):
    start_date: Optional[str] = None
    end_date: Optional[str] = None
    specific_date: Optional[str] = None
    analyzer_runs_dir: str = "data/analyzer_runs"
    output_dir: str = "data/master_matrix"
    stream_filters: Optional[Dict[str, StreamFilterConfig]] = None
    streams: Optional[List[str]] = None  # If provided, only rebuild these streams
    visible_years: Optional[List[int]] = None  # Restrict matrix to these years (with warmup)
    warmup_months: int = 1  # Number of months of warmup data before first visible year


class TimetableRequest(BaseModel):
    date: Optional[str] = None
    scf_threshold: float = 0.5
    analyzer_runs_dir: str = "data/analyzer_runs"
    output_dir: str = "data/timetable"

