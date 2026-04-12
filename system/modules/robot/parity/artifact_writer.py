"""
Artifact Writer

Writes parity audit artifacts to disk.
"""

from pathlib import Path
from typing import Dict, Any
import pandas as pd
import json


class ArtifactWriter:
    """Writes parity audit artifacts."""
    
    def __init__(self, output_dir: Path):
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)
    
    def write_all(
        self,
        analyzer_df: pd.DataFrame,
        robot_df: pd.DataFrame,
        comparison_result: Dict[str, Any],
        parity_config: Dict[str, Any]
    ):
        """Write all artifacts."""
        # Write canonical extracts
        self._write_dataframe(analyzer_df, "analyzer_extract.parquet")
        self._write_dataframe(robot_df, "robot_extract.parquet")
        
        # Write mismatches
        mismatches_df = comparison_result.get("mismatches", pd.DataFrame())
        if not mismatches_df.empty:
            self._write_dataframe(mismatches_df, "parity_mismatches.parquet")
        
        # Write warnings
        warnings_df = comparison_result.get("warnings", pd.DataFrame())
        if not warnings_df.empty:
            self._write_dataframe(warnings_df, "parity_warnings.parquet")
        
        # Write summary markdown
        self._write_summary_markdown(comparison_result, parity_config)
    
    def _write_dataframe(self, df: pd.DataFrame, filename: str):
        """Write DataFrame to parquet and CSV."""
        if df.empty:
            return
        
        parquet_path = self.output_dir / filename
        csv_path = self.output_dir / filename.replace(".parquet", ".csv")
        
        df.to_parquet(parquet_path, index=False)
        df.to_csv(csv_path, index=False)
    
    def _write_summary_markdown(
        self,
        comparison_result: Dict[str, Any],
        parity_config: Dict[str, Any]
    ):
        """Write human-readable summary markdown."""
        summary_path = self.output_dir / "parity_summary.md"
        
        with open(summary_path, "w", encoding="utf-8") as f:
            f.write("# Parity Audit Summary\n\n")
            f.write(f"**Date Window:** {parity_config['start_date']} to {parity_config['end_date']}\n\n")
            f.write(f"**Instruments:** {parity_config.get('instruments', 'all')}\n\n")
            f.write("---\n\n")
            
            # Overall status
            fails = comparison_result["fails"]
            passes = comparison_result["passes"]
            warnings = comparison_result["warnings"]
            total = comparison_result["total_compared"]
            
            if fails == 0:
                f.write("## ✓ PARITY CHECK PASSED\n\n")
            else:
                f.write("## ✗ PARITY CHECK FAILED\n\n")
            
            # Statistics
            f.write("### Statistics\n\n")
            f.write(f"- **Total Compared:** {total}\n")
            f.write(f"- **Passes:** {passes}\n")
            f.write(f"- **Failures:** {fails}\n")
            f.write(f"- **Warnings:** {warnings}\n\n")
            
            # Missing records
            analyzer_only = comparison_result.get("analyzer_only_count", 0)
            robot_only = comparison_result.get("robot_only_count", 0)
            
            if analyzer_only > 0 or robot_only > 0:
                f.write("### Missing Records\n\n")
                if analyzer_only > 0:
                    f.write(f"- **Analyzer-only records:** {analyzer_only} (present in Analyzer but not Robot)\n")
                if robot_only > 0:
                    f.write(f"- **Robot-only records:** {robot_only} (present in Robot but not Analyzer)\n")
                f.write("\n")
            
            # Failure breakdown
            fail_by_field = comparison_result.get("fail_by_field", {})
            if fail_by_field:
                f.write("### Failure Breakdown by Field\n\n")
                for field, count in sorted(fail_by_field.items(), key=lambda x: -x[1]):
                    f.write(f"- **{field}:** {count} mismatch(es)\n")
                f.write("\n")
            
            # Warnings breakdown
            warn_by_type = comparison_result.get("warn_by_type", {})
            if warn_by_type:
                f.write("### Warnings Breakdown\n\n")
                for warn_type, count in sorted(warn_by_type.items(), key=lambda x: -x[1]):
                    f.write(f"- **{warn_type}:** {count} warning(s)\n")
                f.write("\n")
            
            # Artifacts
            f.write("### Artifacts\n\n")
            f.write("The following files are available in this directory:\n\n")
            f.write("- `analyzer_extract.parquet` / `.csv` - Analyzer canonical records\n")
            f.write("- `robot_extract.parquet` / `.csv` - Robot canonical records\n")
            
            if fails > 0:
                f.write("- `parity_mismatches.parquet` / `.csv` - Detailed mismatch records\n")
            
            if warnings > 0:
                f.write("- `parity_warnings.parquet` / `.csv` - Warning records\n")
            
            f.write("- `parity_config.json` - Audit configuration\n")
            f.write("\n")
            
            # Next steps
            if fails > 0:
                f.write("### Next Steps\n\n")
                f.write("1. Review `parity_mismatches.parquet` for detailed mismatch information\n")
                f.write("2. Identify root causes of mismatches\n")
                f.write("3. Fix issues in Analyzer or Robot as appropriate\n")
                f.write("4. Re-run parity audit to verify fixes\n")
