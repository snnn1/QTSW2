"""
Complexity Analysis Report Generator
Analyzes codebase complexity metrics
"""

import ast
import sys
from pathlib import Path
from collections import defaultdict
from typing import Dict, List, Tuple

class ComplexityAnalyzer(ast.NodeVisitor):
    def __init__(self):
        self.complexity = 1  # Base complexity
        self.max_depth = 0
        self.current_depth = 0
        
    def visit_If(self, node):
        self.complexity += 1
        self.current_depth += 1
        self.max_depth = max(self.max_depth, self.current_depth)
        self.generic_visit(node)
        self.current_depth -= 1
        
    def visit_For(self, node):
        self.complexity += 1
        self.current_depth += 1
        self.max_depth = max(self.max_depth, self.current_depth)
        self.generic_visit(node)
        self.current_depth -= 1
        
    def visit_While(self, node):
        self.complexity += 1
        self.current_depth += 1
        self.max_depth = max(self.max_depth, self.current_depth)
        self.generic_visit(node)
        self.current_depth -= 1
        
    def visit_Try(self, node):
        self.complexity += 1
        self.current_depth += 1
        self.max_depth = max(self.max_depth, self.current_depth)
        self.generic_visit(node)
        self.current_depth -= 1
        
    def visit_With(self, node):
        self.complexity += 1
        self.generic_visit(node)
        
    def visit_ExceptHandler(self, node):
        self.complexity += 1
        self.generic_visit(node)

def analyze_file(file_path: Path) -> Dict:
    """Analyze a single Python file for complexity."""
    try:
        with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
            content = f.read()
            lines = content.splitlines()
            
        tree = ast.parse(content)
        
        functions = []
        classes = []
        
        for node in ast.walk(tree):
            if isinstance(node, ast.FunctionDef):
                analyzer = ComplexityAnalyzer()
                analyzer.visit(node)
                functions.append({
                    'name': node.name,
                    'line': node.lineno,
                    'complexity': analyzer.complexity,
                    'max_depth': analyzer.max_depth,
                    'lines': node.end_lineno - node.lineno if hasattr(node, 'end_lineno') else 0
                })
            elif isinstance(node, ast.ClassDef):
                analyzer = ComplexityAnalyzer()
                analyzer.visit(node)
                classes.append({
                    'name': node.name,
                    'line': node.lineno,
                    'complexity': analyzer.complexity,
                    'max_depth': analyzer.max_depth,
                    'lines': node.end_lineno - node.lineno if hasattr(node, 'end_lineno') else 0
                })
        
        return {
            'file': str(file_path),
            'total_lines': len(lines),
            'functions': functions,
            'classes': classes,
            'total_functions': len(functions),
            'total_classes': len(classes)
        }
    except Exception as e:
        return {
            'file': str(file_path),
            'error': str(e)
        }

def main():
    """Generate complexity report."""
    matrix_dir = Path('modules/matrix')
    
    if not matrix_dir.exists():
        print(f"Error: {matrix_dir} does not exist")
        return
    
    print("=" * 80)
    print("COMPLEXITY ANALYSIS REPORT")
    print("=" * 80)
    print()
    
    files = list(matrix_dir.rglob('*.py'))
    print(f"Analyzing {len(files)} Python files in modules/matrix/")
    print()
    
    all_results = []
    function_complexity = []
    class_complexity = []
    
    for file_path in sorted(files):
        if '__pycache__' in str(file_path):
            continue
            
        result = analyze_file(file_path)
        if 'error' in result:
            print(f"ERROR analyzing {file_path.name}: {result['error']}")
            continue
            
        all_results.append(result)
        
        # Collect complex functions
        for func in result['functions']:
            function_complexity.append({
                'file': Path(result['file']).name,
                'function': func['name'],
                'complexity': func['complexity'],
                'lines': func['lines'],
                'max_depth': func['max_depth']
            })
        
        # Collect complex classes
        for cls in result['classes']:
            class_complexity.append({
                'file': Path(result['file']).name,
                'class': cls['name'],
                'complexity': cls['complexity'],
                'lines': cls['lines'],
                'max_depth': cls['max_depth']
            })
    
    # Summary statistics
    print("=" * 80)
    print("SUMMARY STATISTICS")
    print("=" * 80)
    
    total_lines = sum(r['total_lines'] for r in all_results)
    total_functions = sum(r['total_functions'] for r in all_results)
    total_classes = sum(r['total_classes'] for r in all_results)
    
    print(f"Total Lines of Code: {total_lines:,}")
    print(f"Total Functions: {total_functions}")
    print(f"Total Classes: {total_classes}")
    print()
    
    # File size breakdown
    print("=" * 80)
    print("FILE SIZE BREAKDOWN")
    print("=" * 80)
    file_sizes = sorted(all_results, key=lambda x: x['total_lines'], reverse=True)
    for result in file_sizes[:15]:
        print(f"{Path(result['file']).name:40} {result['total_lines']:6} lines  "
              f"{result['total_functions']:3} functions  {result['total_classes']:2} classes")
    print()
    
    # Most complex functions
    print("=" * 80)
    print("MOST COMPLEX FUNCTIONS (Top 20)")
    print("=" * 80)
    print(f"{'File':30} {'Function':30} {'Complexity':>10} {'Lines':>8} {'Max Depth':>10}")
    print("-" * 80)
    
    complex_functions = sorted(function_complexity, key=lambda x: x['complexity'], reverse=True)
    for func in complex_functions[:20]:
        print(f"{func['file']:30} {func['function']:30} {func['complexity']:10} "
              f"{func['lines']:8} {func['max_depth']:10}")
    print()
    
    # Most complex classes
    print("=" * 80)
    print("MOST COMPLEX CLASSES (Top 10)")
    print("=" * 80)
    print(f"{'File':30} {'Class':30} {'Complexity':>10} {'Lines':>8} {'Max Depth':>10}")
    print("-" * 80)
    
    complex_classes = sorted(class_complexity, key=lambda x: x['complexity'], reverse=True)
    for cls in complex_classes[:10]:
        print(f"{cls['file']:30} {cls['class']:30} {cls['complexity']:10} "
              f"{cls['lines']:8} {cls['max_depth']:10}")
    print()
    
    # Complexity warnings
    print("=" * 80)
    print("COMPLEXITY WARNINGS")
    print("=" * 80)
    print("Functions with complexity > 15 (HIGH RISK):")
    high_complexity = [f for f in complex_functions if f['complexity'] > 15]
    if high_complexity:
        for func in high_complexity:
            print(f"  [WARNING] {func['file']}::{func['function']} - Complexity: {func['complexity']}")
    else:
        print("  [OK] No functions exceed complexity 15")
    print()
    
    print("Functions with complexity > 10 (MODERATE RISK):")
    moderate_complexity = [f for f in complex_functions if 10 < f['complexity'] <= 15]
    if moderate_complexity:
        for func in moderate_complexity[:10]:
            print(f"  [WARNING] {func['file']}::{func['function']} - Complexity: {func['complexity']}")
    else:
        print("  [OK] No functions exceed complexity 10")
    print()
    
    print("Very long functions (> 200 lines):")
    long_functions = [f for f in complex_functions if f['lines'] > 200]
    if long_functions:
        for func in long_functions:
            print(f"  [WARNING] {func['file']}::{func['function']} - {func['lines']} lines")
    else:
        print("  [OK] No functions exceed 200 lines")
    print()
    
    print("=" * 80)
    print("RECOMMENDATIONS")
    print("=" * 80)
    print("1. Functions with complexity > 15 should be refactored")
    print("2. Functions > 200 lines should be split into smaller functions")
    print("3. Deep nesting (max_depth > 5) indicates need for early returns")
    print("4. Consider extracting complex logic into separate modules")
    print()

if __name__ == '__main__':
    main()
