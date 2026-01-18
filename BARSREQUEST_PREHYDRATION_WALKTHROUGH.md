# NinjaTrader BarsRequest Pre-Hydration Walkthrough

## Example Scenario
- **Date**: 2026-01-16 (Friday)
- **Strategy Enabled**: 07:25:00 AM Chicago Time
- **Instrument**: ES (E-mini S&P 500)
- **Trading Date** (from timetable): 2026-01-16
- **Range Start**: 02:00:00 CT (session start)
- **Slot Time**: 07:30:00 CT (first slot)

---

## Step-by-Step Execution Flow

### **Step 1: Strategy Initialization (07:25:00 CT / 13:25:00 UTC)**

**Location**: `RobotSimStrategy.OnStateChange(State.DataLoaded)`

```
1. Verify SIM account
   ✓ Account = "Sim101"
   ✓ Account.IsSimAccount = true
   → Log: "SIM account verified: Sim101"

2. Create RobotEngine instance
   → _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), ExecutionMode.SIM, ...)
   → Instrument = "ES"

3. Set account info
   → _engine.SetAccountInfo("Sim101", "SIM")

4. Start engine
   → _engine.Start()
```

---

### **Step 2: Engine Startup (07:25:00 CT)**

**Location**: `RobotEngine.Start()`

```
1. Load ParitySpec
   → Load spec from file
   → Log: "SPEC_LOADED" { spec_name: "ultra_simple", timezone: "America/Chicago" }

2. Create TimeService
   → _time = new TimeService("America/Chicago")

3. Create execution adapter
   → _executionAdapter = NinjaTraderSimAdapter

4. Load timetable and lock trading date
   → ReloadTimetableIfChanged(utcNow, force: true)
   → Read timetable.json
   → Parse: trading_date = "2026-01-16"
   → _activeTradingDate = DateOnly(2026, 1, 16)
   → Log: "TRADING_DATE_LOCKED" { trading_date: "2026-01-16", source: "TIMETABLE" }

5. Create streams
   → EnsureStreamsCreated(utcNow)
   → Read timetable: instrument = "ES", session = "S1", slots = ["07:30", "08:00", "09:00"]
   → RangeStartTime from spec: "02:00" (session start)
   → Create StreamStateMachine for each slot:
     * Stream "ES_S1_0730" (RangeStart: 02:00, SlotTime: 07:30)
     * Stream "ES_S1_0800" (RangeStart: 02:00, SlotTime: 08:00)
     * Stream "ES_S1_0900" (RangeStart: 02:00, SlotTime: 09:00)
   → Each stream starts in PRE_HYDRATION state
   → Log: "STREAMS_CREATED" { count: 3, trading_date: "2026-01-16" }

6. Emit startup banner
   → Log: "OPERATOR_BANNER" { trading_date: "2026-01-16", streams: 3, ... }
```

**Current State**:
- ✅ Trading date locked: **2026-01-16**
- ✅ Streams created: **3 streams** (all in `PRE_HYDRATION` state)
- ✅ Engine ready

---

### **Step 3: Request Historical Bars (07:25:01 CT)**

**Location**: `RobotSimStrategy.RequestHistoricalBarsForPreHydration()`

```
1. Get trading date from engine
   → tradingDateStr = _engine.GetTradingDate()
   → Result: "2026-01-16" ✓

2. Parse trading date
   → DateOnly.TryParse("2026-01-16", out tradingDate)
   → Result: DateOnly(2026, 1, 16) ✓

3. Set time range (hardcoded for now)
   → rangeStartChicago = "02:00"
   → slotTimeChicago = "07:30"
   → Log: "Requesting historical bars from NinjaTrader for 2026-01-16 (02:00 to 07:30)"

4. Create TimeService
   → timeService = new TimeService("America/Chicago")

5. Call BarsRequest helper
   → bars = NinjaTraderBarRequest.RequestBarsForTradingDate(
         Instrument,           // ES instrument
         tradingDate,          // 2026-01-16
         "07:30",             // Range start
         "09:00",             // Slot time
         timeService
     )
```

---

### **Step 4: BarsRequest API Call (07:25:01 CT)**

**Location**: `NinjaTraderBarRequest.RequestBarsForTradingDate()`

```
1. Construct Chicago times
   → rangeStartChicagoTime = ConstructChicagoTime(2026-01-16, "02:00")
     = DateTimeOffset(2026-01-16 02:00:00 -06:00)  // Chicago time with offset
   
   → slotTimeChicagoTime = ConstructChicagoTime(2026-01-16, "07:30")
     = DateTimeOffset(2026-01-16 07:30:00 -06:00)

2. Convert to UTC
   → rangeStartUtc = 2026-01-16 02:00:00 CT → 2026-01-16 08:00:00 UTC
   → slotTimeUtc = 2026-01-16 07:30:00 CT → 2026-01-16 13:30:00 UTC

3. Call RequestHistoricalBars()
   → barsRequest = new BarsRequest(Instrument.ES)
   → barsRequest.BarsPeriod = { BarsPeriodType.Minute, Value: 1 }
   → barsRequest.StartTime = 2026-01-16 08:00:00 UTC
   → barsRequest.EndTime = 2026-01-16 13:30:00 UTC
   → barsRequest.TradingHours = ES.MasterInstrument.TradingHours

4. Request bars synchronously
   → barsSeries = barsRequest.Request()
   → NinjaTrader queries its historical data store
   → Returns: BarsSeries with 330 bars (02:00-07:30 = 330 minutes = 5.5 hours)

5. Convert NinjaTrader bars to Robot.Core.Bar format
   → Loop through barsSeries (330 bars):
     * Bar 1: Time = 2026-01-16 02:00:00 (Chicago, Unspecified)
            → ConvertBarTimeToUtc() → 2026-01-16 08:00:00 UTC
            → Bar(utc: 08:00:00, O: 4945.00, H: 4945.25, L: 4944.75, C: 4945.00)
     * Bar 2: Time = 2026-01-16 02:01:00 (Chicago)
            → ConvertBarTimeToUtc() → 2026-01-16 08:01:00 UTC
            → Bar(utc: 08:01:00, O: 4945.00, H: 4945.50, L: 4944.75, C: 4945.25)
     * ... (328 more bars)
     * Bar 330: Time = 2026-01-16 07:30:00 (Chicago)
            → ConvertBarTimeToUtc() → 2026-01-16 13:30:00 UTC
            → Bar(utc: 13:30:00, O: 4950.25, H: 4950.50, L: 4950.00, C: 4950.25)

6. Return list of bars
   → Returns: List<Bar> with 330 bars (chronologically ordered)
```

**Result**: ✅ **330 bars** retrieved from NinjaTrader historical data (02:00-07:30 CT = 5.5 hours)

---

### **Step 5: Feed Bars to Engine (07:25:02 CT)**

**Location**: `RobotEngine.LoadPreHydrationBars()`

```
1. Validate inputs
   → bars != null ✓
   → bars.Count = 90 > 0 ✓
   → _spec != null ✓
   → _time != null ✓

2. Check streams exist
   → _streams.Count = 3 > 0 ✓

3. Feed bars to matching streams
   → Loop through streams:
     * Stream "ES_S1_0730" → IsSameInstrument("ES") = true
       → Feed all 330 bars via stream.OnBar()
     * Stream "ES_S1_0800" → IsSameInstrument("ES") = true
       → Feed all 330 bars via stream.OnBar()
     * Stream "ES_S1_0900" → IsSameInstrument("ES") = true
       → Feed all 330 bars via stream.OnBar()

4. Log success
   → Log: "PRE_HYDRATION_BARS_LOADED" {
       instrument: "ES",
       bar_count: 330,
       streams_fed: 3,
       first_bar_utc: "2026-01-16T08:00:00Z",
       last_bar_utc: "2026-01-16T13:30:00Z",
       source: "NinjaTrader_BarsRequest"
     }
```

**Result**: ✅ **330 bars fed to 3 streams** (990 total bar calls)

---

### **Step 6: Stream Bar Buffering (07:25:02 CT)**

**Location**: `StreamStateMachine.OnBar()` for each stream

```
For each stream (ES_S1_0730, ES_S1_0800, ES_S1_0900):

1. Check trading date filter
   → barTradingDate = GetChicagoDateToday(barUtc) = "2026-01-16"
   → TradingDate = "2026-01-16"
   → Match ✓ (bars pass filter)

2. Check if in buffering state
   → State = PRE_HYDRATION ✓
   → Buffer bars: AddBarToBuffer(bar)

3. Buffer all 330 bars
   → _barBuffer now contains 330 bars (02:00-07:30)
   → Bars are stored chronologically

Result per stream:
- ES_S1_0730: _barBuffer.Count = 330
- ES_S1_0800: _barBuffer.Count = 330
- ES_S1_0900: _barBuffer.Count = 330
```

**Result**: ✅ **All bars buffered** in each stream's `_barBuffer`

---

### **Step 7: Pre-Hydration State Handling (07:25:03 CT)**

**Location**: `StreamStateMachine.Tick()` → `HandlePreHydrationState()`

```
For each stream (called every 1 second via timer):

1. Check if file-based pre-hydration complete
   → _preHydrationComplete = false (initially)
   → Call PerformPreHydration(utcNow)
   → Read CSV files from data/raw/ES/1m/2026/01/2026-01-16.csv
   → Load bars from file (if exists)
   → _preHydrationComplete = true

2. After file-based pre-hydration
   → _preHydrationComplete = true ✓
   → IsSimMode() = true ✓

3. Check bar count
   → barCount = GetBarBufferCount() = 330 (from BarsRequest)

4. Check time
   → nowChicago = ConvertUtcToChicago(utcNow) = 07:25:03 CT
   → RangeStartChicagoTime = 02:00:00 CT
   → nowChicago >= RangeStartChicagoTime ✓ (already past range start)

5. Transition decision
   → barCount > 0 ✓ (90 bars available)
   → Condition: barCount > 0 || nowChicago >= RangeStartChicagoTime
   → Result: TRUE (barCount > 0)
   → Transition to ARMED state

6. Log transition
   → Log: "PRE_HYDRATION_COMPLETE" {
       instrument: "ES",
       slot: "ES_S1_0730",
       trading_date: "2026-01-16",
       bars_received: 330,
       note: "File-based pre-hydration complete, supplemented with NinjaTrader bars"
     }

7. Transition
   → Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE_SIM")
   → State = ARMED
```

**Result**: ✅ **All streams transition to ARMED** state with 330 bars buffered

---

### **Step 8: Live Bars Continue Arriving (07:25:04+ CT)**

**YES - Live bars are tracked and combined with historical bars!**

**Location**: `RobotSimStrategy.OnBarUpdate()` → `RobotEngine.OnBar()` → `StreamStateMachine.OnBar()`

```
As live bars arrive from NinjaTrader:

1. Bar arrives at 07:25:04 CT
   → OnBarUpdate() called (NinjaTrader callback)
   → Extract: Open, High, Low, Close from NinjaTrader bar
   → Convert bar time to UTC
   → _engine.OnBar(barUtc, "ES", open, high, low, close, utcNow)

2. Engine validates bar
   → barChicagoDate = "2026-01-16"
   → _activeTradingDate = "2026-01-16"
   → Match ✓
   → Bar accepted
   → Log: "BAR_ACCEPTED" (rate-limited, once per minute)

3. Feed to streams
   → All 3 streams receive bar via stream.OnBar()
   → Streams check state: ARMED ✓
   → Buffer bar: AddBarToBuffer(bar)
   → _barBuffer now contains: 330 historical + 1 live = 331 bars

4. Continue...
   → More bars arrive every minute (07:26, 07:27, 07:28, etc.)
   → Each bar added to _barBuffer
   → All buffered for later range computation
   → By 07:30:00 CT: _barBuffer contains 330 historical + 5 live = 335 bars

Key Point: Live bars are ADDED to the same buffer that contains historical bars.
They are combined seamlessly - no distinction between historical and live bars.
```

---

### **Step 9: Range Building Starts (07:30:00 CT)**

**Location**: `StreamStateMachine.Tick()` → `HandleArmedState()`

```
When RangeStartChicagoTime arrives (07:30:00 CT):

1. Check if range start time reached
   → nowChicago = 07:30:00 CT
   → RangeStartChicagoTime = 07:30:00 CT
   → nowChicago >= RangeStartChicagoTime ✓

2. Transition to RANGE_BUILDING
   → Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILDING_START")

3. Range computation (at SlotTime = 07:30:00 CT for first slot)
   → ComputeRangeRetrospectively()
   → Use all bars from _barBuffer (330 from BarsRequest + any live bars)
   → Filter bars by trading date (all should be 2026-01-16)
   → Filter bars by time window: RangeStartChicagoTime (02:00) to SlotTimeChicagoTime (07:30)
   → Calculate range high/low from bars in window
   → Log: "RANGE_COMPUTE_COMPLETE" {
       range_high: 4950.50,
       range_low: 4944.75,
       bar_count: 330
     }
```

---

## Summary Timeline

```
07:25:00 CT - Strategy enabled
07:25:00 CT - Engine.Start() → Trading date locked, streams created
07:25:01 CT - RequestHistoricalBarsForPreHydration() called
07:25:01 CT - BarsRequest.Request() → 330 bars retrieved (02:00-07:30)
07:25:02 CT - LoadPreHydrationBars() → Bars fed to 3 streams
07:25:02 CT - Streams buffer 330 bars each
07:25:03 CT - Pre-hydration complete → Streams transition to ARMED
07:25:04+ CT - Live bars continue arriving → Added to buffer
07:30:00 CT - First slot (07:30) → Range computation → Uses all 330 buffered bars
08:00:00 CT - Second slot (08:00) → Range computation → Uses all buffered bars
09:00:00 CT - Third slot (09:00) → Range computation → Uses all buffered bars
```

---

## Key Points

1. **Trading Date Authority**: Locked from timetable, never changes
2. **Stream Creation**: Happens before BarsRequest (streams must exist)
3. **BarsRequest Timing**: Called synchronously in `DataLoaded` state
4. **Bar Buffering**: All bars (historical + live) buffered in `PRE_HYDRATION`, `ARMED`, `RANGE_BUILDING` states
5. **Pre-Hydration Sources**: 
   - Primary: NinjaTrader BarsRequest (330 bars from 02:00-07:30 CT)
   - Fallback: File-based CSV (if available)
   - Supplement: Live bars from OnBarUpdate()
6. **Range Window**: Bars from 02:00 CT (session start) to slot time (07:30, 08:00, or 09:00)
7. **State Transitions**: PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED

---

## Error Handling

If BarsRequest fails:
- Exception caught in `RequestHistoricalBarsForPreHydration()`
- Logged as warning
- Strategy continues with file-based or live bars only
- No crash, graceful degradation

If streams don't exist:
- `LoadPreHydrationBars()` checks `_streams.Count`
- Logs "PRE_HYDRATION_BARS_SKIPPED"
- Bars will be buffered when streams are created or via `OnBar()`
