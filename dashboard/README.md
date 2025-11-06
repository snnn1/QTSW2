# Pipeline Dashboard

A real-time dashboard for monitoring and controlling the data pipeline.

## Architecture

- **Backend**: FastAPI server (port 8000)
- **Frontend**: React + Vite + Tailwind (port 5173)
- **Communication**: WebSocket for real-time events

## Setup

### Backend

1. Install Python dependencies:
```bash
pip install -r requirements.txt
```

2. Run the backend:
```bash
cd dashboard/backend
python main.py
```

Or use uvicorn directly:
```bash
uvicorn dashboard.backend.main:app --reload --port 8000
```

### Frontend

1. Install Node.js dependencies:
```bash
cd dashboard/frontend
npm install
```

2. Run the development server:
```bash
npm run dev
```

3. Open http://localhost:5173 in your browser

## Usage

1. **Start Pipeline**: Click "Run Now" to start a pipeline run immediately
2. **Update Schedule**: Change the scheduled time and click "Update"
3. **Monitor Progress**: Watch real-time events, stage updates, and file counts
4. **Alerts**: Failure alerts appear automatically when errors occur

## Features

- Real-time event streaming via WebSocket
- Live metrics (file counts, processing rate, current stage)
- Schedule management
- Failure alerts
- Pipeline continues running even if GUI is closed

