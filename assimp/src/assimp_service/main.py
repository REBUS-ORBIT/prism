"""FastAPI app for prism-assimp.

SCAFFOLD ONLY -- the routes are wired but the heavy lifting in `converter`,
`materials`, `layers`, and `packaging` is intentionally left as TODOs so the
contract can be reviewed before implementation.
"""

from __future__ import annotations

import logging
import os
import shutil
import tempfile
import uuid
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, JSONResponse
from pydantic import BaseModel

from . import __version__
from .converter import preconvert_file, PreconvertOptions, PreconvertResult

logger = logging.getLogger("prism-assimp")
# Python's logging.basicConfig is case-sensitive on the level name (it
# routes through ``logging.getLevelName`` which does NOT uppercase its
# input).  The shared compose default is ``LOG_LEVEL=info`` (lower-case
# matches Fastify / Pino conventions on the orchestrator side), so we
# uppercase here before handing off.
_raw_level = os.environ.get("LOG_LEVEL", "INFO").strip().upper()
logging.basicConfig(
    level=_raw_level if _raw_level else "INFO",
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)

WORK_ROOT = Path(os.environ.get("ASSIMP_WORK_ROOT", "/work"))
WORK_ROOT.mkdir(parents=True, exist_ok=True)

# Extensions we are willing to advertise to the orchestrator.  Always a subset
# of whatever Assimp can read at runtime; we keep an allowlist so a new
# Assimp version doesn't silently turn on a noisy importer (e.g. .blend with
# half-broken material handling).
SUPPORTED_EXTS = frozenset({
    ".gltf", ".glb",
    ".stl", ".ply",
    ".dae",
    ".blend",
    ".x",
    ".usdz",
})

app = FastAPI(
    title="prism-assimp",
    version=__version__,
    description="Pre-conversion sidecar that turns Assimp-readable files into "
                "the OBJ+MTL+textures.zip bundle expected by the PRISM agent.",
)


class HealthResponse(BaseModel):
    ok: bool
    version: str


class FormatsResponse(BaseModel):
    extensions: list[str]


@app.get("/health", response_model=HealthResponse)
def health() -> HealthResponse:
    return HealthResponse(ok=True, version=__version__)


@app.get("/v1/formats", response_model=FormatsResponse)
def formats() -> FormatsResponse:
    return FormatsResponse(extensions=sorted(SUPPORTED_EXTS))


@app.post("/v1/preconvert", response_model=None)
async def preconvert(
    file: UploadFile = File(..., description="The source 3D file to pre-convert."),
    bundle: Optional[UploadFile] = File(
        default=None,
        description="Optional .zip of sibling textures.",
    ),
    flatten_hierarchy: bool = Form(default=False),
    target_unit: str = Form(default="m"),
    return_mode: str = Form(
        default="json",
        description="json -> { url, stats }; stream -> raw zip bytes.",
    ),
) -> JSONResponse | FileResponse:
    """Accept a multipart upload, run Assimp, return an OBJ+MTL+textures.zip.

    NOTE: implementation lives in `converter.preconvert_file`; this endpoint
    is just request validation + filesystem plumbing.
    """
    job_id = uuid.uuid4().hex
    work_dir = WORK_ROOT / job_id
    work_dir.mkdir(parents=True, exist_ok=False)

    src_path = work_dir / (file.filename or "input.bin")
    with src_path.open("wb") as out:
        shutil.copyfileobj(file.file, out)
    logger.info("job %s: saved input %s (%d bytes)", job_id, src_path.name, src_path.stat().st_size)

    bundle_path: Optional[Path] = None
    if bundle is not None:
        bundle_path = work_dir / "bundle.zip"
        with bundle_path.open("wb") as out:
            shutil.copyfileobj(bundle.file, out)
        logger.info("job %s: saved bundle %s (%d bytes)", job_id, bundle_path.name, bundle_path.stat().st_size)

    if src_path.suffix.lower() not in SUPPORTED_EXTS:
        raise HTTPException(status_code=415, detail=f"Unsupported extension '{src_path.suffix}'.")
    if target_unit not in {"mm", "cm", "m", "inch", "ft"}:
        raise HTTPException(status_code=400, detail=f"Unsupported target_unit '{target_unit}'.")

    options = PreconvertOptions(
        flatten_hierarchy=flatten_hierarchy,
        target_unit=target_unit,
    )
    try:
        result: PreconvertResult = preconvert_file(src_path, bundle_path, work_dir, options)
    except NotImplementedError as exc:
        raise HTTPException(status_code=501, detail=str(exc))
    except Exception as exc:
        logger.exception("job %s: preconvert failed", job_id)
        raise HTTPException(status_code=500, detail=str(exc)) from exc

    if return_mode == "stream":
        return FileResponse(
            path=result.zip_path,
            media_type="application/zip",
            filename=f"{job_id}.zip",
        )
    return JSONResponse(
        {
            "ok": True,
            "job_id": job_id,
            "outputs": {
                "obj": str(result.obj_path),
                "mtl": str(result.mtl_path) if result.mtl_path else None,
                "zip": str(result.zip_path),
            },
            "stats": result.stats,
            "manifest": result.manifest,
        }
    )
