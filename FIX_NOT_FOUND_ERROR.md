# Fix "Not Found" Error for Window Update Button

## The Problem
You're seeing a "Not Found" error when clicking the "Update Matrix (Rolling 35-Day Window)" button.

## Solution: Restart the Backend

The new `/api/matrix/update` endpoint was added to the code, but **the backend needs to be restarted** to register it.

### Steps to Fix:

1. **Stop the current backend** (if running):
   - Press `Ctrl+C` in the terminal where the backend is running
   - Or close the terminal window

2. **Restart the backend**:
   ```bash
   cd modules/dashboard/backend
   python main.py
   ```

3. **Verify the endpoint is registered**:
   - Open browser and go to: `http://localhost:8000/docs`
   - Look for `/api/matrix/update` in the list of endpoints
   - Or run: `python check_endpoint.py`

4. **Test the button again**:
   - Refresh your frontend page
   - Click "Update Matrix (Rolling 35-Day Window)" button
   - It should now work!

## Why This Happens

FastAPI registers routes when the application starts. Since we added a new endpoint (`/api/matrix/update`), the backend needs to be restarted to:
- Import the updated `modules/matrix/api.py` file
- Register the new route with FastAPI
- Make it available at `/api/matrix/update`

## Quick Verification

After restarting, you can verify the endpoint exists by:

1. **Browser method**:
   - Go to `http://localhost:8000/docs`
   - Look for `POST /api/matrix/update` in the endpoints list

2. **Script method**:
   ```bash
   python check_endpoint.py
   ```

3. **Direct API test** (PowerShell):
   ```powershell
   Invoke-WebRequest -Uri "http://localhost:8000/api/matrix/update" -Method POST -ContentType "application/json" -Body '{"mode":"window"}' | Select-Object StatusCode, Content
   ```

## If Still Not Working

If you still get "Not Found" after restarting:

1. **Check backend logs** for import errors:
   - Look for errors when starting the backend
   - Check if `modules.matrix.api` imports correctly

2. **Verify the file exists**:
   ```bash
   ls modules/matrix/api.py
   ```

3. **Check the router is included** in `modules/dashboard/backend/main.py`:
   - Should have: `app.include_router(matrix_router)`
   - Should import: `from modules.matrix.api import router as matrix_router`

4. **Check for syntax errors**:
   ```bash
   python -m py_compile modules/matrix/api.py
   ```

## Expected Behavior After Fix

Once the backend is restarted:
- The endpoint `/api/matrix/update` will be available
- The button will successfully call the API
- You'll see "UPDATE ENDPOINT HIT!" in backend logs
- The window update will process

