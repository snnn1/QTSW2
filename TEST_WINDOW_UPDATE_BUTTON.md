# Testing the Window Update Button

## Prerequisites

1. **Backend must be running**
   ```bash
   cd modules/dashboard/backend
   python main.py
   ```
   The backend should start on `http://localhost:8000`

2. **Frontend must be running**
   ```bash
   cd modules/matrix_timetable_app/frontend
   npm run dev
   ```
   The frontend should start on `http://localhost:5173` (or similar)

3. **A checkpoint must exist** (created after a full rebuild)
   - Click "Rebuild Matrix (Full)" button first
   - This will create a checkpoint automatically
   - The window update requires a checkpoint to restore state from

## Testing Steps

### Step 1: Verify Backend is Running
1. Open browser console (F12)
2. Navigate to the Master Matrix tab
3. Check for any connection errors

### Step 2: Create Initial Checkpoint
1. Click **"Rebuild Matrix (Full)"** button
2. Wait for rebuild to complete
3. Check backend logs for: `"Checkpoint {checkpoint_id} created successfully"`
4. Verify checkpoint exists:
   ```bash
   python test_window_update.py
   ```

### Step 3: Test the Window Update Button
1. Click **"Update Matrix (Rolling 35-Day Window)"** button
2. The button should:
   - Show "Updating..." text while processing
   - Be disabled during update
   - Re-enable after completion

### Step 4: Check Results
1. **Browser Console** (F12):
   - Look for any JavaScript errors
   - Check Network tab for API call to `/api/matrix/update`
   - Verify response status is 200

2. **Backend Logs**:
   - Should show: `"MASTER MATRIX: WINDOW UPDATE (Rolling Window)"`
   - Should show: `"Reprocess start date: YYYY-MM-DD"`
   - Should show: `"WINDOW UPDATE COMPLETE"`

3. **Check Run History**:
   ```bash
   # Check run history file
   cat data/matrix/state/run_history.jsonl | tail -1 | python -m json.tool
   ```

4. **Verify Matrix Updated**:
   - Matrix data should reload automatically
   - Check that date range includes updated dates
   - Verify no duplicates in the data

## Expected Behavior

### Success Case:
- Button click triggers API call
- Backend processes window update
- Matrix reloads with updated data
- No errors in console or logs

### Error Cases:
1. **No Checkpoint Found**:
   - Error: "No checkpoint found. Please run a full rebuild first."
   - Solution: Click "Rebuild Matrix (Full)" first

2. **Backend Not Running**:
   - Error: "Backend not running" or connection timeout
   - Solution: Start backend server

3. **Insufficient History**:
   - Error: "Insufficient history: need 35 trading days back"
   - Solution: Ensure you have enough historical data

## Quick Test Script

Run the test script to verify endpoint:
```bash
python test_window_update.py
```

This will:
- Check if checkpoint exists
- Test the API endpoint
- Show detailed error messages if something fails

## Troubleshooting

### Button doesn't appear:
- Check browser console for React errors
- Verify `updateMasterMatrix` function is defined
- Check that button JSX is rendered

### Button click does nothing:
- Check browser console for JavaScript errors
- Verify API call is being made (Network tab)
- Check backend logs for errors

### API returns error:
- Check backend logs for detailed error
- Verify checkpoint exists
- Ensure merged data is available
- Check that matrix output directory exists

## Manual API Test

You can also test the API directly:
```bash
curl -X POST http://localhost:8000/api/matrix/update \
  -H "Content-Type: application/json" \
  -d '{"mode": "window"}'
```

