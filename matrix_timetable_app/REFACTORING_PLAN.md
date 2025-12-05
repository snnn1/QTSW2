# Matrix App Refactoring Plan

**STATUS: ✅ COMPLETED** - See `REFACTORING_COMPLETE.md` for details

## Original State (Before Refactoring)
- **App.jsx**: 3,097 lines in a single file
- **Complexity**: High - many functions/hooks/components
- **Maintainability**: Difficult to navigate and modify

## Refactoring Goals ✅ ACHIEVED
1. ✅ Split into smaller, focused components
2. ✅ Extract reusable hooks
3. ✅ Separate utilities and constants
4. ✅ Improve code organization and maintainability

## Structure Created

```
src/
├── components/          # React components
│   ├── TabNavigation.jsx     ✅ Created
│   └── ProfitTable.jsx      ✅ Created
├── hooks/               # Custom React hooks
│   └── (to be created)
├── utils/               # Utility functions and constants
│   └── constants.js         ✅ Created
└── App.jsx              # Main app (to be simplified)
```

## Components to Extract

### ✅ Completed
1. **TabNavigation** - Tab switching UI
2. **ProfitTable** - Profit breakdown table display
3. **constants.js** - All constants and configuration

### 🔄 In Progress / Next Steps

4. **StatsPanel** - Statistics display component
   - Location: `renderStats()` function (~200 lines)
   - Props: `streamId`, `stats`, `workerReady`, etc.

5. **FiltersPanel** - Filter controls component
   - Location: `renderFilters()` function (~150 lines)
   - Props: `streamId`, `filters`, `onFilterChange`, etc.

6. **ColumnSelector** - Column selection UI
   - Location: `renderColumnSelector()` function (~70 lines)
   - Props: `columns`, `selectedColumns`, `onToggle`, etc.

7. **DataTable** - Main data table with virtual scrolling
   - Location: `renderDataTable()` function (~200 lines)
   - Props: `data`, `streamId`, `columns`, etc.

8. **TimetableTab** - Timetable view
   - Location: Lines ~3140-3200
   - Props: `timetable`, `loading`, `error`, etc.

9. **MasterMatrixTab** - Master matrix view
   - Location: Lines ~3251-3400
   - Props: `data`, `loading`, `filters`, etc.

10. **BreakdownTabs** - Profit breakdown views (Time, Day, DOM, Month, Year)
    - Location: Lines ~3200-3250
    - Props: `breakdownData`, `periodType`, etc.

## Custom Hooks to Create

1. **useMatrixFilters** - Filter management logic
   - Filter state
   - localStorage persistence
   - Filter updates

2. **useMatrixData** - Data loading and management
   - API calls
   - Data state
   - Loading/error states

3. **useMatrixColumns** - Column management
   - Column selection
   - Column ordering
   - localStorage persistence

## Benefits

- **Easier Navigation**: Find code by component name
- **Better Testing**: Test components in isolation
- **Reusability**: Components can be reused
- **Maintainability**: Smaller files are easier to understand
- **Performance**: Better code splitting opportunities

## Migration Strategy

1. Extract components one at a time
2. Test after each extraction
3. Update imports in App.jsx
4. Gradually reduce App.jsx size
5. Final App.jsx should be ~200-300 lines (orchestration only)

## Next Steps

Continue extracting components systematically, starting with the most self-contained ones.

