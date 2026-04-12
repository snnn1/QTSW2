"""
Module reloading utility for hot-reloading code changes.

This module provides utilities to reload Python modules from disk, ensuring
that code changes are picked up without restarting the server.
"""

import importlib
import sys
import logging
from typing import List, Optional

logger = logging.getLogger(__name__)


def reload_matrix_modules(
    modules_to_reload: Optional[List[str]] = None,
    clear_all_matrix_modules: bool = True
) -> None:
    """
    Reload matrix modules to pick up code changes.
    
    This function:
    1. Removes matrix modules from sys.modules cache
    2. Clears import cache
    3. Reloads specified modules (or default set)
    
    Args:
        modules_to_reload: List of module names to reload (e.g., ['modules.matrix.filter_engine'])
                          If None, reloads default set: filter_engine, sequencer_logic
        clear_all_matrix_modules: If True, removes all 'matrix' modules from cache first
    """
    # Default modules to reload
    if modules_to_reload is None:
        modules_to_reload = [
            'modules.matrix.filter_engine',
            'modules.matrix.sequencer_logic',
            'modules.matrix.trade_selector',
            'modules.matrix.history_manager'
        ]
    
    # Remove all matrix-related modules from cache
    if clear_all_matrix_modules:
        modules_to_remove = [
            key for key in list(sys.modules.keys())
            if 'master_matrix' in key or 'matrix' in key
        ]
        for module_name in modules_to_remove:
            if 'matrix' in module_name.lower():
                del sys.modules[module_name]
                logger.debug(f"Removed {module_name} from sys.modules")
    
    # Clear import cache
    importlib.invalidate_caches()
    logger.debug("Cleared import cache")
    
    # Reload specified modules
    for module_name in modules_to_reload:
        try:
            if module_name in sys.modules:
                # Module already imported, reload it
                importlib.reload(sys.modules[module_name])
                logger.info(f"Reloaded {module_name} module")
            else:
                # Module not yet imported, import it fresh
                importlib.import_module(module_name)
                logger.info(f"Imported {module_name} module")
        except Exception as e:
            logger.warning(f"Could not reload {module_name}: {e}")


def ensure_matrix_modules_reloaded() -> None:
    """
    Convenience function to ensure all critical matrix modules are reloaded.
    
    This is called before building the matrix to ensure latest code is used.
    """
    reload_matrix_modules(
        modules_to_reload=[
            'modules.matrix.filter_engine',
            'modules.matrix.sequencer_logic',
            'modules.matrix.trade_selector',
            'modules.matrix.history_manager',
            'modules.matrix.data_loader',
            'modules.matrix.schema_normalizer',
            'modules.matrix.utils'  # Ensure normalize_time is reloaded
        ],
        clear_all_matrix_modules=True
    )

