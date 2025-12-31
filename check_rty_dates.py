#!/usr/bin/env python3
"""Check RTY file date ranges"""
import pathlib

# Check RTY files
rty_files = sorted(pathlib.Path('data/translated/RTY').rglob('*.parquet'))
rty_dates = []
for f in rty_files:
    parts = f.stem.split('_')
    if len(parts) >= 3:
        rty_dates.append(parts[-1])

rty_dates.sort()
rty_years = set(d[:4] for d in rty_dates if len(d) >= 4)

print(f'RTY: {len(rty_files)} files')
print(f'  Date range: {rty_dates[0] if rty_dates else "none"} to {rty_dates[-1] if rty_dates else "none"}')
print(f'  Years: {len(rty_years)} years ({min(rty_years) if rty_years else "none"} to {max(rty_years) if rty_years else "none"})')
print('')

# Check other instruments
for inst in ['CL', 'ES', 'GC', 'NG', 'NQ', 'YM']:
    files = sorted(pathlib.Path(f'data/translated/{inst}').rglob('*.parquet'))
    dates = []
    for f in files:
        parts = f.stem.split('_')
        if len(parts) >= 3:
            dates.append(parts[-1])
    dates.sort()
    years = set(d[:4] for d in dates if len(d) >= 4)
    print(f'{inst}: {len(files)} files')
    if dates:
        print(f'  Date range: {dates[0]} to {dates[-1]}')
        print(f'  Years: {len(years)} years ({min(years)} to {max(years)})')
    else:
        print(f'  No files found')
    print('')
