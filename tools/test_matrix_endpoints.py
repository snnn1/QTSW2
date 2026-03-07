#!/usr/bin/env python3
"""Quick test of new matrix API endpoints: performance, files, stream-health, diff."""

import requests
import sys

API = "http://localhost:8000/api/matrix"


def test_performance():
    print("=== Testing GET /performance ===")
    r = requests.get(f"{API}/performance", timeout=5)
    print(f"Status: {r.status_code}")
    if r.ok:
        d = r.json()
        print(f"  matrix_size: {d.get('matrix_size')}")
        print(f"  api_cache_hit_rate: {d.get('api_cache_hit_rate')}%")
        print(f"  api_cache_hits: {d.get('api_cache_hits')}, misses: {d.get('api_cache_misses')}")
        return True
    print(f"  Error: {r.text[:200]}")
    return False


def test_files():
    print("\n=== Testing GET /files ===")
    r = requests.get(f"{API}/files", timeout=5)
    print(f"Status: {r.status_code}")
    if r.ok:
        d = r.json()
        files = d.get("files", [])
        print(f"  Files count: {len(files)}")
        if files:
            print(f"  First file: {files[0].get('name')}")
        return True
    print(f"  Error: {r.text[:200]}")
    return False


def test_stream_health():
    print("\n=== Testing GET /stream-health ===")
    r = requests.get(f"{API}/stream-health", timeout=15)
    print(f"Status: {r.status_code}")
    if r.ok:
        d = r.json()
        streams = d.get("streams", [])
        print(f"  Streams count: {len(streams)}")
        if streams:
            s = streams[0]
            print(f"  Sample: {s.get('stream_id')} win_rate={s.get('win_rate')}")
        return True
    print(f"  Error: {r.text[:200]}")
    return False


def test_diff():
    print("\n=== Testing POST /diff ===")
    r = requests.get(f"{API}/files", timeout=5)
    if not r.ok or len(r.json().get("files", [])) < 2:
        print("  Skipped (need 2+ matrix files)")
        return True
    files = r.json()["files"]
    fa, fb = files[0]["name"], files[1]["name"]
    r2 = requests.post(f"{API}/diff", json={"file_a": fa, "file_b": fb}, timeout=15)
    print(f"Status: {r2.status_code}")
    if r2.ok:
        d = r2.json()
        print(f"  total_differences: {d.get('total_differences', 0)}")
        return True
    print(f"  Error: {r2.text[:300]}")
    return False


def main():
    try:
        ok = True
        ok &= test_performance()
        ok &= test_files()
        ok &= test_stream_health()
        ok &= test_diff()
        print("\n=== All endpoint tests completed ===")
        sys.exit(0 if ok else 1)
    except requests.exceptions.ConnectionError:
        print("ERROR: Cannot connect to backend. Is it running on http://localhost:8000?")
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
