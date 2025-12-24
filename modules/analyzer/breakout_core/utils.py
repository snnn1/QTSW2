import numpy as np

def hhmm_to_sort_int(hhmm: str) -> int:
    h, m = map(int, hhmm.split(":"))
    return h*100 + m

def round_to_tick(x: float, tick: float) -> float:
    return np.round(x / tick) * tick

def floor_to_tick(x: float, tick: float) -> float:
    return np.floor(x / tick) * tick

def grid_lock(mfe: float, grid: float, tick: float) -> float:
    if mfe <= 0: return 0.0
    return floor_to_tick(np.floor(mfe / grid) * grid, tick)
