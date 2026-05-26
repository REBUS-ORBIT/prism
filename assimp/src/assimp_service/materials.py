"""aiMaterial -> MTL + textures.

Extracts embedded textures, resolves external texture paths (consulting
the optional sidecar bundle directory + the source file's parent dir),
normalises non-Rhino-friendly formats (EXR, HDR, BC*) to PNG via Pillow,
and emits a Wavefront MTL file that the agent's existing OBJ+MTL path
can ingest unchanged.
"""

from __future__ import annotations

import logging
import re
import shutil
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

logger = logging.getLogger("prism-assimp.materials")


# ---------- public dataclasses ----------------------------------------------


@dataclass
class MaterialRecord:
    """One material in the final MTL.

    Mirrors the fields we currently set in the agent's PBR pipeline so the
    Rhino importer can map MTL -> RenderMaterial cleanly.
    """

    name: str
    diffuse_rgb: Tuple[float, float, float] = (0.8, 0.8, 0.8)
    specular_rgb: Tuple[float, float, float] = (0.0, 0.0, 0.0)
    emissive_rgb: Tuple[float, float, float] = (0.0, 0.0, 0.0)
    opacity: float = 1.0
    roughness: Optional[float] = None
    metalness: Optional[float] = None
    map_kd: Optional[str] = None              # diffuse / base color
    map_ks: Optional[str] = None              # specular
    map_ke: Optional[str] = None              # emission
    map_normal: Optional[str] = None          # bump_norm
    map_roughness: Optional[str] = None       # PBR roughness
    map_metalness: Optional[str] = None       # PBR metalness


@dataclass
class MaterialsBundle:
    """Output of materials.emit_mtl_and_textures."""

    mtl_path: Path
    texture_dir: Path
    materials: List[MaterialRecord] = field(default_factory=list)


# ---------- pyassimp property helpers ---------------------------------------
#
# pyassimp's `material.properties` is a dict keyed by raw Assimp MATKEY
# strings.  Across versions and loaders the same logical property may appear
# under several keys (e.g. ``"name"``, ``"?mat.name"``, ``"$mat.name"``),
# and texture file paths show up as either a flat ``"file"`` entry or
# nested under per-slot keys like ``("file", textureType, slot)``.
#
# These helpers normalise that quirk so the writer code stays readable.


_NAME_KEYS = ("name", "?mat.name", "$mat.name", "mat.name")
_DIFFUSE_KEYS = ("diffuse", "$clr.diffuse", "clr.diffuse")
_SPECULAR_KEYS = ("specular", "$clr.specular", "clr.specular")
_EMISSIVE_KEYS = ("emissive", "$clr.emissive", "clr.emissive")
_OPACITY_KEYS = ("opacity", "$mat.opacity", "mat.opacity")
_ROUGHNESS_KEYS = (
    "roughnessfactor",
    "$mat.roughnessfactor",
    "$mat.gltf.pbrMetallicRoughness.roughnessFactor",
    "$mat.metallicroughness.roughnessfactor",
)
_METALNESS_KEYS = (
    "metallicfactor",
    "$mat.metallicfactor",
    "$mat.gltf.pbrMetallicRoughness.metallicFactor",
    "$mat.metallicroughness.metallicfactor",
)
_BASE_COLOR_KEYS = (
    "basecolor",
    "$clr.base",
    "$mat.gltf.pbrMetallicRoughness.baseColorFactor",
)


def _walk_properties(material: object):
    """Yield ``(lower_key, value)`` for every property on a pyassimp
    Material, accepting both flat and tuple-keyed schemas.
    """
    props: Dict[Any, Any] = getattr(material, "properties", {}) or {}
    for key, value in props.items():
        if isinstance(key, tuple):
            head = key[0]
        else:
            head = key
        if not isinstance(head, str):
            continue
        yield head.lower(), value


def _first_match(material: object, candidates: Tuple[str, ...]) -> Optional[Any]:
    wanted = {c.lower() for c in candidates}
    for k, v in _walk_properties(material):
        if k in wanted:
            return v
    return None


def _to_rgb(value: Any, fallback: Tuple[float, float, float]) -> Tuple[float, float, float]:
    if value is None:
        return fallback
    try:
        seq = list(value)
    except TypeError:
        return fallback
    if len(seq) < 3:
        return fallback
    try:
        return (float(seq[0]), float(seq[1]), float(seq[2]))
    except (TypeError, ValueError):
        return fallback


def _to_float(value: Any, fallback: Optional[float]) -> Optional[float]:
    if value is None:
        return fallback
    try:
        if hasattr(value, "__len__") and len(value) > 0:  # sometimes wrapped in [x]
            return float(value[0])
        return float(value)
    except (TypeError, ValueError):
        return fallback


# ---------- texture extraction ----------------------------------------------


_TEXTURE_SLOTS = (
    # (logical name, candidate keys)
    ("diffuse", ("$tex.file", "$raw.tex.diffuse", "$raw.tex.basecolor", "diffuse.file")),
    ("specular", ("$raw.tex.specular", "specular.file")),
    ("emissive", ("$raw.tex.emissive", "emissive.file")),
    ("normal", ("$raw.tex.normals", "normal.file")),
    ("roughness", ("$raw.tex.roughness", "$raw.tex.shininess", "roughness.file")),
    ("metalness", ("$raw.tex.metalness", "$raw.tex.ambient", "metalness.file")),
)


def _strip_quotes(s: str) -> str:
    return s.strip().strip('"').strip("'")


def _safe_filename_part(s: str, fallback: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "_", s).strip("._-")
    return cleaned or fallback


def _texture_paths_by_slot(material: object) -> Dict[str, str]:
    """Return ``{slot_name: assimp_path}`` for every texture slot on the
    material.  Path strings may be sentinel form ``"*N"`` (embedded texture
    index N) or external paths.
    """
    out: Dict[str, str] = {}
    for slot_name, candidates in _TEXTURE_SLOTS:
        wanted = {c.lower() for c in candidates}
        for key, value in _walk_properties(material):
            if key not in wanted or not isinstance(value, str):
                continue
            cleaned = _strip_quotes(value)
            if cleaned and slot_name not in out:
                out[slot_name] = cleaned
                break
    # Some pyassimp builds expose the diffuse path simply as "file".
    if "diffuse" not in out:
        flat = _first_match(material, ("file",))
        if isinstance(flat, str) and flat.strip():
            out["diffuse"] = _strip_quotes(flat)
    return out


def _resolve_external_texture(
    rel_path: str,
    src_dir: Path,
    bundle_dir: Optional[Path],
) -> Optional[Path]:
    """Try to locate an external texture file given the path string the
    exporter put in the material.  Search order:
        1. Absolute path (rare but happens in mis-authored DAE files).
        2. ``src_dir / rel_path``.
        3. ``bundle_dir / rel_path`` if a sidecar bundle was provided.
        4. Recursive basename match under ``bundle_dir`` (handles
           exporters that embed Windows-style absolute paths).
    """
    candidates: List[Path] = []
    p = Path(rel_path)
    if p.is_absolute():
        candidates.append(p)
    candidates.append(src_dir / p)
    if bundle_dir is not None:
        candidates.append(bundle_dir / p)
    for c in candidates:
        try:
            if c.is_file():
                return c
        except OSError:
            continue
    if bundle_dir is not None:
        target_name = p.name.lower()
        for c in bundle_dir.rglob("*"):
            if c.is_file() and c.name.lower() == target_name:
                return c
    return None


# Pillow can read these natively but Rhino's MTL importer chokes on them;
# normalise to PNG.
_NEEDS_NORMALISE = {".exr", ".hdr", ".tga", ".tif", ".tiff", ".dds"}


def _copy_or_normalise(src: Path, dest_dir: Path, basename: str) -> Optional[Path]:
    """Copy ``src`` into ``dest_dir`` under a name based on ``basename``.
    Convert via Pillow when the format isn't friendly to Rhino's MTL
    importer.  Returns the final path on disk, or None on failure.
    """
    ext = src.suffix.lower()
    safe = _safe_filename_part(basename, "texture")
    if ext in _NEEDS_NORMALISE:
        try:
            from PIL import Image  # type: ignore
        except ImportError:
            logger.warning("Pillow not available; copying %s as-is", src)
            target = dest_dir / f"{safe}{ext}"
            shutil.copy2(src, target)
            return target
        try:
            target = dest_dir / f"{safe}.png"
            with Image.open(src) as im:
                im.convert("RGBA" if im.mode in ("RGBA", "LA") else "RGB").save(target, format="PNG")
            return target
        except Exception:
            logger.exception("Pillow failed to normalise %s; copying raw", src)
            target = dest_dir / f"{safe}{ext}"
            shutil.copy2(src, target)
            return target
    target = dest_dir / f"{safe}{ext or '.bin'}"
    shutil.copy2(src, target)
    return target


def _extract_embedded(
    scene: object,
    embed_index: int,
    dest_dir: Path,
    basename: str,
) -> Optional[Path]:
    """Materialise scene.textures[embed_index] onto disk.  Handles the two
    storage shapes Assimp uses:
        - compressed: ``achFormatHint`` is non-empty and ``pcData`` is the
          raw bytes of the encoded image.
        - uncompressed: ``pcData`` is RGBA pixels at ``width x height``.
    """
    textures = list(getattr(scene, "textures", []) or [])
    if embed_index < 0 or embed_index >= len(textures):
        return None
    tex = textures[embed_index]
    safe = _safe_filename_part(basename, f"embedded_{embed_index}")

    fmt_hint = getattr(tex, "achFormatHint", None) or getattr(tex, "format_hint", None)
    if isinstance(fmt_hint, bytes):
        fmt_hint = fmt_hint.decode("ascii", errors="ignore")
    fmt_hint = (fmt_hint or "").strip("\x00").strip().lower()
    raw = getattr(tex, "data", None)
    if raw is None:
        raw = getattr(tex, "pcData", None)

    if fmt_hint and raw is not None:
        ext = "." + fmt_hint
        if ext == ".jpg":
            ext = ".jpeg"
        target = dest_dir / f"{safe}{ext}"
        try:
            target.write_bytes(bytes(raw))
            return target
        except Exception:
            logger.exception("Failed writing embedded texture %s", embed_index)
            return None

    width = int(getattr(tex, "width", 0) or 0)
    height = int(getattr(tex, "height", 0) or 0)
    if width > 0 and height > 0 and raw is not None:
        try:
            from PIL import Image  # type: ignore
            import numpy as _np
            arr = _np.frombuffer(bytes(raw), dtype=_np.uint8).reshape(height, width, 4)
            target = dest_dir / f"{safe}.png"
            Image.fromarray(arr, mode="RGBA").save(target, format="PNG")
            return target
        except Exception:
            logger.exception("Failed converting raw RGBA embedded texture %s", embed_index)
    return None


# ---------- public emitter --------------------------------------------------


def _material_name(material: object, index: int, taken: set) -> str:
    raw = _first_match(material, _NAME_KEYS)
    if isinstance(raw, bytes):
        raw = raw.decode("utf-8", errors="ignore")
    base = _safe_filename_part(str(raw or f"material_{index}"), f"material_{index}")
    name = base
    suffix = 1
    while name in taken:
        suffix += 1
        name = f"{base}_{suffix}"
    taken.add(name)
    return name


def _format_mtl_color(channel: Tuple[float, float, float]) -> str:
    return f"{channel[0]:.6f} {channel[1]:.6f} {channel[2]:.6f}"


def emit_mtl_and_textures(
    scene: object,
    src_dir: Path,
    mtl_path: Path,
    texture_dir: Path,
    bundle_dir: Optional[Path],
) -> MaterialsBundle:
    """Walk ``scene.materials``, copy/convert textures, write ``model.mtl``.

    Returns the full ``MaterialsBundle`` so the manifest writer can
    introspect material properties without re-walking the scene.
    """
    texture_dir.mkdir(parents=True, exist_ok=True)
    materials_out: List[MaterialRecord] = []
    taken_names: set = set()

    materials = list(getattr(scene, "materials", []) or [])
    logger.info(
        "emit_mtl: %d materials, %d embedded textures, src_dir=%s, bundle_dir=%s",
        len(materials),
        len(getattr(scene, "textures", []) or []),
        src_dir,
        bundle_dir,
    )

    for index, material in enumerate(materials):
        name = _material_name(material, index, taken_names)
        diffuse = _to_rgb(
            _first_match(material, _BASE_COLOR_KEYS) or _first_match(material, _DIFFUSE_KEYS),
            (0.8, 0.8, 0.8),
        )
        specular = _to_rgb(_first_match(material, _SPECULAR_KEYS), (0.0, 0.0, 0.0))
        emissive = _to_rgb(_first_match(material, _EMISSIVE_KEYS), (0.0, 0.0, 0.0))
        opacity = _to_float(_first_match(material, _OPACITY_KEYS), 1.0) or 1.0
        roughness = _to_float(_first_match(material, _ROUGHNESS_KEYS), None)
        metalness = _to_float(_first_match(material, _METALNESS_KEYS), None)

        slot_paths: Dict[str, Optional[str]] = {}
        for slot_name, raw_path in _texture_paths_by_slot(material).items():
            relpath: Optional[Path] = None
            if raw_path.startswith("*"):
                # Embedded texture sentinel from glTF/GLB or FBX.
                try:
                    embed_index = int(raw_path[1:])
                except ValueError:
                    embed_index = -1
                got = _extract_embedded(
                    scene,
                    embed_index,
                    texture_dir,
                    f"{name}_{slot_name}",
                )
                if got is not None:
                    relpath = got.relative_to(texture_dir.parent)
            else:
                external = _resolve_external_texture(raw_path, src_dir, bundle_dir)
                if external is not None:
                    got = _copy_or_normalise(external, texture_dir, f"{name}_{slot_name}")
                    if got is not None:
                        relpath = got.relative_to(texture_dir.parent)
                else:
                    logger.warning(
                        "material %r slot %s: could not resolve texture %r",
                        name,
                        slot_name,
                        raw_path,
                    )
            slot_paths[slot_name] = relpath.as_posix() if relpath else None

        materials_out.append(
            MaterialRecord(
                name=name,
                diffuse_rgb=diffuse,
                specular_rgb=specular,
                emissive_rgb=emissive,
                opacity=opacity,
                roughness=roughness,
                metalness=metalness,
                map_kd=slot_paths.get("diffuse"),
                map_ks=slot_paths.get("specular"),
                map_ke=slot_paths.get("emissive"),
                map_normal=slot_paths.get("normal"),
                map_roughness=slot_paths.get("roughness"),
                map_metalness=slot_paths.get("metalness"),
            )
        )

    with mtl_path.open("w", encoding="utf-8") as f:
        f.write("# prism-assimp generated MTL\n\n")
        for rec in materials_out:
            f.write(f"newmtl {rec.name}\n")
            f.write(f"Kd {_format_mtl_color(rec.diffuse_rgb)}\n")
            f.write(f"Ks {_format_mtl_color(rec.specular_rgb)}\n")
            f.write(f"Ke {_format_mtl_color(rec.emissive_rgb)}\n")
            f.write(f"d  {rec.opacity:.6f}\n")
            f.write("illum 2\n")
            if rec.roughness is not None:
                f.write(f"Pr {rec.roughness:.6f}\n")
            if rec.metalness is not None:
                f.write(f"Pm {rec.metalness:.6f}\n")
            if rec.map_kd:
                f.write(f"map_Kd {rec.map_kd}\n")
            if rec.map_ks:
                f.write(f"map_Ks {rec.map_ks}\n")
            if rec.map_ke:
                f.write(f"map_Ke {rec.map_ke}\n")
            if rec.map_normal:
                f.write(f"norm {rec.map_normal}\n")
                f.write(f"map_Bump {rec.map_normal}\n")
            if rec.map_roughness:
                f.write(f"map_Pr {rec.map_roughness}\n")
            if rec.map_metalness:
                f.write(f"map_Pm {rec.map_metalness}\n")
            f.write("\n")

    return MaterialsBundle(
        mtl_path=mtl_path,
        texture_dir=texture_dir,
        materials=materials_out,
    )
