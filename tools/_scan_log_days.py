"""One-off: scan logs/robot for Chicago-day coverage (not shipped as product)."""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

try:
    from zoneinfo import ZoneInfo

    CHI = ZoneInfo("America/Chicago")
except Exception:
    CHI = None

LOG_DIR = Path(__file__).resolve().parent.parent / "logs" / "robot"


def collect_paths() -> list[Path]:
    paths: list[Path] = []
    if (LOG_DIR / "robot_ENGINE.jsonl").is_file():
        paths.append(LOG_DIR / "robot_ENGINE.jsonl")
    for p in sorted(LOG_DIR.glob("robot_ENGINE_*.jsonl")):
        if p not in paths:
            paths.append(p)
    arch = LOG_DIR / "archive"
    if arch.is_dir():
        for p in sorted(arch.glob("robot_ENGINE*.jsonl")):
            paths.append(p)
    for p in sorted(LOG_DIR.glob("robot_*.jsonl")):
        if p.name.startswith("robot_") and p not in paths:
            paths.append(p)
    seen: set[Path] = set()
    out: list[Path] = []
    for p in paths:
        r = p.resolve()
        if r not in seen:
            seen.add(r)
            out.append(p)
    return out


def parse_ts(ts_raw):
    if ts_raw is None:
        return None
    s = str(ts_raw)
    try:
        if isinstance(ts_raw, (int, float)):
            return datetime.fromtimestamp(float(ts_raw) / 1000.0, tz=timezone.utc)
        if s.endswith("Z"):
            return datetime.fromisoformat(s[:-1]).replace(tzinfo=timezone.utc)
        dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            return dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc)
    except Exception:
        return None


def chicago_date(dt):
    if CHI is None or dt is None:
        return None
    return dt.astimezone(CHI).date()


def is_engine_activity(ev: str) -> bool:
    if not ev:
        return False
    if ev in ("ENGINE_TIMER_HEARTBEAT", "ENGINE_CPU_PROFILE", "BAR_ACCEPTED"):
        return True
    return ev.startswith("ENGINE_TICK_STALL")


def main() -> None:
    T = defaultdict(
        lambda: {"lines": 0, "eng_stream": 0, "act": 0, "hb": 0, "cpu": 0, "reco": 0}
    )

    for fp in collect_paths():
        try:
            f = fp.open("r", encoding="utf-8-sig", errors="replace")
        except OSError:
            continue
        with f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    o = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(o, dict):
                    continue
                dt = parse_ts(o.get("ts_utc") or o.get("ts") or o.get("timestamp"))
                if dt is None:
                    continue
                d = chicago_date(dt)
                if d is None:
                    continue
                ev = str(o.get("event") or o.get("event_type") or "")
                data = o.get("data")
                data = data if isinstance(data, dict) else {}
                src = (o.get("source") or "").lower()
                stream = data.get("stream")
                eng_stream = ("engine" in src) or (stream == "__engine__")
                m = T[d]
                m["lines"] += 1
                if eng_stream:
                    m["eng_stream"] += 1
                if is_engine_activity(ev):
                    m["act"] += 1
                if ev == "ENGINE_TIMER_HEARTBEAT":
                    m["hb"] += 1
                if ev == "ENGINE_CPU_PROFILE":
                    m["cpu"] += 1
                if ev == "RECONCILIATION_PASS_SUMMARY":
                    m["reco"] += 1

    def engine_full_like(m: dict) -> bool:
        cov = (m["hb"] > 0) or (m["cpu"] > 0) or (m["reco"] > 0)
        return m["eng_stream"] >= 50 and m["act"] > 0 and cov

    days = sorted(T.keys())
    print("Log directory:", LOG_DIR)
    print("Chicago calendar days with any lines:", len(days))
    print()
    full_days = [d for d in days if engine_full_like(T[d])]
    print("Days meeting ENGINE_FULL-like gate (eng_stream>=50, activity, hb|cpu|reco):", len(full_days))
    for d in full_days:
        m = T[d]
        print(f"  {d}  total={m['lines']} eng_stream={m['eng_stream']} act_events={m['act']}")
    print()
    print("Other days with material volume (total lines >= 1000) but not FULL-like:")
    for d in days:
        if d in full_days:
            continue
        m = T[d]
        if m["lines"] >= 1000:
            print(f"  {d}  total={m['lines']} eng_stream={m['eng_stream']}")
    print()
    print("Sparse days (1-999 lines):")
    sparse = [d for d in days if 1 <= T[d]["lines"] < 1000]
    for d in sparse:
        print(f"  {d}  total={T[d]['lines']}")


if __name__ == "__main__":
    main()
