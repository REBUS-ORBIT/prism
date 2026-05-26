"""Smoke tests for the FastAPI surface.

These don't require ``libassimp`` to be installed -- they just check that
the routes are wired up and that request validation / error handling work
sensibly.  The full converter is exercised by the integration test on the
runner LXC after the docker image is built, since pyassimp.load needs
the native libassimp.so present.
"""

from __future__ import annotations

from pathlib import Path

from fastapi.testclient import TestClient

from assimp_service.main import app

client = TestClient(app)


def test_health() -> None:
    r = client.get("/health")
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is True
    assert isinstance(body["version"], str)


def test_formats() -> None:
    r = client.get("/v1/formats")
    assert r.status_code == 200
    exts = r.json()["extensions"]
    assert ".gltf" in exts
    assert ".glb" in exts
    assert ".stl" in exts


def test_preconvert_unsupported_extension() -> None:
    r = client.post(
        "/v1/preconvert",
        files={"file": ("model.foo", b"\x00\x01\x02\x03", "application/octet-stream")},
    )
    assert r.status_code == 415


def test_preconvert_bad_target_unit() -> None:
    r = client.post(
        "/v1/preconvert",
        files={"file": ("scene.glb", b"glTF\x02\x00\x00\x00" + b"\x00" * 24, "model/gltf-binary")},
        data={"target_unit": "parsec"},
    )
    assert r.status_code == 400


def test_preconvert_invalid_glb_returns_500() -> None:
    """A truncated .glb has the right extension and target_unit, so the
    server gets all the way to ``pyassimp.load`` which raises -- the
    converter wraps that in a RuntimeError that the route turns into 500.

    Skipped automatically when libassimp isn't installed (e.g. on plain
    Windows dev boxes without the runtime image)."""
    try:
        import pyassimp  # type: ignore # noqa: F401
    except Exception:
        import pytest

        pytest.skip("pyassimp / libassimp not available in this environment")
    r = client.post(
        "/v1/preconvert",
        files={"file": ("scene.glb", b"glTF\x02\x00\x00\x00" + b"\x00" * 24, "model/gltf-binary")},
    )
    assert r.status_code == 500
