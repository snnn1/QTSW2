# Quick Start - Dashboard Troubleshooting

## Services Not Running?

If localhost:5173 won't open, the services aren't running. Here's how to start them:

## Option 1: Use Batch File (Easiest)

Double-click: `batch/START_DASHBOARD.bat`

This starts both services automatically.

## Option 2: Manual Start (Step by Step)

### Step 1: Start Backend

Open a **PowerShell or Command Prompt** window:

```powershell
cd C:\Users\jakej\QTSW2\dashboard\backend
python main.py
```

You should see:
```
INFO:     Uvicorn running on http://0.0.0.0:8000
```

**Keep this window open!**

### Step 2: Start Frontend

Open a **NEW PowerShell or Command Prompt** window:

```powershell
cd C:\Users\jakej\QTSW2\dashboard\frontend
npm run dev
```

You should see:
```
  VITE v5.x.x  ready in xxx ms

  ➜  Local:   http://localhost:5173/
  ➜  Network: use --host to expose
```

### Step 3: Open Browser

Open your browser and go to: **http://localhost:5173**

## Troubleshooting

### "Port 8000 already in use"
- Another process is using port 8000
- Find and close it, or change the port in `dashboard/backend/main.py`

### "Port 5173 already in use"
- Another process is using port 5173
- Find and close it, or Vite will automatically use the next available port

### "npm is not recognized"
- Node.js not installed or not in PATH
- Install Node.js from https://nodejs.org/
- Restart your terminal after installation

### "python is not recognized"
- Python not in PATH
- Use full path: `C:\Users\jakej\AppData\Local\Programs\Python\Python313\python.exe`

### Backend starts but frontend won't connect
- Check backend is actually running: http://localhost:8000
- Should see: `{"message":"Pipeline Dashboard API"}`
- Check browser console for WebSocket errors

### Frontend shows "Backend not connected"
- Backend isn't running
- Start backend first (Step 1)
- Check http://localhost:8000 works

## Verify Services Are Running

**Check Backend:**
```powershell
curl http://localhost:8000
```
Should return: `{"message":"Pipeline Dashboard API"}`

**Check Frontend:**
- Open http://localhost:5173 in browser
- Should see the dashboard interface

## Common Issues

1. **Services not started** - Most common issue
2. **Wrong directory** - Make sure you're in the right folder
3. **Port conflicts** - Another app using the ports
4. **Dependencies not installed** - Run `pip install` and `npm install`
5. **Firewall blocking** - Allow Python and Node.js through firewall

## Still Not Working?

1. Check both terminal windows are open and running
2. Check for error messages in the terminals
3. Try accessing http://localhost:8000 directly (backend API)
4. Check browser console (F12) for errors
5. Verify Node.js and Python are installed correctly







