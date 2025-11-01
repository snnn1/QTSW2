"""
Convert text file to CSV format
Handles various text formats and converts to CSV compatible with translator
"""

import sys
from pathlib import Path
import pandas as pd
import re

def convert_text_to_csv(input_file: Path, output_file: Path = None):
    """
    Convert text file to CSV format
    
    Args:
        input_file: Path to input text file
        output_file: Optional output CSV path (default: input_file with .csv extension)
    """
    if not input_file.exists():
        print(f"Error: File not found: {input_file}")
        return
    
    # Read the text file
    with open(input_file, 'r', encoding='utf-8') as f:
        content = f.read()
    
    if not content.strip():
        print(f"Error: File is empty: {input_file}")
        return
    
    print(f"Reading file: {input_file}")
    print(f"File size: {len(content)} characters")
    print(f"First 500 characters:")
    print(content[:500])
    print("-" * 60)
    
    # Try different parsing methods
    lines = content.strip().split('\n')
    
    # Method 1: Tab-separated
    if '\t' in content:
        print("Detected: Tab-separated format")
        df = pd.read_csv(input_file, sep='\t')
    
    # Method 2: Comma-separated (might be text with commas)
    elif ',' in content and len(lines) > 1:
        print("Detected: Comma-separated format")
        df = pd.read_csv(input_file, sep=',')
    
    # Method 3: Space-separated (multiple spaces)
    elif re.search(r'\s{2,}', content):
        print("Detected: Space-separated format")
        df = pd.read_csv(input_file, sep=r'\s+', engine='python')
    
    # Method 4: Pipe-separated
    elif '|' in content:
        print("Detected: Pipe-separated format")
        df = pd.read_csv(input_file, sep='|')
    
    # Method 5: Try to parse as NinjaTrader format or other
    else:
        print("Trying: Generic text parsing")
        # Try to detect headers
        if lines[0].strip().lower().startswith(('date', 'time', 'open', 'high')):
            # Has header
            header_line = lines[0]
            data_lines = lines[1:]
            
            # Detect separator
            if '\t' in header_line:
                sep = '\t'
            elif ',' in header_line:
                sep = ','
            else:
                sep = r'\s+'
            
            df = pd.read_csv(input_file, sep=sep, skiprows=0)
        else:
            # No header, try to parse manually
            print("Attempting to parse without clear header...")
            df = pd.read_csv(input_file, header=None)
    
    print(f"\nParsed DataFrame:")
    print(f"  Shape: {df.shape}")
    print(f"  Columns: {df.columns.tolist()}")
    print(f"\nFirst 5 rows:")
    print(df.head())
    
    # Determine output file
    if output_file is None:
        output_file = input_file.with_suffix('.csv')
    
    # Save as CSV
    df.to_csv(output_file, index=False)
    print(f"\n✓ Converted to CSV: {output_file}")
    print(f"  Output size: {output_file.stat().st_size / 1024:.2f} KB")
    
    return df


if __name__ == "__main__":
    import argparse
    
    parser = argparse.ArgumentParser(description="Convert text file to CSV")
    parser.add_argument("input_file", type=str, help="Input text file path")
    parser.add_argument("-o", "--output", type=str, help="Output CSV file path (optional)")
    
    args = parser.parse_args()
    
    input_path = Path(args.input_file)
    output_path = Path(args.output) if args.output else None
    
    convert_text_to_csv(input_path, output_path)




