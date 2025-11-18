@echo off
REM Run Data Merger / Consolidator
REM Merges daily analyzer and sequencer files into monthly Parquet files

cd /d "%~dp0.."
python tools\data_merger.py

pause

