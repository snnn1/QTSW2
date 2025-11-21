# Master Matrix & Timetable Engine - React App

A React web application for building Master Matrix and generating Timetables.

## Setup

1. **Install dependencies:**
   ```bash
   cd matrix_timetable_app/frontend
   npm install
   ```

2. **Start the dashboard backend** (if not already running):
   ```bash
   cd dashboard/backend
   python main.py
   ```
   The backend runs on `http://localhost:8000`

3. **Start the frontend:**
   ```bash
   cd matrix_timetable_app/frontend
   npm run dev
   ```
   The frontend runs on `http://localhost:5174`

## Quick Start (Windows)

Use the batch script:
```batch
batch\RUN_MATRIX_TIMETABLE_APP.bat
```

Or manually:
```batch
cd matrix_timetable_app\frontend
START_FRONTEND.bat
```

## Features

### Master Matrix Tab
- Build master matrix from all streams
- Filter by date range or specific date
- View results: total trades, allowed trades, streams, instruments
- Browse recent master matrix files

### Timetable Tab
- Generate timetable for a trading day
- Configure SCF threshold
- View timetable with allowed/blocked trades
- See reasons for blocking (dom_blocked, scf_blocked, etc.)
- Browse recent timetable files

## API Endpoints

The app uses the dashboard backend API:
- `POST /api/matrix/build` - Build master matrix
- `POST /api/timetable/generate` - Generate timetable
- `GET /api/matrix/files` - List master matrix files
- `GET /api/timetable/files` - List timetable files

## Notes

- Make sure the dashboard backend is running on port 8000
- The frontend runs on port 5174 (different from dashboard frontend)
- Both can run simultaneously

