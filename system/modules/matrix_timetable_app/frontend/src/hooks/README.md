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

### `useMatrixController`
Central orchestration hook for matrix data and worker coordination:
- Backend data lifecycle (load, rebuild, resequence, reload)
- Worker lifecycle (initData, compute triggers)
- Derived state: masterData, masterLoading, masterError, availableYearsFromAPI
- File change detection, matrix freshness, auto-update

**Usage:**
```javascript
const {
  masterData,
  masterLoading,
  masterError,
  loadMasterMatrix,
  reloadLatestMatrix,
  resequenceMasterMatrix,
  availableYearsFromAPI,
  streamFilters,
  ...
} = useMatrixController({ streamFilters, masterContractMultiplier, ... })
```

### `useMatrixFilters`
Manages filter state and operations:
- Loads/saves filters from localStorage
- Updates filters for specific streams
- Provides filter state and update functions

**Usage:**
```javascript
const { streamFilters, updateStreamFilter, getFiltersForStream } = useMatrixFilters()
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

## Hook Structure

- **useMatrixController** – Single source for data loading; depends on `useMatrixWorker`
- **useMatrixFilters** – Filter state and persistence (used by Controller)
- **useColumnSelection** – Column visibility
- **useMatrixWorker** – Worker lifecycle and message handling (used internally by Controller)

See `docs/matrix/MATRIX_ARCHITECTURE.md` for data flow and module boundaries.

























