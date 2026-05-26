"""Assimp scene load -> OBJ+MTL+textures.zip emission.

Orchestrates layers + materials + packaging for the FastAPI `/v1/preconvert`
endpoint.  All scene access happens inside the ``pyassimp.load`` context
manager so the underlying C-side memory is released even if a downstream
writer raises.
"""

from __future__ import annotations

import logging
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, Optional

logger = logging.getLogger("prism-assimp.converter")


@dataclass(frozen=True)
class PreconvertOptions:
    flatten_hierarchy: bool = False
    target_unit: str = "m"


@dataclass
class PreconvertResult:
    obj_path: Path
    mtl_path: Optional[Path]
    zip_path: Path
    stats: Dict[str, Any] = field(default_factory=dict)
    manifest: Dict[str, Any] = field(default_factory=dict)


# Source files are interpreted in their authored unit (we don't attempt to
# detect it -- glTF says metres, FBX says centimetres-by-default-but-actually-
# whatever-the-author-wanted, OBJ has no unit at all).  ``target_unit``
# multiplies vertex positions on the way out so downstream consumers get a
# consistent scale.  The agent imports OBJ in metres regardless.
_UNIT_TO_METRES = {
    "mm": 0.001,
    "cm": 0.01,
    "m": 1.0,
    "inch": 0.0254,
    "ft": 0.3048,
}


def _expand_bundle(bundle_path: Optional[Path], work_dir: Path) -> Optional[Path]:
    if bundle_path is None:
        return None
    bundle_dir = work_dir / "bundle"
    bundle_dir.mkdir(parents=True, exist_ok=True)
    try:
        with zipfile.ZipFile(bundle_path) as zf:
            for member in zf.namelist():
                # zipfile prevents path traversal in 3.6+ but we belt-and-brace.
                target = (bundle_dir / member).resolve()
                if bundle_dir.resolve() not in target.parents and target != bundle_dir.resolve():
                    logger.warning("skipping suspicious zip member %r", member)
                    continue
            zf.extractall(bundle_dir)
    except zipfile.BadZipFile:
        logger.warning("bundle %s is not a valid zip; ignoring", bundle_path)
        return None
    logger.info("expanded bundle into %s", bundle_dir)
    return bundle_dir


def _postprocess_flags(options: PreconvertOptions) -> int:
    """Build the bitwise-or of post-processing steps we want Assimp to run.
    Imported lazily so the module is importable even when ``pyassimp`` /
    ``libassimp`` aren't installed (handy for unit tests on the API
    surface).
    """
    from pyassimp import postprocess  # type: ignore

    flags = (
        postprocess.aiProcess_Triangulate
        | postprocess.aiProcess_GenSmoothNormals
        | postprocess.aiProcess_GenUVCoords
        | postprocess.aiProcess_TransformUVCoords
        | postprocess.aiProcess_JoinIdenticalVertices
        | postprocess.aiProcess_ImproveCacheLocality
        | postprocess.aiProcess_FindInstances
        | postprocess.aiProcess_ValidateDataStructure
        | postprocess.aiProcess_FixInfacingNormals
    )
    if options.flatten_hierarchy:
        flags |= postprocess.aiProcess_PreTransformVertices
    return flags


def preconvert_file(
    src_path: Path,
    bundle_path: Optional[Path],
    work_dir: Path,
    options: PreconvertOptions,
) -> PreconvertResult:
    """Convert ``src_path`` into an OBJ+MTL+textures.zip under ``work_dir``.

    Returns a :class:`PreconvertResult` that the FastAPI layer turns into
    a JSON response or a raw file stream.
    """
    logger.info(
        "preconvert src=%s bundle=%s options=%s",
        src_path,
        bundle_path,
        options,
    )

    bundle_dir = _expand_bundle(bundle_path, work_dir)

    obj_path = work_dir / "model.obj"
    mtl_path = work_dir / "model.mtl"
    manifest_path = work_dir / "manifest.json"
    texture_dir = work_dir / "textures"
    texture_dir.mkdir(parents=True, exist_ok=True)

    scale = _UNIT_TO_METRES.get(options.target_unit, 1.0)

    # Defer the imports that need libassimp until we actually run, so unit
    # tests against the FastAPI surface don't transitively require the
    # native library.
    import pyassimp  # type: ignore
    from .layers import walk_leaves
    from .materials import emit_mtl_and_textures
    from .packaging import build_zip, write_manifest, write_obj

    flags = _postprocess_flags(options)

    try:
        with pyassimp.load(str(src_path), processing=flags) as scene:
            leaves = list(walk_leaves(scene))
            materials_bundle = emit_mtl_and_textures(
                scene,
                src_path.parent,
                mtl_path,
                texture_dir,
                bundle_dir,
            )
            obj_stats = write_obj(
                obj_path,
                "model.mtl",
                scene,
                leaves,
                materials_bundle.materials,
                scale=scale,
            )
            manifest = write_manifest(manifest_path, leaves, materials_bundle)
            mesh_count = len(list(getattr(scene, "meshes", []) or []))
    except pyassimp.AssimpError as exc:
        logger.exception("assimp failed to load %s", src_path)
        raise RuntimeError(f"Assimp failed to load file: {exc}") from exc

    files_to_pack = [obj_path, mtl_path, manifest_path]
    files_to_pack.extend(p for p in texture_dir.iterdir() if p.is_file())
    zip_path = work_dir / "model.zip"
    build_zip(zip_path, files_to_pack, work_dir)

    texture_count = sum(1 for _ in texture_dir.iterdir() if _.is_file())

    stats: Dict[str, Any] = {
        "meshes": mesh_count,
        "vertices": obj_stats["vertices"],
        "triangles": obj_stats["triangles"],
        "groups": obj_stats["groups"],
        "materials": len(materials_bundle.materials),
        "textures": texture_count,
        "leaves": len(leaves),
        "scale_to_metres": scale,
    }

    return PreconvertResult(
        obj_path=obj_path,
        mtl_path=mtl_path,
        zip_path=zip_path,
        stats=stats,
        manifest=manifest,
    )
