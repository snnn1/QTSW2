"""
Incident Correlator (Phase 5)

Classifies primary vs secondary incidents to reduce alert spam.
When incidents cascade (e.g. CONNECTION_LOST -> DATA_STALL -> ENGINE_STALLED),
marks downstream incidents as secondary so only the root cause is alerted.
"""
from typing import Dict, Set

# Cascade map: downstream type -> set of upstream (root cause) types
# If any upstream is active when we end this incident, we're secondary
CASCADE_UPSTREAM: Dict[str, Set[str]] = {
    "DATA_STALL": {"CONNECTION_LOST"},
    "ENGINE_STALLED": {"CONNECTION_LOST", "DATA_STALL"},
}


def is_primary(incident_type: str, active_incident_types: Set[str]) -> bool:
    """
    Return True if this incident is primary (root cause), False if secondary (cascaded).
    When upstream types are active, we're secondary.
    """
    upstream = CASCADE_UPSTREAM.get(incident_type)
    if not upstream:
        return True  # No cascade defined, treat as primary
    return not bool(upstream & active_incident_types)


def tag_incident_record(
    record: dict,
    active_incident_types: Set[str],
) -> dict:
    """
    Add primary and root_cause fields to incident record.
    Mutates record in place and returns it.
    """
    incident_type = record.get("type", "")
    primary = is_primary(incident_type, active_incident_types)
    record["primary"] = primary
    if not primary:
        upstream = CASCADE_UPSTREAM.get(incident_type, set())
        # First upstream we find in active set (arbitrary; CONNECTION_LOST is typical root)
        for u in ("CONNECTION_LOST", "DATA_STALL"):
            if u in upstream and u in active_incident_types:
                record["root_cause"] = u
                break
    return record
