"""
SessionAuthority policy — explicit decisions before Phase 2 wiring.

## When no authority file exists (Phase 1)

**Decision:** Observability uses **synthetic auto** state: `session_trading_date` is exactly
`get_cme_trading_date(utc_now)` from `modules.timetable.cme_session`. The API does **not**
create `data/session/session_authority.json`, does **not** fail closed, and does **not**
block timetable/matrix writers (they are unchanged until Phase 2).

This is **not** "forced initialization": there is nothing to initialize for persistence until
an explicit writer or operator creates the file. The synthetic row is labeled
(`observation_reason=synthetic_no_file`, `state.reason=not_persisted`) so callers can tell
observation-only state from a real persisted record.

## Phase 2+ (explicit follow-up)

Whether publish paths **require** a persisted file first, or **auto-create** authority on
first boot/publish, is a separate product choice. If you require strict init, add
`initialization_required` semantics to those endpoints only — not to read-only GET.

Constants below are for API clarity and tests.
"""

# Observability-only semantics when data/session/session_authority.json is missing.
NO_FILE_OBSERVABILITY_MODE = "synthetic_auto_canonical"

# Synthetic state is not written to disk in Phase 1.
NO_FILE_CREATES_NO_FILE = True

# Phase 2 Model A: live timetable publishers require a valid persisted authority file.
STRICT_PUBLISH_REQUIRES_PERSISTED_AUTHORITY = True

# Phase 4: timetable_supervisor is validator-only (drift logs); it does not publish timetables.
SUPERVISOR_VALIDATOR_ONLY_NO_AUTO_PUBLISH = True
