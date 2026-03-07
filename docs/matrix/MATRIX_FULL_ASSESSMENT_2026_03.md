# Matrix Application Full Assessment

**Date:** March 2026  
**Scope:** `modules/matrix` (backend), `modules/matrix_timetable_app` (frontend), data flow, UX, performance, maintainability

---

## Executive Summary

The matrix application has a solid architecture with clear separation between backend (FastAPI + Python), frontend (React + Vite), and a Web Worker for heavy client-side computation. The main improvement opportunities are: **App.jsx size** (~4,200 lines), **duplication** in filter/breakdown logic, **API schema mismatches**, **missing frontend tests**, and **production logging cleanup**.

---

## 1. Architecture & Structure

### Components

| Layer | Key Files | Role |
|-------|-----------|------|
| **Backend** | `api.py` (~770 lines) | FastAPI router: build, resequence, data, breakdown, stream-stats, freshness |
| **Backend core** | `master_matrix.py`, `data_loader.py`, `sequencer_logic.py`, `filter_engine.py`, `statistics.py` | Build, load, sequence, filter, compute stats |
| **State/cache** | `matrix_state.py`, `cache.py`, `_matrix_data_cache` | In-process DataFrame cache, parquet discovery |
| **Frontend** | `App.jsx` (~4,248 lines) | Main UI, tab orchestration, stats, filters |
| **Worker** | `matrixWorker.js` (~2,100 lines) | Filtering, stats, profit breakdown, timetable |
| **Hooks** | `useMatrixController`, `useMatrixFilters`, `useMatrixData`, `useColumnSelection` | State, API, filters, columns |
| **API client** | `matrixApi.js` | Thin wrapper around backend endpoints |

### Data Flow

1. **Load:** `useMatrixController.loadMasterMatrix` → `matrixApi.getMatrixData` → FastAPI `/api/matrix/data` → `MatrixState` or disk → returns rows + `stats_full`
2. **Filter:** User changes filters → `workerFilter` → worker applies filters → `workerFilteredIndices` / `workerFilteredRows`
3. **Stats:** Backend `stats_full` for full-history; worker stats as fallback (partial data)
4. **Breakdown:** `calculateProfitBreakdown` → worker → POST `/api/matrix/breakdown` (or worker-only for some tabs)

### Boundaries

- **Frontend:** React 18, Vite, Tailwind, react-window
- **Backend:** FastAPI (mounted via dashboard backend)
- **Worker:** Dedicated Web Worker for CPU-heavy work
- **Matrix module:** Standalone; no direct timetable app dependency

---

## 2. Code Quality

### File Sizes

| File | Lines | Assessment |
|------|-------|------------|
| `App.jsx` | ~4,248 | **Too large** – split into tab panels, stats, filters |
| `api.py` | ~770 | Large – breakdown logic could be extracted |
| `matrixWorker.js` | ~2,100 | Large – timetable logic is complex |
| `master_matrix.py` | ~990 | Reasonable |
| `statistics.py` | ~1,167 | Large but focused |
| `useMatrixController.js` | ~640 | Many effects; coordination-heavy |

### Duplication

1. **Stream filter conversion** – Same `streamFilters` → API format in `buildMatrix`, `resequenceMatrix`, `getProfitBreakdown` in `matrixApi.js`
2. **Contract values** – Duplicated in `DataTable.jsx`, `matrixWorker.js`, `statistics.py`; should be a single source
3. **DOW/DOM filter application** – Repeated in worker timetable logic (multiple blocks)
4. **Breakdown grouping** – Similar patterns for day/dom/time/month/year in `api.py`

### Complexity Hotspots

1. **`matrixWorker.js` CALCULATE_TIMETABLE** – ~400 lines; time slots, previous-day handling, filter checks
2. **`api.py` get_matrix_data** – ~200 lines; cache, date filter, cleaning, stats
3. **`useMatrixController`** – Many `useEffect`/`useCallback`; filter/worker coordination
4. **`App.jsx` renderStats** – Long conditional chain for backend vs worker stats

### Hook Usage

- `useMatrixController` – Central orchestration; depends on `useMatrixWorker`
- `useMatrixFilters` – Filter state and persistence
- `useMatrixData` – Partially used; some overlap with `useMatrixController`
- `useColumnSelection` – Column visibility
- `useMatrixWorker` – Worker lifecycle and message handling

---

## 3. Performance

### Caching

| Location | Key | Notes |
|----------|-----|-------|
| Backend `_matrix_data_cache` | (file_path, mtime, stream_include, contract_multiplier, include_filtered_executed, include_stats) | Per-request stats |
| MatrixState | In-process DataFrame | mtime invalidation |
| Worker | `filterCache`, `profitBreakdownCache` | Filter and breakdown results |

### Pagination

- **API:** `limit` (default 10k), `order`, `start_date`/`end_date`
- **Frontend:** Virtualized table; `onLoadMoreRows` when scrolling near end
- **Worker:** Returns first 200 rows; more via `GET_ROWS`

### API Design

- **GET /api/matrix/data** – Many query params; `include_stats=false`, `nocache=true` for lighter loads
- **POST /api/matrix/breakdown** – Loads full parquet each time; **no caching**
- **POST /api/matrix/stream-stats** – Loads full parquet per stream; **no shared cache** with `/data`

---

## 4. UX/UI

### Loading States

- `masterLoading`, `masterStatsLoading`, `statsLoading`, `breakdownLoading`, `timetableLoading`, `backendStreamStatsLoading`
- DataTable: `...` for unloaded rows, "loading more..." when fetching
- Backend readiness polling with 30s timeout

### Error Handling

- Try/catch in `App` with error UI
- `masterError`, `workerError`, `backendConnectionError` surfaced
- No global error boundary around main app tree

### Responsiveness

- `useTransition` and `useDeferredValue` for tab switching
- Virtualized table with `react-window` List
- Fixed table height (600px); horizontal scroll for wide tables

### Potential Issues

- **Auto-update:** Resequence every 20 min then `window.location.reload()` – full page reload
- **DataTable:** Uses custom row component; verify react-window `List` API usage

---

## 5. API Schema & Consistency

### Mismatches

1. **Build request:** Frontend sends `warmup_months`, `visible_years`; `MatrixBuildRequest` does not define them → **ignored by backend**
2. **include_filtered_executed:** API default `False`; frontend default `true` – now aligned via refetch with `nocache`

### Naming

- Frontend: camelCase
- Backend: snake_case
- API: snake_case in request/response

---

## 6. Maintainability

### Documentation

- `hooks/README.md` – Describes hooks; notes optional integration
- `statistics.py` – Clear docstrings and guarantees
- `api.py` – Endpoint docstrings
- `docs/matrix/MATRIX_OPTIMIZATION_SUMMARY.md` – Telemetry, phases
- No top-level architecture or data-flow doc

### Test Coverage

- **Backend:** `modules/matrix/tests/` – matrix functionality, diagnostic, optimization, golden outputs, startup
- **Frontend:** No unit or integration tests
- **API + frontend:** No integration tests

### Logging

- `console.log` in production code (`matrixApi.js`, `App.jsx`, `useMatrixController.js`) – should use a logging utility or remove for production

---

## 7. Improvement Recommendations

### High Priority

| # | Item | Effort | Impact |
|---|------|--------|--------|
| 1 | **Split App.jsx** – Extract tab panels (StatsPanel, FiltersPanel, TimetableTab, etc.) into separate components | Medium | High |
| 2 | **Align build API** – Add `warmup_months`, `visible_years` to `MatrixBuildRequest` and pass to `build_master_matrix`, or remove from frontend | Low | Medium |
| 3 | **Remove/replace console.log** – Use `import.meta.env.DEV` guard or a logger | Low | Low |
| 4 | **Add error boundary** – Wrap main app tree for uncaught errors | Low | Medium |

### Medium Priority

| # | Item | Effort | Impact |
|---|------|--------|--------|
| 5 | **Extract breakdown logic** – Move day/dom/time/month/year logic in `api.py` into helpers or a service | Medium | Medium |
| 6 | **Shared contract values** – Single source (e.g. `constants.js` + backend config) for contract values | Low | Medium |
| 7 | **Cache breakdown and stream-stats** – Add caching where parameters allow | Medium | Medium |
| 8 | **Frontend tests** – Unit tests for hooks (`useMatrixFilters`, `useMatrixController`) and key components | Medium | High |
| 9 | **Reduce timetable duplication** – Centralize DOW/DOM/time filter application in worker | Medium | Medium |

### Lower Priority

| # | Item | Effort | Impact |
|---|------|--------|--------|
| 10 | **Improve auto-update** – Incremental refresh instead of full page reload | High | Medium |
| 11 | **Architecture doc** – Short doc for data flow and module boundaries | Low | Low |
| 12 | **Consolidate useMatrixData** – Clarify vs `useMatrixController` or merge | Low | Low |

---

## 8. Quick Wins

These can be done in a single session:

1. Add `warmup_months` and `visible_years` to `MatrixBuildRequest` (or document that they are ignored)
2. Guard `console.log` with `if (import.meta.env.DEV)`
3. Add React error boundary around `AppContent`
4. Create `CONTRACT_VALUES` constant shared by frontend/worker (backend can stay as-is for now)

---

## 9. References

- `docs/matrix/MATRIX_OPTIMIZATION_SUMMARY.md` – Telemetry, phases, validation
- `docs/matrix/MATRIX_DOWNSTREAM_CONTRACT.md` – Required columns
- `modules/matrix_timetable_app/frontend/src/hooks/README.md` – Hooks overview
