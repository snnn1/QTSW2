from pydantic import BaseModel, Field
from typing import List, Literal, Dict, Tuple

Instrument = Literal["ES","NQ","YM","CL","NG","GC","MES","MNQ","MYM","MCL","MNG","MGC","MINUTEDATAEXPORT"]

SLOT_ENDS = {
    "S1": ["07:30","08:00","09:00"],
    "S2": ["09:30","10:00","10:30","11:00"],
}
SLOT_START = {"S1":"02:00","S2":"08:00"}

TICK_SIZE: Dict[Instrument,float] = {
    "ES": 0.25, "NQ": 0.25, "YM": 1.0, "CL": 0.01, "NG": 0.001, "GC": 0.1,
    "MES": 0.25, "MNQ": 0.25, "MYM": 1.0, "MCL": 0.01, "MNG": 0.001, "MGC": 0.1,
    "MINUTEDATAEXPORT": 0.25  # Treat as ES
}

TARGET_LADDER: Dict[Instrument, Tuple[float,...]] = {
    "ES": (10,15,20,25,30,35,40),
    "NQ": (50,75,100,125,150,175,200),
    "YM": (100,150,200,250,300,350,400),
    "CL": (0.5,0.75,1.0,1.25,1.5,1.75,2.0),
    "NG": (0.05,0.075,0.10,0.125,0.15,0.175,0.20),
    "GC": (5,7.5,10,12.5,15,17.5,20),
    "MES": (10,15,20,25,30,35,40),  # Same as ES but profit calculated as 1/10th
    "MNQ": (50,75,100,125,150,175,200),  # Same as NQ but profit calculated as 1/10th
    "MYM": (100,150,200,250,300,350,400),  # Same as YM but profit calculated as 1/10th
    "MCL": (0.5,0.75,1.0,1.25,1.5,1.75,2.0),  # Same as CL but profit calculated as 1/10th
    "MNG": (0.05,0.075,0.10,0.125,0.15,0.175,0.20),  # Same as NG but profit calculated as 1/10th
    "MGC": (5,7.5,10,12.5,15,17.5,20),  # Same as GC but profit calculated as 1/10th
    "MINUTEDATAEXPORT": (10,15,20,25,30,35,40)  # Same as ES
}

def base_target(inst: Instrument) -> float:
    return TARGET_LADDER[inst][0]

def get_target_profit(inst: Instrument, target_value: float) -> float:
    """Get the actual profit for a target value, accounting for micro-futures scaling."""
    if inst.startswith("M"):  # Micro-futures
        return target_value / 10.0  # Micro-futures are 1/10th the size
    else:
        return target_value  # Regular futures use the target value as-is

def stream_tag(inst: str, sess: str) -> str:
    return f"{inst.upper()}{'1' if sess=='S1' else '2'}"

def level_class(level_idx: int) -> int:
    if level_idx == 0: return 1
    if level_idx == 1: return 2
    return 3

# RunParams class moved to logic/config_logic.py to avoid duplication
