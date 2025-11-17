#!/usr/bin/env python

# Analyze cases where both T1 and T2 triggered and we can't tell which came first
print('=== AMBIGUOUS T1/T2 TRIGGER ANALYSIS ===')
print()

# From the debug output, look for cases where both T1 and T2 triggered
# and the favorable movement is very close to both thresholds

cases = [
    # Case 1: 2025-09-15 Level 1 - Both T1 and T2 triggered
    {'date': '2025-09-15', 'level': 1, 'target': 10, 't1_threshold': 6.50, 't2_threshold': 9.50, 'actual_favorable': 9.50, 'peak': 29.50},
    
    # Case 2: 2025-09-15 Level 2 - Both T1 and T2 triggered  
    {'date': '2025-09-15', 'level': 2, 'target': 15, 't1_threshold': 7.50, 't2_threshold': 13.50, 'actual_favorable': 13.75, 'peak': 29.50},
    
    # Case 3: 2025-09-16 Level 1 - Both T1 and T2 triggered
    {'date': '2025-09-16', 'level': 1, 'target': 10, 't1_threshold': 6.50, 't2_threshold': 9.50, 'actual_favorable': 9.75, 'peak': 28.50},
    
    # Case 4: 2025-09-16 Level 2 - Both T1 and T2 triggered
    {'date': '2025-09-16', 'level': 2, 'target': 15, 't1_threshold': 7.50, 't2_threshold': 13.50, 'actual_favorable': 14.00, 'peak': 28.50},
    
    # Case 5: 2025-09-16 Level 3 - Both T1 and T2 triggered
    {'date': '2025-09-16', 'level': 3, 'target': 20, 't1_threshold': 8.00, 't2_threshold': 18.00, 'actual_favorable': 18.00, 'peak': 28.50},
    
    # Case 6: 2025-09-19 Level 1 - Both T1 and T2 triggered
    {'date': '2025-09-19', 'level': 1, 'target': 10, 't1_threshold': 6.50, 't2_threshold': 9.50, 'actual_favorable': 9.75, 'peak': 28.50},
    
    # Case 7: 2025-09-19 Level 2 - Both T1 and T2 triggered
    {'date': '2025-09-19', 'level': 2, 'target': 15, 't1_threshold': 7.50, 't2_threshold': 13.50, 'actual_favorable': 13.75, 'peak': 28.50},
    
    # Case 8: 2025-09-19 Level 3 - Both T1 and T2 triggered
    {'date': '2025-09-19', 'level': 3, 'target': 20, 't1_threshold': 8.00, 't2_threshold': 18.00, 'actual_favorable': 18.00, 'peak': 28.50},
    
    # Case 9: 2025-09-19 Level 4 - Both T1 and T2 triggered
    {'date': '2025-09-19', 'level': 4, 'target': 25, 't1_threshold': 8.75, 't2_threshold': 22.50, 'actual_favorable': 22.50, 'peak': 28.50},
]

ambiguous_cases = []
clear_cases = []

for case in cases:
    t1_threshold = case['t1_threshold']
    t2_threshold = case['t2_threshold']
    actual_favorable = case['actual_favorable']
    
    # Calculate distances from each threshold
    dist_from_t1 = abs(actual_favorable - t1_threshold)
    dist_from_t2 = abs(actual_favorable - t2_threshold)
    
    # If the favorable movement is very close to both thresholds, it's ambiguous
    # We can't tell which threshold was hit first
    if dist_from_t1 <= 0.5 and dist_from_t2 <= 0.5:
        ambiguous_cases.append(case)
        case['ambiguity_reason'] = f'Close to both: T1±{dist_from_t1:.2f}, T2±{dist_from_t2:.2f}'
    else:
        clear_cases.append(case)

print(f'Total T2 cases analyzed: {len(cases)}')
print(f'Ambiguous cases (cannot tell T1 vs T2 order): {len(ambiguous_cases)}')
print(f'Clear cases (can determine order): {len(clear_cases)}')
print()

if ambiguous_cases:
    print('=== AMBIGUOUS CASES ===')
    for i, case in enumerate(ambiguous_cases, 1):
        print(f'{i}. {case["date"]} Level {case["level"]} ({case["target"]}-point target)')
        print(f'   T1 threshold: {case["t1_threshold"]:.2f}, T2 threshold: {case["t2_threshold"]:.2f}')
        print(f'   Actual favorable: {case["actual_favorable"]:.2f}')
        print(f'   Reason: {case["ambiguity_reason"]}')
        print()

print(f'Ambiguity rate: {len(ambiguous_cases)/len(cases)*100:.1f}%')

# Additional analysis: Look for cases where the price jumped over both thresholds
print()
print('=== ADDITIONAL ANALYSIS: THRESHOLD JUMPING ===')
print()

jump_cases = []
for case in cases:
    t1_threshold = case['t1_threshold']
    t2_threshold = case['t2_threshold']
    actual_favorable = case['actual_favorable']
    
    # If the favorable movement is significantly above both thresholds,
    # it likely jumped over both in a single bar
    if actual_favorable > t2_threshold + 0.5:
        jump_cases.append(case)
        case['jump_amount'] = actual_favorable - t2_threshold

print(f'Cases where price jumped significantly over T2: {len(jump_cases)}')
for i, case in enumerate(jump_cases, 1):
    print(f'{i}. {case["date"]} Level {case["level"]}: Jumped {case["jump_amount"]:.2f} points over T2 threshold')


