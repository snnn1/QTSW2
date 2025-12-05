# Scheduler Refactoring Plan

## Critical Issues Identified

1. **Single Responsibility Principle Violations** - Orchestrator does 10+ things
2. **Dangerous Subprocess Management** - Custom process supervisor needs extraction
3. **Ambiguous State Transitions** - Pipeline state unclear, can deadlock
4. **Global Mutable State** - EVENT_LOG_PATH, stage_results, DATA_* directories
5. **Non-Survivable Scheduling** - Infinite loop, no self-healing
6. **Inconsistent Logging** - Mixed JSON/console/ad-hoc formats
7. **GUI Logic Contamination** - debug_window mixed with pipeline logic
8. **Non-Atomic File Operations** - Race conditions possible
9. **Risky Data Deletion** - Heuristic string matching unsafe
10. **Unreachable Code** - Dead code after returns

## Refactoring Strategy

### Phase 1: Extract Services (High Priority)

#### 1.1 ProcessSupervisor Service
- Extract all subprocess management logic
- Reusable timeout/hang detection
- Consistent returncode handling
- Standardized stdout/stderr capture

#### 1.2 FileManager Service
- Atomic file operations
- Lock file mechanism (.lock files)
- Safe file deletion with verification
- Directory scanning with locking

#### 1.3 EventLogger Service
- Unified structured logging
- No global EVENT_LOG_PATH
- Consistent JSON format
- Proper flushing

#### 1.4 StageExecutor Service
- Single responsibility per stage
- Pure functions (no global state)
- Clear input/output contracts
- Deterministic behavior

### Phase 2: State Management (High Priority)

#### 2.1 PipelineState Class
- Immutable state transitions
- Clear stage dependencies
- Rollback capability
- No global mutations

#### 2.2 StageResult Enum
- Success/Failure/Skipped states
- Clear state machine
- No ambiguous transitions

### Phase 3: Scheduling (Medium Priority)

#### 3.1 Move to OS Scheduler
- Use Windows Task Scheduler or cron
- Simple script that runs once
- No infinite loops
- Self-healing via OS

#### 3.2 Or: Lightweight Job Runner
- Use Prefect/Dagster if needed
- But OS scheduler preferred for simplicity

### Phase 4: Cleanup (Medium Priority)

#### 4.1 Remove GUI Logic
- Extract debug_window to separate module
- Pipeline code should be headless-only

#### 4.2 Remove Dead Code
- Clean up unreachable code
- Remove commented sections

#### 4.3 Standardize Logging
- Single logging format
- Consistent across all stages

## Implementation Order

1. **Extract ProcessSupervisor** (fixes issue #2, helps #1)
2. **Extract FileManager** (fixes issue #8, #9)
3. **Extract EventLogger** (fixes issue #6, #4)
4. **Refactor PipelineOrchestrator** (fixes issue #1, #3)
5. **Create PipelineState** (fixes issue #3, #4)
6. **Remove GUI logic** (fixes issue #7)
7. **Move to OS scheduler** (fixes issue #5)
8. **Cleanup dead code** (fixes issue #10)

## File Structure After Refactoring

```
automation/
├── daily_data_pipeline_scheduler.py  # Main entry point (simple)
├── services/
│   ├── __init__.py
│   ├── process_supervisor.py        # Subprocess management
│   ├── file_manager.py              # File operations + locking
│   ├── event_logger.py              # Structured logging
│   └── stage_executor.py            # Stage execution logic
├── pipeline/
│   ├── __init__.py
│   ├── state.py                     # PipelineState class
│   ├── stages/
│   │   ├── __init__.py
│   │   ├── translator.py           # Translator stage
│   │   ├── analyzer.py              # Analyzer stage
│   │   └── merger.py                # Merger stage
│   └── orchestrator.py              # Thin orchestrator (delegates)
└── config.py                        # Configuration (no globals)
```

## Key Principles

1. **Single Responsibility** - Each class/function does one thing
2. **Pure Functions** - No global state mutations
3. **Explicit Dependencies** - Pass state as parameters
4. **Atomic Operations** - File operations with locking
5. **Deterministic** - Same inputs → same outputs
6. **Testable** - Easy to unit test each component
7. **Survivable** - OS handles scheduling, not our code



