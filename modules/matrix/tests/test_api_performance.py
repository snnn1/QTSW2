"""Tests for new matrix API endpoints: performance, stream-health, diff."""

import sys
from pathlib import Path

import pytest

# Add project root
QTSW2_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))


@pytest.fixture
def client():
    """Create FastAPI test client."""
    from fastapi.testclient import TestClient
    from modules.dashboard.backend.main import app
    return TestClient(app)


def test_performance_endpoint(client):
    """GET /api/matrix/performance returns 200 and expected keys."""
    r = client.get("/api/matrix/performance")
    assert r.status_code == 200
    d = r.json()
    assert "matrix_size" in d
    assert "api_cache_hit_rate" in d
    assert "api_cache_hits" in d
    assert "api_cache_misses" in d


def test_files_endpoint(client):
    """GET /api/matrix/files returns 200 and files list."""
    r = client.get("/api/matrix/files")
    assert r.status_code == 200
    d = r.json()
    assert "files" in d
    assert isinstance(d["files"], list)


def test_stream_health_endpoint(client):
    """GET /api/matrix/stream-health returns 200 and streams list."""
    r = client.get("/api/matrix/stream-health")
    assert r.status_code == 200
    d = r.json()
    assert "streams" in d
    assert isinstance(d["streams"], list)


def test_diff_endpoint_no_files(client):
    """POST /api/matrix/diff with non-existent files returns 404."""
    r = client.post(
        "/api/matrix/diff",
        json={"file_a": "nonexistent_a.parquet", "file_b": "nonexistent_b.parquet"},
    )
    assert r.status_code == 404
