# Custom Hooks for Matrix App

## What are Custom Hooks?

Custom hooks are a way to extract component logic into reusable functions. They follow the naming convention `use*` and can use other React hooks inside them.

## Benefits

1. **Separation of Concerns**: Business logic is separated from UI components
2. **Reusability**: Logic can be shared across multiple components
3. **Testability**: Hooks can be tested independently
4. **Cleaner Components**: Components focus on rendering, not logic
5. **Easier Maintenance**: Changes to logic are centralized

## Available Hooks

### `useMatrixFilters`
Manages filter state and operations:
- Loads/saves filters from localStorage
- Updates filters for specific streams
- Provides filter state and update functions

**Usage:**
```javascript
const { streamFilters, updateStreamFilter, getFiltersForStream } = useMatrixFilters()
```

### `useMatrixData`
Manages data loading and state:
- Loads data from API
- Manages loading and error states
- Handles retry logic
- Extracts available years

**Usage:**
```javascript
const { masterData, masterLoading, masterError, loadMasterMatrix, retryLoad } = useMatrixData()
```

### `useColumnSelection`
Manages column visibility:
- Loads/saves selected columns from localStorage
- Toggles column visibility
- Manages column selector UI state

**Usage:**
```javascript
const { selectedColumns, showColumnSelector, setShowColumnSelector, toggleColumn } = useColumnSelection()
```

## Example: Before vs After

### Before (in App.jsx):
```javascript
// 200+ lines of state management, useEffect hooks, and helper functions
const [streamFilters, setStreamFilters] = useState(() => loadAllFilters())
const [masterData, setMasterData] = useState([])
const [masterLoading, setMasterLoading] = useState(false)
// ... many more state declarations and useEffect hooks
```

### After (using hooks):
```javascript
// Clean, declarative, and easy to understand
const { streamFilters, updateStreamFilter } = useMatrixFilters()
const { masterData, masterLoading, loadMasterMatrix } = useMatrixData()
const { selectedColumns, toggleColumn } = useColumnSelection()
```

## Implementation Status

These hooks are **optional** and **not yet integrated**. The app works perfectly fine without them. They would be a nice-to-have improvement for:

- Further code organization
- Easier testing
- Better code reusability
- Cleaner App.jsx file

You can integrate them later if you want to further refactor the codebase.




