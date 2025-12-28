"""
Translator Service - Translates raw CSV files to canonical Parquet format

Single Responsibility: Translate raw exporter CSV files into quant-grade Parquet files
"""

import logging
from pathlib import Path
from typing import Optional, Set
from dataclasses import dataclass
from datetime import date as Date
from collections import defaultdict

from automation.services.process_supervisor import ProcessSupervisor, ProcessResult
from automation.services.event_logger import EventLogger
from automation.config import PipelineConfig

# FileManager may not be available - make it optional
try:
    from automation.services.file_manager import FileManager
except ImportError:
    FileManager = None  # type: ignore

# Import the new translator module
from modules.translator.core import translate_day


@dataclass
class TranslatorResult:
    """Result of translator stage execution"""
    stage_name: str = "translator"
    status: str = "pending"  # success, failure, skipped
    duration_seconds: float = 0.0
    error_message: Optional[str] = None
    files_written: int = 0
    files_skipped: int = 0
    files_failed: int = 0


class TranslatorService:
    """
    Service for translating raw CSV files into canonical Parquet format.
    
    Responsibilities:
    - Find raw CSV files
    - Group by instrument and date
    - Call translate_day for each instrument-date combination
    - Report metrics (files written, skipped, failed)
    """
    
    def __init__(
        self,
        config: PipelineConfig,
        logger: logging.Logger,
        process_supervisor: ProcessSupervisor,
        file_manager,  # Optional[FileManager] - may be None
        event_logger: EventLogger
    ):
        self.config = config
        self.logger = logger
        self.process_supervisor = process_supervisor
        self.file_manager = file_manager
        self.event_logger = event_logger
    
    def run(self, run_id: str) -> TranslatorResult:
        """
        Run the translator stage.
        
        Args:
            run_id: Pipeline run ID
        
        Returns:
            TranslatorResult with stage outcome
        """
        import time
        start_time = time.time()
        result = TranslatorResult()
        
        self.logger.info("Starting translator stage")
        self.event_logger.emit(run_id, "translator", "start", "Starting translator stage")
        
        try:
            # Find all raw CSV files
            raw_files = list(self.config.data_raw.rglob("*.csv"))
            
            if not raw_files:
                self.logger.warning("No raw CSV files found")
                self.event_logger.emit(run_id, "translator", "log", "No raw CSV files found")
                result.status = "skipped"
                result.duration_seconds = time.time() - start_time
                return result
            
            self.logger.info(f"Found {len(raw_files)} raw file(s) to process")
            # Don't emit metric event for file count - too verbose, info is in logs
            
            # Group files by instrument and date
            # Filename format: {instrument}_1m_{YYYY-MM-DD}.csv
            instrument_date_groups = defaultdict(set)
            
            for raw_file in raw_files:
                try:
                    # Extract instrument and date from filename
                    stem = raw_file.stem
                    parts = stem.split("_")
                    if len(parts) >= 3:
                        instrument = parts[0].upper()
                        date_str = parts[-1]  # Last part should be YYYY-MM-DD
                        try:
                            trade_date = Date.fromisoformat(date_str)
                            instrument_date_groups[(instrument, trade_date)].add(raw_file)
                        except ValueError:
                            self.logger.warning(f"Could not parse date from filename: {raw_file.name}")
                            continue
                except Exception as e:
                    self.logger.warning(f"Error processing filename {raw_file.name}: {e}")
                    continue
            
            self.logger.info(f"Processing {len(instrument_date_groups)} unique instrument-date combinations")
            
            # Process each instrument-date combination
            files_written = 0
            files_skipped = 0
            files_failed = 0
            total = len(instrument_date_groups)
            current = 0
            # Don't emit progress metric events - too verbose for live feed
            # Progress is logged to file, final summary is emitted at the end
            
            for (instrument, trade_date), files in sorted(instrument_date_groups.items()):
                current += 1
                self.logger.info(f"Translating {instrument} {trade_date} ({current}/{total})")
                
                # No progress events - only final summary at the end
                
                try:
                    # Translate using the new translator module (always overwrite existing files)
                    success = translate_day(
                        instrument=instrument,
                        day=trade_date,
                        raw_root=self.config.data_raw,
                        output_root=self.config.data_translated,
                        overwrite=True
                    )
                    
                    if success:
                        files_written += 1
                        self.logger.info(f"  [OK] Translated {instrument} {trade_date}")
                        # Don't emit event for every successful file - too verbose for live events
                    else:
                        files_skipped += 1
                        self.logger.info(f"  - Skipped {instrument} {trade_date} (no raw file)")
                        
                except Exception as e:
                    files_failed += 1
                    error_msg = f"Failed to translate {instrument} {trade_date}: {e}"
                    self.logger.error(f"  [ERROR] {error_msg}")
                    # Only emit error events - these are important and should be visible
                    self.event_logger.emit(run_id, "translator", "error", error_msg)
            
            # Determine overall status
            if files_failed > 0 and files_written == 0:
                result.status = "failure"
                result.error_message = f"All translations failed ({files_failed} failed)"
            elif files_failed > 0:
                result.status = "success"  # Partial success
                result.error_message = f"{files_failed} file(s) failed, but {files_written} succeeded"
            elif files_written > 0:
                result.status = "success"
            else:
                result.status = "skipped"
            
            result.files_written = files_written
            result.files_skipped = files_skipped
            result.files_failed = files_failed
            result.duration_seconds = time.time() - start_time
            
            # Emit summary
            summary = f"Translator completed: {files_written} written, {files_skipped} skipped, {files_failed} failed"
            self.logger.info(f"[SUCCESS] {summary}")
            
            # Emit event matching the status (success, skipped, or failure)
            event_type = result.status  # "success", "skipped", or "failure"
            self.event_logger.emit(run_id, "translator", event_type, summary, {
                "files_written": files_written,
                "files_skipped": files_skipped,
                "files_failed": files_failed
            })
            
        except Exception as e:
            result.status = "failure"
            result.error_message = f"Translator stage exception: {str(e)}"
            result.duration_seconds = time.time() - start_time
            self.logger.error(f"[ERROR] {result.error_message}", exc_info=True)
            self.event_logger.emit(run_id, "translator", "failure", result.error_message)
        
        return result
