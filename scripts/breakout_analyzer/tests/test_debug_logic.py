"""
Test suite for quant-grade debug logic
"""

import pytest
from datetime import datetime
from logic.debug_logic import DebugManager, DebugRecord, DebugError


class TestLogCreation:
    """Test basic log creation and record structure"""
    
    def test_log_creates_record_with_mandatory_context(self):
        """Test that log_warning creates a record with correct structure"""
        manager = DebugManager()
        
        context = {
            "module": "DataManager",
            "instrument": "ES",
            "removed_count": 3
        }
        
        manager.log_warning("DATA", "Duplicate timestamps removed", context)
        
        records = manager.get_logs()
        assert len(records) == 1
        
        record = records[0]
        assert record.level == "WARNING"
        assert record.category == "DATA"
        assert record.message == "Duplicate timestamps removed"
        assert record.context == context
        assert isinstance(record.created_at, datetime)
    
    def test_log_without_context_raises(self):
        """Test that logging without context raises ValueError"""
        manager = DebugManager()
        
        with pytest.raises(ValueError, match="context must be a dict"):
            manager.log_warning("DATA", "Test message", None)
        
        with pytest.raises(ValueError, match="context must be a dict"):
            manager.log_warning("DATA", "Test message", "not a dict")
    
    def test_log_without_module_in_context_raises(self):
        """Test that context missing 'module' key raises ValueError"""
        manager = DebugManager()
        
        context = {"instrument": "ES"}  # Missing "module"
        
        with pytest.raises(ValueError, match="context must contain 'module' key"):
            manager.log_warning("DATA", "Test message", context)
    
    def test_all_log_levels_create_records(self):
        """Test that all log levels (DEBUG, INFO, WARNING) create records"""
        manager = DebugManager()
        
        context = {"module": "TestModule"}
        
        manager.log_debug("TEST", "Debug message", context)
        manager.log_info("TEST", "Info message", context)
        manager.log_warning("TEST", "Warning message", context)
        
        records = manager.get_logs()
        assert len(records) == 3
        
        levels = [r.level for r in records]
        assert "DEBUG" in levels
        assert "INFO" in levels
        assert "WARNING" in levels


class TestMinLevelFiltering:
    """Test minimum level filtering"""
    
    def test_min_level_filters_lower_levels(self):
        """Test that min_level filters out lower priority logs"""
        manager = DebugManager(min_level="WARNING")
        
        context = {"module": "TestModule"}
        
        manager.log_debug("TEST", "Debug message", context)
        manager.log_info("TEST", "Info message", context)
        manager.log_warning("TEST", "Warning message", context)
        
        # log_error always raises, so catch the exception
        with pytest.raises(DebugError):
            manager.log_error("TEST", "Error message", context)
        
        records = manager.get_logs()
        assert len(records) == 2  # Only WARNING and ERROR
        
        levels = [r.level for r in records]
        assert "DEBUG" not in levels
        assert "INFO" not in levels
        assert "WARNING" in levels
        assert "ERROR" in levels
    
    def test_min_level_debug_stores_all(self):
        """Test that min_level=DEBUG stores all levels"""
        manager = DebugManager(min_level="DEBUG")
        
        context = {"module": "TestModule"}
        
        manager.log_debug("TEST", "Debug", context)
        manager.log_info("TEST", "Info", context)
        manager.log_warning("TEST", "Warning", context)
        
        records = manager.get_logs()
        assert len(records) == 3
    
    def test_min_level_error_stores_only_errors(self):
        """Test that min_level=ERROR stores only errors"""
        manager = DebugManager(min_level="ERROR")
        
        context = {"module": "TestModule"}
        
        manager.log_debug("TEST", "Debug", context)
        manager.log_info("TEST", "Info", context)
        manager.log_warning("TEST", "Warning", context)
        
        # log_error always raises, so catch the exception
        with pytest.raises(DebugError):
            manager.log_error("TEST", "Error", context)
        
        records = manager.get_logs()
        assert len(records) == 1
        assert records[0].level == "ERROR"


class TestLogError:
    """Test error logging behavior"""
    
    def test_log_error_stores_record_and_raises(self):
        """Test that log_error stores record AND raises exception"""
        manager = DebugManager()
        
        context = {"module": "TestModule", "instrument": "ES"}
        
        with pytest.raises(DebugError) as exc_info:
            manager.log_error("DATA", "Critical data error", context)
        
        # Verify exception contains correct information
        assert exc_info.value.level == "ERROR"
        assert exc_info.value.category == "DATA"
        assert exc_info.value.message == "Critical data error"
        assert exc_info.value.context == context
        
        # Verify record was stored
        records = manager.get_logs()
        assert len(records) == 1
        assert records[0].level == "ERROR"
        assert records[0].category == "DATA"
        assert records[0].message == "Critical data error"
    
    def test_log_error_raises_even_if_below_min_level(self):
        """Test that log_error always raises, even if below min_level"""
        manager = DebugManager(min_level="ERROR")
        
        context = {"module": "TestModule"}
        
        # Log at levels below ERROR - should not be stored
        manager.log_debug("TEST", "Debug", context)
        manager.log_info("TEST", "Info", context)
        manager.log_warning("TEST", "Warning", context)
        
        # ERROR should be stored and raise
        with pytest.raises(DebugError):
            manager.log_error("TEST", "Error", context)
        
        # Only ERROR should be stored
        records = manager.get_logs()
        assert len(records) == 1
        assert records[0].level == "ERROR"


class TestGetLogsFiltering:
    """Test log filtering via get_logs"""
    
    def test_get_logs_filters_by_level(self):
        """Test filtering by level"""
        manager = DebugManager()
        
        context = {"module": "TestModule"}
        
        manager.log_debug("TEST", "Debug", context)
        manager.log_info("TEST", "Info", context)
        manager.log_warning("TEST", "Warning", context)
        
        debug_logs = manager.get_logs(level="DEBUG")
        assert len(debug_logs) == 1
        assert debug_logs[0].level == "DEBUG"
        
        warning_logs = manager.get_logs(level="WARNING")
        assert len(warning_logs) == 1
        assert warning_logs[0].level == "WARNING"
    
    def test_get_logs_filters_by_category(self):
        """Test filtering by category"""
        manager = DebugManager()
        
        context = {"module": "TestModule"}
        
        manager.log_info("DATA", "Data message", context)
        manager.log_info("ENTRY", "Entry message", context)
        manager.log_info("EXIT", "Exit message", context)
        
        data_logs = manager.get_logs(category="DATA")
        assert len(data_logs) == 1
        assert data_logs[0].category == "DATA"
        
        entry_logs = manager.get_logs(category="ENTRY")
        assert len(entry_logs) == 1
        assert entry_logs[0].category == "ENTRY"
    
    def test_get_logs_filters_by_module(self):
        """Test filtering by module via context"""
        manager = DebugManager()
        
        manager.log_info("TEST", "Message 1", {"module": "DataManager", "instrument": "ES"})
        manager.log_info("TEST", "Message 2", {"module": "EntryDetector", "instrument": "ES"})
        manager.log_info("TEST", "Message 3", {"module": "DataManager", "instrument": "GC"})
        
        data_manager_logs = manager.get_logs(module="DataManager")
        assert len(data_manager_logs) == 2
        assert all(r.context["module"] == "DataManager" for r in data_manager_logs)
        
        entry_detector_logs = manager.get_logs(module="EntryDetector")
        assert len(entry_detector_logs) == 1
        assert entry_detector_logs[0].context["module"] == "EntryDetector"
    
    def test_get_logs_filters_by_instrument(self):
        """Test filtering by instrument via context"""
        manager = DebugManager()
        
        manager.log_info("TEST", "Message 1", {"module": "DataManager", "instrument": "ES"})
        manager.log_info("TEST", "Message 2", {"module": "EntryDetector", "instrument": "ES"})
        manager.log_info("TEST", "Message 3", {"module": "DataManager", "instrument": "GC"})
        
        es_logs = manager.get_logs(instrument="ES")
        assert len(es_logs) == 2
        assert all(r.context.get("instrument") == "ES" for r in es_logs)
        
        gc_logs = manager.get_logs(instrument="GC")
        assert len(gc_logs) == 1
        assert gc_logs[0].context.get("instrument") == "GC"
    
    def test_get_logs_combines_filters(self):
        """Test that multiple filters can be combined"""
        manager = DebugManager()
        
        manager.log_info("DATA", "Message 1", {"module": "DataManager", "instrument": "ES"})
        manager.log_warning("DATA", "Message 2", {"module": "DataManager", "instrument": "ES"})
        manager.log_info("ENTRY", "Message 3", {"module": "DataManager", "instrument": "GC"})
        manager.log_info("DATA", "Message 4", {"module": "EntryDetector", "instrument": "ES"})
        
        # Filter by level, category, module, and instrument
        filtered = manager.get_logs(level="INFO", category="DATA", module="DataManager", instrument="ES")
        assert len(filtered) == 1
        assert filtered[0].level == "INFO"
        assert filtered[0].category == "DATA"
        assert filtered[0].context["module"] == "DataManager"
        assert filtered[0].context["instrument"] == "ES"


class TestClearLogs:
    """Test log clearing"""
    
    def test_clear_logs_removes_all_records(self):
        """Test that clear_logs removes all records"""
        manager = DebugManager()
        
        context = {"module": "TestModule"}
        
        manager.log_debug("TEST", "Debug", context)
        manager.log_info("TEST", "Info", context)
        manager.log_warning("TEST", "Warning", context)
        
        assert len(manager.get_logs()) == 3
        
        manager.clear_logs()
        
        assert len(manager.get_logs()) == 0


class TestSetMinLevel:
    """Test minimum level configuration"""
    
    def test_set_min_level_invalid_value_raises(self):
        """Test that invalid min_level raises ValueError"""
        manager = DebugManager()
        
        with pytest.raises(ValueError, match="Invalid log level"):
            manager.set_min_level("FOO")
        
        with pytest.raises(ValueError, match="Invalid log level"):
            manager.set_min_level("")
    
    def test_set_min_level_updates_filtering(self):
        """Test that set_min_level updates filtering behavior"""
        manager = DebugManager(min_level="DEBUG")
        
        context = {"module": "TestModule"}
        
        # Log before changing min_level
        manager.log_debug("TEST", "Debug", context)
        manager.log_info("TEST", "Info", context)
        
        # Change min_level to WARNING
        manager.set_min_level("WARNING")
        
        # Log after changing min_level
        manager.log_warning("TEST", "Warning", context)
        
        # log_error always raises, so catch the exception
        with pytest.raises(DebugError):
            manager.log_error("TEST", "Error", context)
        
        # Should have all 4 records (DEBUG and INFO were logged before min_level change)
        records = manager.get_logs()
        assert len(records) == 4
        
        # Clear and test new filtering
        manager.clear_logs()
        manager.log_debug("TEST", "Debug", context)
        manager.log_info("TEST", "Info", context)
        manager.log_warning("TEST", "Warning", context)
        
        # Now only WARNING should be stored (DEBUG and INFO filtered out)
        records = manager.get_logs()
        assert len(records) == 1
        assert records[0].level == "WARNING"


class TestDeterministicBehavior:
    """Test deterministic behavior"""
    
    def test_deterministic_behavior(self):
        """Test that same sequence produces identical logs"""
        manager1 = DebugManager()
        manager2 = DebugManager()
        
        context = {"module": "TestModule", "instrument": "ES"}
        
        # Same sequence of operations
        for manager in [manager1, manager2]:
            manager.log_debug("DATA", "Debug message", context)
            manager.log_info("ENTRY", "Info message", context)
            manager.log_warning("EXIT", "Warning message", context)
        
        logs1 = manager1.get_logs()
        logs2 = manager2.get_logs()
        
        assert len(logs1) == len(logs2) == 3
        
        # Verify levels and categories match
        levels1 = [r.level for r in logs1]
        levels2 = [r.level for r in logs2]
        assert levels1 == levels2
        
        categories1 = [r.category for r in logs1]
        categories2 = [r.category for r in logs2]
        assert categories1 == categories2
        
        messages1 = [r.message for r in logs1]
        messages2 = [r.message for r in logs2]
        assert messages1 == messages2


class TestIntegrationExamples:
    """Integration-style tests mimicking real engine usage"""
    
    def test_datamanager_duplicate_timestamp_warning(self):
        """Simulate DataManager logging duplicate timestamp warning"""
        manager = DebugManager()
        
        context = {
            "module": "DataManager",
            "instrument": "ES",
            "removed_count": 3,
            "timestamp": "2025-01-02 07:30:00"
        }
        
        manager.log_warning("DATA", "Duplicate timestamps removed", context)
        
        records = manager.get_logs(module="DataManager", category="DATA")
        assert len(records) == 1
        assert records[0].context["removed_count"] == 3
        assert records[0].context["instrument"] == "ES"
    
    def test_entrydetector_no_post_range_data_warning(self):
        """Simulate EntryDetector logging no post-range data warning"""
        manager = DebugManager()
        
        from datetime import datetime
        import pytz
        
        end_ts = pytz.timezone("America/Chicago").localize(datetime(2025, 1, 2, 7, 30))
        
        context = {
            "module": "EntryDetector",
            "instrument": "GC",
            "end_ts": end_ts,
            "range_size": 2.5,
            "session": "S1"
        }
        
        manager.log_warning("ENTRY", "No post-range data available", context)
        
        records = manager.get_logs(module="EntryDetector", category="ENTRY")
        assert len(records) == 1
        assert records[0].context["instrument"] == "GC"
        assert records[0].context["end_ts"] == end_ts
    
    def test_pricetracker_inconsistent_stop_target_error(self):
        """Simulate PriceTracker logging error about inconsistent stop/target"""
        manager = DebugManager()
        
        context = {
            "module": "PriceTracker",
            "instrument": "CL",
            "entry_price": 65.50,
            "stop_loss": 65.00,
            "target": 66.00,
            "direction": "Long",
            "trade_id": "CL_20250102_0730"
        }
        
        with pytest.raises(DebugError) as exc_info:
            manager.log_error("EXECUTION", "Inconsistent stop loss and target levels", context)
        
        # Verify exception
        assert exc_info.value.category == "EXECUTION"
        assert exc_info.value.context["instrument"] == "CL"
        assert exc_info.value.context["trade_id"] == "CL_20250102_0730"
        
        # Verify record was stored
        records = manager.get_logs(module="PriceTracker", level="ERROR")
        assert len(records) == 1
        assert records[0].context["entry_price"] == 65.50
        assert records[0].context["stop_loss"] == 65.00
    
    def test_multiple_modules_logging(self):
        """Test multiple modules logging simultaneously"""
        manager = DebugManager()
        
        # DataManager logs
        manager.log_info("DATA", "Data loaded", {"module": "DataManager", "instrument": "ES", "row_count": 1000})
        manager.log_warning("DATA", "Missing bars detected", {"module": "DataManager", "instrument": "ES", "missing_count": 5})
        
        # EntryDetector logs
        manager.log_info("ENTRY", "Entry detected", {"module": "EntryDetector", "instrument": "ES", "direction": "Long"})
        manager.log_warning("ENTRY", "No breakout found", {"module": "EntryDetector", "instrument": "ES", "time_slot": "07:30"})
        
        # PriceTracker logs
        manager.log_info("EXECUTION", "Trade executed", {"module": "PriceTracker", "instrument": "ES", "result": "Win"})
        
        # Verify filtering works across modules
        data_logs = manager.get_logs(module="DataManager")
        assert len(data_logs) == 2
        
        entry_logs = manager.get_logs(module="EntryDetector")
        assert len(entry_logs) == 2
        
        execution_logs = manager.get_logs(module="PriceTracker")
        assert len(execution_logs) == 1
        
        # Verify all ES instrument logs
        es_logs = manager.get_logs(instrument="ES")
        assert len(es_logs) == 5  # All logs are for ES


class TestEdgeCases:
    """Test edge cases and boundary conditions"""
    
    def test_empty_context_raises(self):
        """Test that empty context dict raises"""
        manager = DebugManager()
        
        with pytest.raises(ValueError, match="context must contain 'module' key"):
            manager.log_info("TEST", "Message", {})
    
    def test_context_with_extra_keys(self):
        """Test that context can have extra keys beyond 'module'"""
        manager = DebugManager()
        
        context = {
            "module": "TestModule",
            "extra_key_1": "value1",
            "extra_key_2": 42,
            "nested": {"key": "value"}
        }
        
        manager.log_info("TEST", "Message", context)
        
        records = manager.get_logs()
        assert len(records) == 1
        assert records[0].context == context
        assert records[0].context["extra_key_1"] == "value1"
        assert records[0].context["nested"]["key"] == "value"
    
    def test_get_logs_with_no_matches(self):
        """Test get_logs returns empty list when no matches"""
        manager = DebugManager()
        
        context = {"module": "TestModule", "instrument": "ES"}
        manager.log_info("DATA", "Message", context)
        
        # Filter for non-existent module
        logs = manager.get_logs(module="NonExistent")
        assert len(logs) == 0
        
        # Filter for non-existent instrument
        logs = manager.get_logs(instrument="GC")
        assert len(logs) == 0
        
        # Filter for non-existent category
        logs = manager.get_logs(category="ENTRY")
        assert len(logs) == 0
    
    def test_context_is_copied_not_referenced(self):
        """Test that context dict is copied, not referenced"""
        manager = DebugManager()
        
        context = {"module": "TestModule", "value": 1}
        manager.log_info("TEST", "Message", context)
        
        # Modify original context
        context["value"] = 2
        
        # Stored context should be unchanged
        records = manager.get_logs()
        assert records[0].context["value"] == 1

