#!/usr/bin/env python3
"""
Regenerate timetable from existing master matrix.

This script loads the most recent master matrix file and regenerates
the timetable_current.json file using the fixed timetable logic.
"""

import sys
from pathlib import Path

# Add project root to path
project_root = Path(__file__).parent
sys.path.insert(0, str(project_root))

from modules.matrix.file_manager import load_existing_matrix
from modules.timetable.timetable_engine import TimetableEngine
import logging

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

def main():
    """Regenerate timetable from existing master matrix."""
    logger.info("=" * 80)
    logger.info("REGENERATING TIMETABLE FROM MASTER MATRIX")
    logger.info("=" * 80)
    
    # Load existing master matrix
    master_matrix_dir = "data/master_matrix"
    logger.info(f"Loading master matrix from {master_matrix_dir}...")
    
    master_df = load_existing_matrix(master_matrix_dir)
    
    if master_df.empty:
        logger.error("No master matrix found! Please build the master matrix first.")
        return 1
    
    logger.info(f"Loaded master matrix with {len(master_df)} trades")
    
    # Get latest date from master matrix
    if 'trade_date' not in master_df.columns:
        logger.error("Master matrix missing trade_date column!")
        return 1
    
    latest_date = master_df['trade_date'].max()
    logger.info(f"Latest date in master matrix: {latest_date}")
    
    # Load stream filters if they exist
    stream_filters = None
    try:
        import json
        filters_path = Path("configs/stream_filters.json")
        if filters_path.exists():
            with open(filters_path, 'r') as f:
                stream_filters = json.load(f)
            logger.info(f"Loaded stream filters from {filters_path}")
    except Exception as e:
        logger.warning(f"Could not load stream filters: {e}. Continuing without filters.")
    
    # Regenerate timetable
    logger.info("Regenerating timetable...")
    engine = TimetableEngine()
    
    try:
        engine.write_execution_timetable_from_master_matrix(
            master_df,
            trade_date=latest_date.strftime('%Y-%m-%d') if hasattr(latest_date, 'strftime') else str(latest_date)[:10],
            stream_filters=stream_filters
        )
        logger.info("=" * 80)
        logger.info("SUCCESS: Timetable regenerated!")
        logger.info(f"Timetable file: data/timetable/timetable_current.json")
        logger.info("=" * 80)
        return 0
    except Exception as e:
        logger.error(f"Failed to regenerate timetable: {e}")
        import traceback
        logger.error(traceback.format_exc())
        return 1

if __name__ == "__main__":
    sys.exit(main())
