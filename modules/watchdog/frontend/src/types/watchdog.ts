/**
 * TypeScript types for Watchdog UI
 */

export interface WatchdogStatus {
  /** When this /status payload was sealed (UTC ISO); align with stream-states snapshot_utc */
  snapshot_utc?: string;
  timestamp_chicago: string;
  engine_alive: boolean;
  engine_activity_state: 'ACTIVE' | 'IDLE_MARKET_CLOSED' | 'STALLED' | 'ENGINE_ACTIVE_PROCESSING' | 'ENGINE_MARKET_CLOSED' | 'ENGINE_IDLE_WAITING_FOR_DATA' | 'ENGINE_STALLED';
  last_engine_tick_chicago: string | null;
  engine_tick_stall_detected: boolean;
  recovery_state: string;
  /** Composite: recovery path clear, gate/adoption clear, kill switch off (matches backend) */
  execution_safe?: boolean;
  /** Robot ENGINE_TIMER_HEARTBEAT timetable identity ≠ system timetable_current.json identity */
  timetable_drift?: boolean;
  robot_timetable_hash?: string | null;
  /** Publisher / file JSON identity — drift compares this to robot heartbeat. */
  timetable_publisher_hash?: string | null;
  /** Content hash (C# TimetableContentHasher parity); not used for drift. */
  timetable_content_hash?: string | null;
  /** @deprecated Prefer timetable_publisher_hash; same value when identity is known. */
  current_timetable_hash?: string | null;
  /** Effective session trading date from timetable poller (may match trading_date). */
  session_trading_date?: string | null;
  timetable_last_ok_utc?: string | null;
  robot_timetable_observed_chicago?: string | null;
  /** OK | ENGAGED | FAIL_CLOSED — state-consistency / reconciliation gate from robot */
  reconciliation_gate_state?: string;
  reconciliation_gate_since_chicago?: string | null;
  reconciliation_gate_last_detail?: Record<string, unknown> | null;
  adoption_grace_expired_active?: boolean;
  kill_switch_active: boolean;
  connection_status: string;
  /** Authoritative: LOST | RECOVERING | STABLE (deterministic, timestamp-driven) */
  derived_connection_state?: 'LOST' | 'RECOVERING' | 'STABLE';
  last_connection_event_chicago: string | null;
  session_connectivity?: SessionConnectivityInfo | null;
  last_connectivity_daily_summary?: ConnectivityDailySummary | null;
  stuck_streams: StreamStuckInfo[];
  execution_blocked_count: number;
  protective_failures_count: number;
  data_stall_detected: Record<string, DataStallInfo>;
  data_status?: 'FLOWING' | 'STALLED' | 'ACCEPTABLE_SILENCE' | 'UNKNOWN';
  /** Engine activity: RUNNING | IDLE | STALLED (derived from heartbeat + tick activity) */
  engine_activity_classification?: 'RUNNING' | 'IDLE' | 'STALLED';
  /** Feed health: DATA_FLOWING | DATA_STALLED | MARKET_CLOSED */
  feed_health_classification?: 'DATA_FLOWING' | 'DATA_STALLED' | 'MARKET_CLOSED';
  market_open: boolean | null;
  // PHASE 3.1: Identity invariants status
  last_identity_invariants_pass: boolean | null;
  last_identity_invariants_event_chicago: string | null;
  last_identity_violations: string[];
  // PATTERN 1: Bars expected observability
  bars_expected_count?: number;
  worst_last_bar_age_seconds?: number | null;
  // Fill health (execution logging hygiene)
  fill_health?: FillHealthInfo | null;
  trading_date?: string | null;
  /**
   * Last timetable_current.json ``source`` (auto_roll, master_matrix, dashboard_ui, …).
   * Null if last poll failed. Same as backend status ``timetable_source``.
   */
  timetable_source?: string | null;
  /** True when watchdog could not read enabled streams from timetable file (robot may still fail-closed). */
  enabled_streams_unknown?: boolean;
  /** Alias for enabled_streams_unknown — no reliable timetable-derived stream list. */
  timetable_unavailable?: boolean;
  // Phase 1: Active push alerts (process stopped, heartbeat lost, etc.)
  active_alerts?: ActiveAlert[];
}

export interface ActiveAlert {
  alert_id: string;
  alert_type: string;
  severity: string;
  first_seen_utc: string;
  last_seen_utc: string;
  dedupe_key: string;
  context?: Record<string, unknown>;
  delivery_status?: string;
}

export interface FillHealthInfo {
  trading_date: string;
  total_fills: number;
  mapped_fills: number;
  unmapped_fills: number;
  null_trading_date_fills: number;
  fill_coverage_rate: number;
  unmapped_rate: number;
  null_trading_date_rate: number;
  fill_health_ok: boolean;
  /** Event-based counts (last 1h): fills not in ledger */
  broker_flatten_fill_count?: number;
  execution_update_unknown_order_critical_count?: number;
  execution_fill_blocked_count?: number;
  execution_fill_unmapped_count?: number;
}

export interface SessionConnectivityInfo {
  session: string;
  trading_date: string;
  disconnect_count: number;
  total_downtime_seconds: number;
  last_disconnect_chicago: string | null;
  currently_disconnected: boolean;
}

export interface ConnectivityDailySummary {
  disconnect_count: number;
  avg_duration_seconds: number;
  max_duration_seconds: number;
  total_downtime_seconds: number;
  short_disconnects?: number;
  long_disconnects?: number;
}

export interface StreamStuckInfo {
  stream: string;
  instrument: string;
  state: string;
  stuck_duration_seconds: number;
  state_entry_time_chicago: string;
}

export interface DataStallInfo {
  instrument: string;
  last_bar_chicago: string;
  stall_detected: boolean;
  market_open: boolean;
}

export interface RiskGateStatus {
  timestamp_chicago: string;
  recovery_state_allowed: boolean;
  kill_switch_allowed: boolean;
  /** recovery_state_allowed && kill_switch_allowed */
  execution_safe?: boolean;
  timetable_validated: boolean;
  stream_armed: StreamArmedStatus[];
  session_slot_time_valid: boolean;
  trading_date_set: boolean;
}

export interface StreamArmedStatus {
  stream: string;
  armed: boolean;
}

export interface StreamState {
  stream: string;
  instrument: string;  // Canonical instrument (e.g., "RTY", "ES") - DO NOT CHANGE
  execution_instrument?: string | null;  // Full contract name (e.g., "M2K 03-26", "MES 03-26")
  session: string;
  trading_date: string;
  /** Present on API rows: stream is enabled in timetable_current.json */
  timetable_enabled?: boolean;
  /** Same as GET /status execution_safe at snapshot time (system-wide) */
  system_tradable_now?: boolean;
  state: StreamStateEnum | string;  // Allow empty string for streams without state yet
  committed: boolean;
  commit_reason: string | null;
  slot_time_chicago: string | null;
  slot_time_utc: string | null;
  range_high: number | null;
  range_low: number | null;
  freeze_close: number | null;
  range_invalidated: boolean;
  state_entry_time_utc: string;
  range_locked_time_utc: string | null;
  range_locked_time_chicago: string | null;
  trade_executed?: boolean | null;
  slot_reason?: string | null;
}

export enum StreamStateEnum {
  PRE_HYDRATION = "PRE_HYDRATION",
  ARMED = "ARMED",
  RANGE_BUILDING = "RANGE_BUILDING",
  RANGE_LOCKED = "RANGE_LOCKED",
  OPEN = "OPEN",  // Position open, managing stop/target
  DONE = "DONE"
}

/** Active intent not on today's timetable — from GET /stream-states (timetable ``streams`` unchanged). */
export interface OutOfTimetableActiveStream {
  intent_id: string;
  stream_id: string;
  instrument: string;
  trading_date: string;
  direction: string;
  remaining_exposure: number;
  entry_filled_qty: number;
  exit_filled_qty: number;
}

/** Expected vs actual: robot reported slot ended without trade (SLOT_END_SUMMARY). */
export interface ExecutionExpectationGap {
  stream_id: string;
  trading_date: string;
  instrument: string;
  session: string;
  watchdog_state: string;
  gap_type: string;
  /** v1: false; v2 slot_missing_summary: null */
  trade_executed: boolean | null;
  slot_reason: string | null;
  expected: string;
  actual: string;
  detail: string;
  timetable_slot_time?: string;
  slot_boundary_chicago?: string | null;
}

export interface StreamStatesResponse {
  snapshot_utc?: string;
  timestamp_chicago: string;
  streams: StreamState[];
  /** Present on current API; missing on older backends — treat as []. */
  out_of_timetable_active_streams?: OutOfTimetableActiveStream[];
  /** Timetable streams with explicit slot-end “no trade” vs planned slot (see backend doc). */
  execution_expectation_gaps?: ExecutionExpectationGap[];
  timetable_unavailable?: boolean;
  enabled_streams_unknown?: boolean;
  timetable_source?: string | null;
}

export interface IntentExposure {
  intent_id: string;
  stream_id: string;
  instrument: string;
  direction: string;
  quantity?: number;
  entry_filled_qty: number;
  exit_filled_qty: number;
  remaining_exposure: number;
  state: IntentExposureState | string;
  entry_filled_at_chicago?: string | null;
}

export enum IntentExposureState {
  ACTIVE = "ACTIVE",
  CLOSED = "CLOSED",
  STANDING_DOWN = "STANDING_DOWN"
}

export interface UnprotectedPosition {
  intent_id: string;
  stream: string;
  instrument: string;
  direction: string;
  entry_filled_at_chicago: string;
  unprotected_duration_seconds: number;
}

export interface WatchdogEvent {
  event_seq: number;
  /** Phase 4: Canonical ID for REST/WS dedupe (run_id:event_seq) */
  event_id?: string;
  run_id: string;
  timestamp_utc: string;
  timestamp_chicago: string;
  event_type: string;
  trading_date: string | null;
  stream: string | null;
  instrument: string | null;
  session: string | null;
  data: Record<string, any>;
}

export interface ExecutionJournalEntry {
  intent_id: string;
  trading_date: string;
  stream: string;
  instrument: string;
  entry_submitted: boolean;
  entry_submitted_at: string | null;
  entry_submitted_at_chicago?: string | null;
  entry_filled: boolean;
  entry_filled_at: string | null;
  entry_filled_at_chicago?: string | null;
  broker_order_id: string | null;
  entry_order_type: string | null;
  fill_price: number | null;
  fill_quantity: number | null;
  rejected: boolean;
  rejected_at: string | null;
  rejected_at_chicago?: string | null;
  rejection_reason: string | null;
  be_modified: boolean;
  be_modified_at: string | null;
  be_modified_at_chicago?: string | null;
  expected_entry_price: number | null;
  actual_fill_price: number | null;
  slippage_points: number | null;
  slippage_dollars: number | null;
  commission: number | null;
  fees: number | null;
  total_cost: number | null;
}

export interface StreamJournal {
  trading_date: string;
  stream: string;
  committed: boolean;
  commit_reason: string | null;
  state: string;
  last_update_chicago?: string;
}

export interface ExecutionSummary {
  intents_seen: number;
  intents_executed: number;
  orders_submitted: number;
  orders_rejected: number;
  orders_filled: number;
  orders_blocked: number;
  blocked_by_reason: Record<string, number>;
  duplicates_skipped: number;
  intent_details: IntentSummary[];
  total_slippage_dollars: number;
  total_commission: number;
  total_fees: number;
  total_execution_cost: number;
}

export interface IntentSummary {
  intent_id: string;
  trading_date: string;
  stream: string;
  instrument: string;
  executed: boolean;
  orders_submitted: number;
  orders_rejected: number;
  orders_filled: number;
  order_types: string[];
  rejection_reasons: string[];
  blocked: boolean;
  block_reason: string | null;
  duplicate_skipped: boolean;
  slippage_dollars: number | null;
  commission: number | null;
  fees: number | null;
  total_cost: number | null;
}

export interface DailyJournalTrade {
  intent_id: string;
  direction: string;
  entry_price: number | null;
  exit_price: number | null;
  entry_qty: number | null;
  exit_qty: number;
  realized_pnl: number | null;
  costs_allocated: number;
  status: string;
  exit_order_type: string | null;
  /** Win | Loss | BE */
  result?: string;
  /** Exit fill timestamp (UTC ISO string) */
  exit_filled_at?: string | null;
}

export interface DailyJournalStream {
  stream: string;
  instrument: string;
  committed: boolean;
  commit_reason: string | null;
  state: string;
  realized_pnl: number;
  trade_count: number;
  intent_count: number;
  closed_count: number;
  partial_count: number;
  open_count: number;
  total_costs_realized: number;
  trades: DailyJournalTrade[];
}

export interface DailyJournal {
  trading_date: string;
  total_pnl: number;
  streams: DailyJournalStream[];
  summary: ExecutionSummary | null;
}

export interface ApiResponse<T> {
  data: T | null;
  error: string | null;
}
