"""Node-tree -> OBJ group path normalisation.

Walks an `aiScene` rooted at ``scene.rootnode`` depth-first and yields one
``LeafRecord`` per (node, mesh) pair so the OBJ emitter can produce a
``g <path>`` line per visible chunk of geometry while the manifest writer
gets a complete picture of the layer hierarchy.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Iterator, List, Optional, Tuple

import numpy as np

logger = logging.getLogger("prism-assimp.layers")


@dataclass(frozen=True)
class LeafRecord:
    """A renderable node-mesh pair in the scene graph.

    Attributes
    ----------
    layer_path
        Slash-separated path from scene root down to the owning node, with
        characters illegal in OBJ group names escaped.  Used verbatim as
        the value of ``g <layer_path>`` in the emitted OBJ.
    mesh_index
        Index into ``scene.meshes``.
    material_index
        Index into ``scene.materials``.  Mirrors the aiMesh's
        ``material_index`` for convenience so the OBJ writer doesn't need
        to dereference the mesh again.
    world_transform
        4x4 row-major matrix accumulated from the scene root to the owning
        node.  OBJ has no transform syntax so vertices and normals are
        pre-multiplied before emission.
    """

    layer_path: str
    mesh_index: int
    material_index: int
    world_transform: Tuple[float, ...]  # 16 floats, row-major


def sanitise_group_name(name: Optional[str], fallback: str) -> str:
    """OBJ group names cannot contain whitespace; we also strip ``/`` so
    nested paths can be reconstructed losslessly when the importer splits
    on ``/``.
    """
    if name is None:
        return fallback
    cleaned = (
        name.strip()
        .replace("/", "_")
        .replace(" ", "_")
        .replace("\t", "_")
        .replace("\r", "")
        .replace("\n", "")
    )
    return cleaned or fallback


def _node_meshes(node: object, scene_meshes: List[object]) -> List[Tuple[int, object]]:
    """pyassimp's ``node.meshes`` flips between "list of indices" and "list
    of resolved Mesh objects" depending on version and how the scene was
    loaded.  Normalise to ``[(index, mesh), ...]``.
    """
    raw = getattr(node, "meshes", None) or []
    out: List[Tuple[int, object]] = []
    for entry in raw:
        if isinstance(entry, (int, np.integer)):
            idx = int(entry)
            if 0 <= idx < len(scene_meshes):
                out.append((idx, scene_meshes[idx]))
        else:
            try:
                idx = scene_meshes.index(entry)
            except ValueError:
                # Mesh isn't in the scene mesh list (shouldn't happen, but be safe).
                continue
            out.append((idx, entry))
    return out


def walk_leaves(scene: object) -> Iterator[LeafRecord]:
    """Yield one ``LeafRecord`` per (node, mesh) pair reachable from
    ``scene.rootnode``.

    Internal nodes that carry meshes themselves are emitted too -- Assimp's
    convention is that any node may own meshes regardless of whether it has
    children, so "leaf" here means "(node, mesh) pair", not "graph leaf".
    """
    root = getattr(scene, "rootnode", None)
    if root is None:
        return
    scene_meshes = list(getattr(scene, "meshes", []) or [])

    def _descend(node: object, parent_path: str, parent_xform: np.ndarray) -> Iterator[LeafRecord]:
        local = np.asarray(node.transformation, dtype=np.float64).reshape(4, 4)
        world = parent_xform @ local

        seg = sanitise_group_name(getattr(node, "name", None), "node")
        path = f"{parent_path}/{seg}" if parent_path else seg

        for mesh_index, mesh in _node_meshes(node, scene_meshes):
            material_index = int(getattr(mesh, "materialindex", 0) or 0)
            yield LeafRecord(
                layer_path=path,
                mesh_index=mesh_index,
                material_index=material_index,
                world_transform=tuple(float(v) for v in world.ravel()),
            )

        for child in getattr(node, "children", []) or []:
            yield from _descend(child, path, world)

    yield from _descend(root, "", np.eye(4, dtype=np.float64))
