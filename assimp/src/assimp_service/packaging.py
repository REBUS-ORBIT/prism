"""OBJ emission + manifest writer + zip packaging."""

from __future__ import annotations

import json
import logging
import zipfile
from pathlib import Path
from typing import Iterable, Iterator, List, Sequence

import numpy as np

from .layers import LeafRecord
from .materials import MaterialRecord, MaterialsBundle

logger = logging.getLogger("prism-assimp.packaging")


# ---------- OBJ writer ------------------------------------------------------


def _normal_matrix(world: np.ndarray) -> np.ndarray:
    """Return the inverse-transpose of the upper-left 3x3 of ``world``,
    falling back to the rotation block when the world matrix is singular
    (e.g. zero scale on one axis -- which Assimp does emit in degenerate
    inputs).
    """
    upper = world[:3, :3]
    try:
        return np.linalg.inv(upper).T
    except np.linalg.LinAlgError:
        return upper


def _texturecoords(mesh: object) -> "np.ndarray | None":
    """Return the first non-empty UV channel as an (N, 2) ndarray, or
    ``None`` when the mesh has no usable UVs.

    pyassimp exposes ``mesh.texturecoords`` as either an iterable of UV
    channels (each shape ``(N, 3)``) or a single such array, depending on
    version.  We accept both.
    """
    raw = getattr(mesh, "texturecoords", None)
    if raw is None:
        return None
    arr = np.asarray(raw)
    if arr.dtype == object or arr.size == 0:
        return None
    if arr.ndim == 3:
        # (channels, N, 3) -> first non-empty channel
        for channel in arr:
            ch = np.asarray(channel)
            if ch.size > 0:
                return ch[:, :2].astype(np.float32, copy=False)
        return None
    if arr.ndim == 2 and arr.shape[1] >= 2:
        return arr[:, :2].astype(np.float32, copy=False)
    return None


def _iter_triangles(mesh: object) -> Iterator[Sequence[int]]:
    """Yield triangle index triples from a mesh whose faces have been
    triangulated by Assimp's ``aiProcess_Triangulate`` post-processing
    flag.  Faces with > 3 vertices are fan-triangulated as a defensive
    fallback in case triangulation didn't run.
    """
    faces = getattr(mesh, "faces", None)
    if faces is None:
        return
    for face in faces:
        idx = list(int(i) for i in face)
        if len(idx) < 3:
            continue
        if len(idx) == 3:
            yield idx
        else:
            for i in range(1, len(idx) - 1):
                yield [idx[0], idx[i], idx[i + 1]]


def write_obj(
    obj_path: Path,
    mtl_relpath: str,
    scene: object,
    leaves: Iterable[LeafRecord],
    materials: List[MaterialRecord],
    scale: float = 1.0,
) -> dict:
    """Write ``model.obj`` from a sequence of LeafRecords.

    Each leaf becomes its own ``g <layer_path>`` group with a ``usemtl``
    line.  Vertices, normals, and UVs are emitted per-leaf with their own
    index space (which is wasteful in theory but keeps the writer trivial
    and the resulting file plenty small for the scenes we care about --
    OBJ vertex sharing is a meaningful optimisation only above ~10M verts,
    and Assimp will already have collapsed identical vertices via
    ``aiProcess_JoinIdenticalVertices`` per-mesh).
    """
    scene_meshes = list(getattr(scene, "meshes", []) or [])
    total_vertices = 0
    total_triangles = 0
    groups = 0

    v_offset = 1  # OBJ is 1-indexed
    vt_offset = 1
    vn_offset = 1

    with obj_path.open("w", encoding="utf-8") as f:
        f.write("# prism-assimp auto-generated OBJ\n")
        f.write(f"mtllib {mtl_relpath}\n\n")

        for leaf in leaves:
            if leaf.mesh_index < 0 or leaf.mesh_index >= len(scene_meshes):
                continue
            mesh = scene_meshes[leaf.mesh_index]
            verts = np.asarray(getattr(mesh, "vertices", []), dtype=np.float64)
            if verts.size == 0 or verts.ndim != 2 or verts.shape[1] != 3:
                continue

            world = np.asarray(leaf.world_transform, dtype=np.float64).reshape(4, 4)
            ones = np.ones((verts.shape[0], 1), dtype=np.float64)
            verts_h = np.hstack([verts, ones])
            transformed = (world @ verts_h.T).T[:, :3] * scale

            normals_arr = getattr(mesh, "normals", None)
            normals = np.asarray(normals_arr, dtype=np.float64) if normals_arr is not None else None
            if normals is not None and normals.size == 0:
                normals = None
            if normals is not None and normals.shape != verts.shape:
                normals = None
            t_normals = None
            if normals is not None:
                nm = _normal_matrix(world)
                t_normals = (nm @ normals.T).T
                lengths = np.linalg.norm(t_normals, axis=1, keepdims=True)
                # Avoid division-by-zero for degenerate normals.
                lengths = np.where(lengths == 0, 1.0, lengths)
                t_normals = t_normals / lengths

            uvs = _texturecoords(mesh)
            if uvs is not None and uvs.shape[0] != verts.shape[0]:
                uvs = None

            material_name = (
                materials[leaf.material_index].name
                if 0 <= leaf.material_index < len(materials)
                else f"material_{leaf.material_index}"
            )

            f.write(f"g {leaf.layer_path}\n")
            f.write(f"usemtl {material_name}\n")

            for v in transformed:
                f.write(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}\n")
            if uvs is not None:
                # OBJ V coordinate convention is bottom-up; glTF/FBX use
                # top-down.  Assimp's aiProcess_FlipUVs would handle this
                # but we do it explicitly here so the post-process flag
                # set stays minimal.
                for uv in uvs:
                    f.write(f"vt {uv[0]:.6f} {1.0 - uv[1]:.6f}\n")
            if t_normals is not None:
                for n in t_normals:
                    f.write(f"vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}\n")

            tri_count = 0
            for tri in _iter_triangles(mesh):
                a, b, c = tri[0], tri[1], tri[2]
                if uvs is not None and t_normals is not None:
                    f.write(
                        f"f {a + v_offset}/{a + vt_offset}/{a + vn_offset} "
                        f"{b + v_offset}/{b + vt_offset}/{b + vn_offset} "
                        f"{c + v_offset}/{c + vt_offset}/{c + vn_offset}\n"
                    )
                elif uvs is not None:
                    f.write(
                        f"f {a + v_offset}/{a + vt_offset} "
                        f"{b + v_offset}/{b + vt_offset} "
                        f"{c + v_offset}/{c + vt_offset}\n"
                    )
                elif t_normals is not None:
                    f.write(
                        f"f {a + v_offset}//{a + vn_offset} "
                        f"{b + v_offset}//{b + vn_offset} "
                        f"{c + v_offset}//{c + vn_offset}\n"
                    )
                else:
                    f.write(f"f {a + v_offset} {b + v_offset} {c + v_offset}\n")
                tri_count += 1

            f.write("\n")

            v_offset += verts.shape[0]
            if uvs is not None:
                vt_offset += uvs.shape[0]
            if t_normals is not None:
                vn_offset += t_normals.shape[0]
            total_vertices += verts.shape[0]
            total_triangles += tri_count
            groups += 1

    return {
        "vertices": total_vertices,
        "triangles": total_triangles,
        "groups": groups,
    }


# ---------- manifest --------------------------------------------------------


def write_manifest(
    manifest_path: Path,
    leaves: Iterable[LeafRecord],
    materials: MaterialsBundle,
) -> dict:
    """Emit ``manifest.json`` describing the layer tree + material map.

    The orchestrator surfaces this to the admin UI so users can pick layers
    before sending the bundle to Rhino.
    """
    layer_paths: List[str] = []
    seen = set()
    for leaf in leaves:
        if leaf.layer_path in seen:
            continue
        seen.add(leaf.layer_path)
        layer_paths.append(leaf.layer_path)

    materials_out = []
    for rec in materials.materials:
        materials_out.append(
            {
                "name": rec.name,
                "diffuse": list(rec.diffuse_rgb),
                "specular": list(rec.specular_rgb),
                "emissive": list(rec.emissive_rgb),
                "opacity": rec.opacity,
                "roughness": rec.roughness,
                "metalness": rec.metalness,
                "map_kd": rec.map_kd,
                "map_ks": rec.map_ks,
                "map_ke": rec.map_ke,
                "map_normal": rec.map_normal,
                "map_roughness": rec.map_roughness,
                "map_metalness": rec.map_metalness,
            }
        )

    manifest = {
        "version": 1,
        "layers": layer_paths,
        "materials": materials_out,
    }
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return manifest


# ---------- zip -------------------------------------------------------------


def build_zip(
    zip_path: Path,
    files: List[Path],
    base_dir: Path,
) -> Path:
    """Pack ``files`` (relative to ``base_dir``) into ``zip_path``.

    No compression on textures (they're typically PNG/JPEG already);
    default deflate on OBJ/MTL/manifest because they're text and compress
    well.
    """
    with zipfile.ZipFile(zip_path, mode="w", compression=zipfile.ZIP_DEFLATED) as zf:
        for f in files:
            arcname = f.relative_to(base_dir).as_posix()
            if f.suffix.lower() in (".png", ".jpg", ".jpeg", ".webp"):
                zf.write(f, arcname=arcname, compress_type=zipfile.ZIP_STORED)
            else:
                zf.write(f, arcname=arcname)
    logger.info("packaged %d files into %s", len(files), zip_path)
    return zip_path
