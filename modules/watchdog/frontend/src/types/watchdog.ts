/**
 * TypeScript types for Watchdog UI
 */

export interface WatchdogStatus {
  timestamp_chicago: string;
  engine_alive: boolean;
  engine_activity_state: 'ACTIVE' | 'IDLE_MARKET_CLOSED' | 'STALLED' | 'ENGINE_ACTIVE_PROCESSING' | 'ENGINE_MARKET_CLOSED' | 'ENGINE_IDLE_WAITING_FOR_DATA' | 'ENGINE_STALLED';
  last_engine_tick_chicago: string | null;
  engine_tick_stall_detected: boolean;
  recovery_state: string;
  kill_switch_active: boolean;
  connection_status: string;
  last_connection_event_chicago: string | null;
  stuck_streams: StreamStuckInfo[];
  execution_blocked_count: number;
  protective_failures_count: number;
  data_stall_detected: Record<string, DataStallInfo>;
  market_open: boolean | null;
  // PHASE 3.1: Identity invariants status
  last_identity_invariants_pass: boolean | null;
  last_identity_invariants_event_chicago: string | null;
  last_identity_violations: string[];
  // PATTERN 1: Bars expected observability
  bars_expected_count?: number;
  worst_last_bar_age_seconds?: number | null;
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
  instrument: string;
  session: string;
  trading_date: string;
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
}

export enum StreamStateEnum {
  PRE_HYDRATION = "PRE_HYDRATION",
  ARMED = "ARMED",
  RANGE_BUILDING = "RANGE_BUILDING",
  RANGE_LOCKED = "RANGE_LOCKED",
  DONE = "DONE"
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

export interface ApiResponse<T> {
  data: T | null;
  error: string | null;
}
