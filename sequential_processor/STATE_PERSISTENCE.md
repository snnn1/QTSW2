# State Persistence for Sequential Processor

## Problem
The sequential processor needs **13 previous data points** for:
- **Rolling median/ladder windows**: Last 13 peaks
- **Time slot histories**: Last 13 trade scores per time slot

Without state persistence, each run starts fresh and needs 13 days of data before making proper decisions.

## Solution: State Manager

### How It Works

1. **State File Structure**
   - Location: `data/sequencer_runs/state/`
   - Format: `state_{instrument}_{stream}.json`
   - Example: `state_ES_ES1.json`

2. **What Gets Saved**
   - Last 13 peaks for rolling median window
   - Last 13 peaks for median ladder window  
   - Last 13 trade scores for each time slot
   - Current target and time slot
   - Current rolling median/ladder targets
   - Median ladder step and days at level
   - Last processed date

3. **Workflow**

   **First Run:**
   ```
   Start → No state found → Process 13+ days → Save state → Continue
   ```

   **Subsequent Runs:**
   ```
   Start → Load state → Continue from last date → Update state → Save
   ```

### Integration Points

#### 1. Load State on Initialization (Optional)
```python
from sequential_processor.state_manager import SequencerStateManager

# In SequentialProcessorV2.__init__ or process_sequential
state_manager = SequencerStateManager()
stream = self.data['Stream'].iloc[0] if 'Stream' in self.data.columns else 'ES1'
instrument = self.instrument

# Try to load previous state
state_manager.load_state_to_processor(self, stream, instrument)
```

#### 2. Save State After Processing
```python
# At end of process_sequential, before returning results
if hasattr(self, 'last_processed_date') and self.last_processed_date:
    state_manager.save_state_from_processor(
        self, 
        stream, 
        instrument,
        self.last_processed_date
    )
```

### Usage Options

#### Option A: Automatic State Management (Recommended)
- State loads automatically if exists
- State saves automatically after each run
- Seamless continuation between runs

#### Option B: Manual State Management
- User controls when to load/save state
- Can reset state by deleting state files
- More control over state lifecycle

#### Option C: State Per Configuration
- Different state files for different configurations
- Allows multiple parallel sequencer runs
- Useful for testing different parameters

### State File Example

```json
{
  "stream": "ES1",
  "instrument": "ES",
  "saved_at": "2025-01-16T10:30:00",
  "last_processed_date": "2025-01-15",
  "rolling_median_window": [15.0, 20.0, 18.0, ...],  // Last 13 peaks
  "median_ladder_window": [15.0, 20.0, 18.0, ...],  // Last 13 peaks
  "time_slot_histories": {
    "08:00": [1, -2, 1, 0, ...],  // Last 13 scores
    "09:00": [1, 1, -2, ...]
  },
  "current_target": 15,
  "current_time": "08:00",
  "rolling_median_target": 15,
  "median_ladder_target": 15,
  "median_ladder_current_step": 0,
  "median_ladder_days_at_level": 5
}
```

### Benefits

1. **No Warm-up Period**: Continues seamlessly from previous run
2. **Accurate Decisions**: Has full 13-day history from day 1
3. **Incremental Processing**: Process new data as it arrives
4. **State Recovery**: Can resume after interruption
5. **Multiple Streams**: Separate state per stream/instrument

### Implementation Steps

1. ✅ Created `state_manager.py` with save/load functionality
2. ⏳ Integrate into `sequential_processor.py`
3. ⏳ Add state management to Streamlit app
4. ⏳ Add state reset/clear functionality
5. ⏳ Add state visualization/debugging

### Next Steps

1. **Add state loading to processor initialization**
2. **Add state saving after processing**
3. **Add UI controls in Streamlit app**:
   - Checkbox: "Load previous state"
   - Button: "Save state"
   - Button: "Clear state"
   - Display: Current state info

