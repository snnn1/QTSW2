# How to Open the Dashboard

## Step 1: Make sure the backend is running

The backend must be running on **port 8001** for the dashboard to work.

**Check if backend is running:**
- Open your browser and go to: `http://localhost:8001/api/pipeline/status`
- If you see JSON data, the backend is running ✅
- If you see "Unable to connect" or similar, the backend is NOT running ❌

**If backend is NOT running, start it:**
```powershell
cd C:\Users\jakej\QTSW2\modules\dashboard\backend
python -m uvicorn main:app --host 0.0.0.0 --port 8001
```

Or if you have a batch file to start it, use that instead.

---

## Step 2: Start the frontend (if not already running)

**Option A: Use the batch file**
1. Double-click: `C:\Users\jakej\QTSW2\modules\dashboard\frontend\START_FRONTEND.bat`
2. Wait for it to say "Local: http://localhost:5173"

**Option B: Use command line**
```powershell
cd C:\Users\jakej\QTSW2\modules\dashboard\frontend
npm run dev
```

Wait until you see:
```
  VITE v5.x.x  ready in xxx ms

  ➜  Local:   http://localhost:5173/
  ➜  Network: use --host to expose
```

---

## Step 3: Open the dashboard in your browser

**Open this URL:**
```
http://localhost:5173
```

**That's it!** The dashboard should load and show:
- Pipeline status
- Live events (if backend is connected)
- Scheduler status
- Run controls

---

## Troubleshooting

**If you see "Unable to connect" or no events:**
1. Make sure backend is running on port 8001
2. Check browser console (F12) for WebSocket connection errors
3. Verify backend logs show: `[JSONL Monitor] Monitor loop STARTING`

**If frontend won't start:**
1. Make sure you've installed dependencies: `npm install` in the frontend directory
2. Check if port 5173 is already in use
3. Look for error messages in the terminal

**If events aren't appearing:**
1. Backend is running ✅ (we verified this)
2. JSONL monitor is working ✅ (we verified this)
3. Events are available ✅ (we verified this)
4. Check browser console (F12) → Network tab → WS (WebSocket) → Should show connection to `ws://localhost:5173/ws/events`

---

## Quick Reference

- **Backend URL**: `http://localhost:8001`
- **Frontend URL**: `http://localhost:5173`
- **Backend Status Check**: `http://localhost:8001/api/pipeline/status`
- **Frontend Location**: `C:\Users\jakej\QTSW2\modules\dashboard\frontend`
- **Backend Location**: `C:\Users\jakej\QTSW2\modules\dashboard\backend`

