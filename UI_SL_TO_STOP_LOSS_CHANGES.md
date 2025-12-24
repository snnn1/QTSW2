# UI Changes: SL → Stop Loss

## Changes Made

Updated the UI to display "Stop Loss" instead of "SL" as the column header, while keeping the data column name as "SL" internally.

### Files Modified

1. **`modules/matrix_timetable_app/frontend/src/App.jsx`**
   - Line 2400-2410: Updated table header rendering to map "SL" → "Stop Loss"
   - Line 2053-2066: Updated inline column selector to display "Stop Loss"

2. **`modules/matrix_timetable_app/frontend/src/components/DataTable.jsx`**
   - Line 215-223: Updated table header rendering to map "SL" → "Stop Loss"

3. **`modules/matrix_timetable_app/frontend/src/components/ColumnSelector.jsx`**
   - Line 33-46: Updated column selector list to display "Stop Loss"

## Implementation

The column name mapping is done inline where headers are rendered:
```javascript
const displayName = col === 'SL' ? 'Stop Loss' : col
```

## What Stays the Same

- Data column name remains "SL" (backend compatibility)
- Column references in code still use "SL"
- Column selector checkboxes still work with "SL"
- All data access uses "SL" as the key

## What Changed

- **Table headers**: Now display "Stop Loss" instead of "SL"
- **Column selector UI**: Now shows "Stop Loss" in the checkbox labels

## Result

Users will see "Stop Loss" in the UI, but all data operations continue to work with the "SL" column name internally.

