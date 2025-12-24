# Import functions for easier access
try:
    from .engine import run_strategy
except ImportError:
    # Fallback for when running from outside the package
    from engine import run_strategy
