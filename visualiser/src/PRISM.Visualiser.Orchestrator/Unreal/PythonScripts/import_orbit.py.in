# PRISM Visualiser — UE 5.7 Interchange import driver
#
# This file is a TEMPLATE rendered per-run by ProjectScaffolder.cs.
# Placeholders ({{RUN_ID}}, {{GLTF_PATH}}, {{TARGET_FOLDER}},
# {{LEVEL_NAME}}) are substituted before the script reaches the editor.
# The orchestrator invokes the rendered version via:
#
#     UnrealEditor-Cmd.exe REBUSVis.uproject -run=PythonScript \
#         -script="<path-to-rendered-import_orbit.py>"
#
# Contract with the orchestrator:
#   * Emit exactly one PRISM_VISUALISER_READY <json> line on success.
#   * Emit exactly one PRISM_VISUALISER_ERROR <json> line on failure
#     and sys.exit(1).
#   * No other line on stdout matters to the launcher's marker parser,
#     but every line is forwarded to the orchestrator's Serilog under
#     the "ue-editor" channel.
#
# UE 5.7 API note (issue #21):
#   * The singleton accessor is `InterchangeManager.get_interchange_manager_scripted()`.
#     The pre-5.5 name `get_interchange_manager()` was removed; calling it
#     raises AttributeError under 5.7.
#   * `import_asset` takes (content_path, source_data, import_asset_parameters)
#     — `content_path` is the first positional argument, NOT a field on
#     `ImportAssetParameters`. `ImportAssetParameters` has no
#     `destination_path` field; setting it on a fresh instance is a no-op
#     pre-5.7 and a hard AttributeError on 5.7.
#   * Source data is built explicitly via
#     `InterchangeManager.create_source_data(file_name)`.
#   * The legacy `AssetImportTask` fallback is removed: `AssetImportTask`
#     is Slate-bound (it routes through the import-settings dialog even
#     with `automated=True`) and Slate is NOT initialised when the editor
#     runs as `PythonScriptCommandlet` under `-NullRHI`. Hitting the
#     fallback path crashed UE on `Assertion failed: CurrentApplication
#     .IsValid() [SlateApplication.h:321]` and the commandlet
#     `RequestExit(1, 3, ...)`'d out with exit code 3.
#
# Headless mesh-spawn note (v0.3.12 / visualiser-v0.5.10):
#   * Once v0.3.11 actually discovered a mesh to place, spawning it via
#     `EditorActorSubsystem.spawn_actor_from_object(mesh, …)` crashed UE
#     with `EXCEPTION_ACCESS_VIOLATION reading 0x40` in EditorFramework
#     (commandlet `RequestExitWithStatus(1, 3)` -> exit code 3). That
#     object-spawn helper fires editor selection / component-visualizer
#     notifications that deref null under the `-NullRHI` PythonScript
#     commandlet. We now spawn a plain `StaticMeshActor` via the
#     class-spawn path (identical to how the lights spawn cleanly) and
#     assign the mesh to its `StaticMeshComponent` instead.
#
# Geometry-spawn note (v0.3.11 / visualiser-v0.5.9):
#   * UE 5.7's `InterchangeManager.import_asset(...)` returns a results
#     *container* (or None), NOT the array of created assets, so the
#     return value yields zero StaticMeshes even on a fully successful
#     import. The level was therefore created with lights + camera but
#     ZERO geometry (PRISM_VISUALISER_READY assetCount=0), so the streamed
#     frame showed a lit-but-empty scene instead of the model. Fix: after
#     import, force an Asset Registry scan of TARGET_FOLDER and enumerate
#     the StaticMesh assets Interchange actually wrote, then spawn those.
#
# Scene-framing + lighting note (v0.3.10 / visualiser-v0.5.8):
#   * `_new_level()` creates a *blank* UE level: no lights, no sky, no
#     post-process, no PlayerStart, no camera. The Phase F `-game` launch
#     streams the default player camera, which spawns at world origin with
#     default orientation into an unlit void — producing a SOLID BLACK
#     frame even though Pixel Streaming transport is fully healthy (the
#     v0.3.9 symptom: streamer Active, video+audio tracks live, viewport
#     black). UE also logs `FindPlayerStart: PATHS NOT DEFINED or NO
#     PLAYERSTART with positive rating` for the same reason.
#   * After importing geometry we therefore (a) compute the union bounds of
#     the imported meshes, (b) spawn a Directional Light + Sky Light +
#     Sky Atmosphere so the scene is lit, (c) spawn an unbound
#     PostProcessVolume with auto-exposure clamped so an empty/dark or
#     very bright frame can't crush to black / blow out, and (d) spawn a
#     framing CameraActor (auto-activated for player 0) plus a PlayerStart
#     at the same orbit transform so the streamed view shows the model
#     lit and in frame. This is model-agnostic — bounds drive the framing,
#     nothing is hardcoded for a specific import.
#
# Phase E note: BP_OrbitImporter.uasset (the Blueprint wrapper Phase D
# would have shipped) does not yet exist in the v0.1.0-ue5.7-scaffold
# template. We therefore use the Interchange Python API directly. Once
# the artist-populated v1.0.0-ue5.7 release lands, prefer the BP
# wrapper's `BP_OrbitImporter.RunImport(GLTF_PATH, TARGET_FOLDER)`
# entrypoint; the public stdout contract is unchanged.

import json
import math
import sys
import time

import unreal  # noqa: F401  - provided by UE's PythonScriptPlugin

RUN_ID = "{{RUN_ID}}"
GLTF_PATH = r"{{GLTF_PATH}}"
TARGET_FOLDER = "{{TARGET_FOLDER}}"
LEVEL_NAME = "{{LEVEL_NAME}}"


def _emit_ready(level_path, asset_count, elapsed_s):
    payload = {
        "runId": RUN_ID,
        "levelPath": level_path,
        "assetCount": int(asset_count),
        "importDurationMs": int(elapsed_s * 1000),
    }
    print("PRISM_VISUALISER_READY " + json.dumps(payload), flush=True)


def _emit_error(code, message):
    payload = {"code": code, "message": message}
    print("PRISM_VISUALISER_ERROR " + json.dumps(payload), flush=True)


def _log(message):
    # Plain stdout line — forwarded to Serilog's ue-editor channel. Not a
    # marker, so the launcher's parser ignores it.
    print("import_orbit: " + message, flush=True)


def _get_editor_asset_library():
    # EditorAssetLibrary is being deprecated in favour of
    # EditorAssetSubsystem in newer UE 5.x point releases. Tolerate both
    # by preferring the subsystem when available.
    get_editor_subsystem = getattr(unreal, "get_editor_subsystem", None)
    subsystem_class = getattr(unreal, "EditorAssetSubsystem", None)
    if get_editor_subsystem is not None and subsystem_class is not None:
        sub = get_editor_subsystem(subsystem_class)
        if sub is not None:
            return sub
    return unreal.EditorAssetLibrary


def _ensure_directory(editor_asset, target_folder):
    if hasattr(editor_asset, "does_directory_exist"):
        if not editor_asset.does_directory_exist(target_folder):
            editor_asset.make_directory(target_folder)
    else:
        # Older API name on some 5.x builds.
        editor_asset.make_directory(target_folder)


def _build_import_parameters():
    # `ImportAssetParameters` carries the per-import options. The
    # destination path is conveyed as the first positional argument to
    # `InterchangeManager.import_asset(...)`, NOT as a field on this
    # object — the field doesn't exist on UE 5.7's binding.
    params = unreal.ImportAssetParameters()
    params.is_automated = True
    params.replace_existing = True
    params.follow_redirectors = True
    return params


def _get_interchange_manager():
    # UE 5.7 renamed the scripted singleton accessor. The pre-5.5 name
    # `get_interchange_manager` is gone; calling it on 5.7 raises
    # `AttributeError: type object 'InterchangeManager' has no attribute
    # 'get_interchange_manager'`. Be tolerant of the old name on legacy
    # 5.x point releases (best-effort) so this script can still help
    # diagnose mismatched UE installs.
    if hasattr(unreal.InterchangeManager, "get_interchange_manager_scripted"):
        return unreal.InterchangeManager.get_interchange_manager_scripted()
    if hasattr(unreal.InterchangeManager, "get_interchange_manager"):
        return unreal.InterchangeManager.get_interchange_manager()
    raise RuntimeError(
        "InterchangeManager has neither get_interchange_manager_scripted "
        "nor get_interchange_manager — the Interchange editor plugin is "
        "missing or this UE build is older than 5.0."
    )


def _normalise_imported_assets(result):
    # `InterchangeManager.import_asset` returns
    # `Array[Object] or None` per the 5.5+ docs, but real UE builds have
    # been seen returning a single object or a result wrapper with an
    # `imported_assets` property. Normalise to a list[Object].
    if result is None:
        return []
    if hasattr(result, "imported_assets"):
        return list(result.imported_assets)
    if isinstance(result, list):
        return result
    return [result]


def _import_via_interchange(gltf_path, target_folder):
    manager = _get_interchange_manager()
    source_data = unreal.InterchangeManager.create_source_data(gltf_path)
    params = _build_import_parameters()
    # First positional argument is the content-path destination
    # (e.g. /Game/REBUS/Imported_<runId>); the source file is conveyed
    # through `source_data`.
    result = manager.import_asset(target_folder, source_data, params)
    return _normalise_imported_assets(result)


def _scan_assets(target_folder):
    # Freshly-imported assets aren't always in the Asset Registry the
    # instant `import_asset` returns. Force a synchronous scan of the
    # destination folder so `list_assets` / `does_asset_exist` see them.
    try:
        registry = unreal.AssetRegistryHelpers.get_asset_registry()
        registry.scan_paths_synchronous([target_folder], force_rescan=True)
    except Exception as ex:  # noqa: BLE001
        _log("asset registry scan skipped: %s" % ex)


def _discover_static_meshes(editor_asset, target_folder):
    # UE 5.7's `InterchangeManager.import_asset(...)` returns a results
    # *container* (or None), NOT the array of created assets — so
    # `_normalise_imported_assets` yields nothing even though the glTF
    # imported a StaticMesh into `target_folder`. That left the level with
    # zero geometry actors (assetCount=0), which is why the streamed frame
    # showed a lit-but-empty scene. Enumerate the destination folder and
    # load the StaticMesh assets Interchange actually wrote.
    _scan_assets(target_folder)

    paths = []
    try:
        if hasattr(editor_asset, "list_assets"):
            paths = editor_asset.list_assets(target_folder, True, False)
        else:
            paths = unreal.EditorAssetLibrary.list_assets(target_folder, True, False)
    except Exception as ex:  # noqa: BLE001
        _log("list_assets failed for %s: %s" % (target_folder, ex))
        paths = []

    static_mesh_cls = getattr(unreal, "StaticMesh", None)
    meshes = []
    for path in paths:
        try:
            asset = unreal.EditorAssetLibrary.load_asset(path)
        except Exception:  # noqa: BLE001
            asset = None
        if asset is None:
            continue
        if static_mesh_cls is None or isinstance(asset, static_mesh_cls):
            meshes.append(asset)
    return meshes


def _get_actor_spawner():
    # EditorActorSubsystem is the 5.x-preferred way to spawn actors into
    # the active editor world; EditorLevelLibrary is the legacy fallback.
    if hasattr(unreal, "EditorActorSubsystem"):
        sub = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
        if sub is not None:
            return sub
    return None


def _spawn_actor_from_class(actor_class, location, rotation):
    sub = _get_actor_spawner()
    if sub is not None and hasattr(sub, "spawn_actor_from_class"):
        return sub.spawn_actor_from_class(actor_class, location, rotation)
    return unreal.EditorLevelLibrary.spawn_actor_from_class(actor_class, location, rotation)


def _static_meshes(imported_assets):
    static_mesh_cls = getattr(unreal, "StaticMesh", None)
    if static_mesh_cls is None:
        return list(imported_assets)
    return [a for a in imported_assets if isinstance(a, static_mesh_cls)]


def _spawn_meshes_into_level(static_meshes):
    # Every mesh is spawned at the world origin with identity transform,
    # so a mesh's *world* bounds equal its asset-local bounding box. That
    # keeps `_compute_bounds` reliable even under -NullRHI (the local box
    # is CPU-side data and doesn't need a rendered frame).
    #
    # IMPORTANT (v0.3.12): do NOT use `spawn_actor_from_object(mesh, …)`.
    # The EditorActorSubsystem object-spawn helper routes through
    # EditorFramework selection/component-visualizer notifications that
    # dereference a null under the headless `PythonScriptCommandlet`
    # (`-NullRHI`, no Slate) — it crashed UE with
    # `EXCEPTION_ACCESS_VIOLATION reading 0x40` in EditorFramework and the
    # commandlet `RequestExitWithStatus(1, 3)`'d out (exit code 3) the
    # instant a real mesh was present to spawn (v0.3.11). Instead spawn a
    # plain `StaticMeshActor` via the class-spawn path (the same one the
    # lights use successfully) and assign the mesh to its component.
    spawned = []
    smc_cls = getattr(unreal, "StaticMeshComponent", None)
    for asset in static_meshes:
        location = unreal.Vector(0.0, 0.0, 0.0)
        rotation = unreal.Rotator(0.0, 0.0, 0.0)
        try:
            actor = _spawn_actor_from_class(unreal.StaticMeshActor, location, rotation)
        except Exception as ex:  # noqa: BLE001
            _log("static mesh actor spawn failed: %s" % ex)
            continue
        if actor is None:
            continue
        comp = None
        try:
            comp = actor.static_mesh_component
        except Exception:  # noqa: BLE001
            comp = None
        if comp is None and smc_cls is not None:
            try:
                comp = actor.get_component_by_class(smc_cls)
            except Exception:  # noqa: BLE001
                comp = None
        if comp is not None:
            try:
                comp.set_static_mesh(asset)
            except Exception as ex:  # noqa: BLE001
                _log("set_static_mesh failed: %s" % ex)
        spawned.append(actor)
    return spawned


def _compute_bounds(static_meshes, spawned_actors):
    # Prefer the StaticMesh asset bounding boxes (always available, even
    # under -NullRHI). Fall back to actor bounds if an asset can't report
    # a box. Returns (center: Vector, radius: float) or None when empty.
    have = False
    min_x = min_y = min_z = 0.0
    max_x = max_y = max_z = 0.0

    def _accumulate(bmin, bmax):
        nonlocal have, min_x, min_y, min_z, max_x, max_y, max_z
        if not have:
            min_x, min_y, min_z = bmin.x, bmin.y, bmin.z
            max_x, max_y, max_z = bmax.x, bmax.y, bmax.z
            have = True
        else:
            min_x = min(min_x, bmin.x)
            min_y = min(min_y, bmin.y)
            min_z = min(min_z, bmin.z)
            max_x = max(max_x, bmax.x)
            max_y = max(max_y, bmax.y)
            max_z = max(max_z, bmax.z)

    for mesh in static_meshes:
        try:
            box = mesh.get_bounding_box()
        except Exception:  # noqa: BLE001
            box = None
        if box is None:
            continue
        try:
            _accumulate(box.min, box.max)
        except Exception:  # noqa: BLE001
            continue

    if not have:
        # Asset boxes unavailable — fall back to actor world bounds.
        for actor in spawned_actors:
            try:
                origin, extent = actor.get_actor_bounds(False)
            except Exception:  # noqa: BLE001
                continue
            bmin = unreal.Vector(origin.x - extent.x, origin.y - extent.y, origin.z - extent.z)
            bmax = unreal.Vector(origin.x + extent.x, origin.y + extent.y, origin.z + extent.z)
            _accumulate(bmin, bmax)

    if not have:
        return None

    center = unreal.Vector(
        (min_x + max_x) * 0.5,
        (min_y + max_y) * 0.5,
        (min_z + max_z) * 0.5,
    )
    dx = (max_x - min_x) * 0.5
    dy = (max_y - min_y) * 0.5
    dz = (max_z - min_z) * 0.5
    radius = math.sqrt(dx * dx + dy * dy + dz * dz)
    return center, radius


def _set_movable(actor):
    try:
        root = actor.root_component
        if root is not None and hasattr(root, "set_mobility"):
            root.set_mobility(unreal.ComponentMobility.MOVABLE)
    except Exception as ex:  # noqa: BLE001
        _log("set_mobility failed: %s" % ex)


def _set_props(target, props):
    for name, value in props:
        try:
            target.set_editor_property(name, value)
        except Exception:  # noqa: BLE001
            # Property name drifts across UE point releases; tolerate.
            pass


def _add_lighting():
    # Directional sun + Sky Atmosphere + ambient Sky Light. This mirrors
    # the UE "Basic" daylight template, which is correctly exposed under
    # default auto-exposure. The previously-blank Imported_<runId> level
    # had zero lights, so the streamed frame was black regardless of
    # camera framing.
    added = []

    # Directional light (sun). Movable so the -game launch lights the
    # scene without a baked lighting build.
    try:
        sun = _spawn_actor_from_class(
            unreal.DirectionalLight,
            unreal.Vector(0.0, 0.0, 1000.0),
            unreal.Rotator(-48.0, -30.0, 0.0))
        if sun is not None:
            _set_movable(sun)
            comp = None
            try:
                comp = sun.get_component_by_class(unreal.DirectionalLightComponent)
            except Exception:  # noqa: BLE001
                comp = getattr(sun, "directional_light_component", None)
            if comp is not None:
                _set_props(comp, [
                    ("intensity", 10.0),
                    ("atmosphere_sun_light", True),
                ])
            added.append("DirectionalLight")
    except Exception as ex:  # noqa: BLE001
        _log("directional light spawn failed: %s" % ex)

    # Sky atmosphere — gives a bright sky so auto-exposure always has a
    # well-lit reference and the sun reads as daylight.
    try:
        if hasattr(unreal, "SkyAtmosphere"):
            atmo = _spawn_actor_from_class(
                unreal.SkyAtmosphere,
                unreal.Vector(0.0, 0.0, 0.0),
                unreal.Rotator(0.0, 0.0, 0.0))
            if atmo is not None:
                added.append("SkyAtmosphere")
    except Exception as ex:  # noqa: BLE001
        _log("sky atmosphere spawn failed: %s" % ex)

    # Sky light (ambient fill). Real-time capture so it re-captures the
    # atmosphere at runtime (movable, no baked capture needed).
    try:
        sky = _spawn_actor_from_class(
            unreal.SkyLight,
            unreal.Vector(0.0, 0.0, 1000.0),
            unreal.Rotator(0.0, 0.0, 0.0))
        if sky is not None:
            _set_movable(sky)
            comp = None
            try:
                comp = sky.get_component_by_class(unreal.SkyLightComponent)
            except Exception:  # noqa: BLE001
                comp = getattr(sky, "sky_light_component", None)
            if comp is not None:
                _set_props(comp, [
                    ("real_time_capture", True),
                    ("intensity", 1.0),
                ])
            added.append("SkyLight")
    except Exception as ex:  # noqa: BLE001
        _log("sky light spawn failed: %s" % ex)

    return added


def _add_post_process():
    # Unbound PostProcessVolume that clamps auto-exposure adaptation so an
    # empty/dark frame can't crush to black and a bright sky can't blow the
    # model out. The min/max band is sane whether the project expresses
    # exposure in EV100 (Extend Default Luminance Range on) or cd/m2 (off).
    # Best-effort: if the override flags aren't bindable on this UE build,
    # the lit scene + default auto-exposure already produces a visible
    # frame, so this is a safety net rather than the primary fix.
    try:
        ppv = _spawn_actor_from_class(
            unreal.PostProcessVolume,
            unreal.Vector(0.0, 0.0, 0.0),
            unreal.Rotator(0.0, 0.0, 0.0))
        if ppv is None:
            return False
        _set_props(ppv, [("unbound", True)])
        try:
            settings = ppv.get_editor_property("settings")
            _set_props(settings, [
                ("auto_exposure_min_brightness", 0.5),
                ("auto_exposure_max_brightness", 2.0),
                ("auto_exposure_bias", 1.0),
            ])
            # Enable the corresponding override flags so the volume
            # actually applies the clamped values. Property naming varies
            # across UE point releases; try both conventions.
            for flag in (
                "override_auto_exposure_min_brightness",
                "override_auto_exposure_max_brightness",
                "override_auto_exposure_bias",
                "b_override_auto_exposure_min_brightness",
                "b_override_auto_exposure_max_brightness",
                "b_override_auto_exposure_bias",
            ):
                try:
                    settings.set_editor_property(flag, True)
                except Exception:  # noqa: BLE001
                    pass
            ppv.set_editor_property("settings", settings)
        except Exception as ex:  # noqa: BLE001
            _log("post-process exposure tune skipped: %s" % ex)
        return True
    except Exception as ex:  # noqa: BLE001
        _log("post-process volume spawn failed: %s" % ex)
        return False


def _frame_view(center, radius):
    # Place a camera at an orbit distance from the bounds centre, looking
    # at it, and a PlayerStart at the same transform. The CameraActor is
    # auto-activated for player 0 so the Phase F `-game` launch streams it;
    # the PlayerStart eliminates the `NO PLAYERSTART` warning and gives the
    # default pawn a sane spawn pointing at the model if camera
    # auto-activation is ever unavailable.
    if radius is None or radius <= 1.0:
        # Degenerate / empty bounds — frame a ~2 m sphere at the centre.
        radius = 200.0

    # Orbit offset direction in UE space (X forward, Y right, Z up).
    dx, dy, dz = 1.0, -1.0, 0.6
    dlen = math.sqrt(dx * dx + dy * dy + dz * dz)
    dx, dy, dz = dx / dlen, dy / dlen, dz / dlen
    dist = radius * 2.5

    cam_loc = unreal.Vector(
        center.x + dx * dist,
        center.y + dy * dist,
        center.z + dz * dist,
    )
    try:
        cam_rot = unreal.MathLibrary.find_look_at_rotation(cam_loc, center)
    except Exception:  # noqa: BLE001
        cam_rot = unreal.Rotator(-20.0, 135.0, 0.0)

    try:
        cam = _spawn_actor_from_class(unreal.CameraActor, cam_loc, cam_rot)
        if cam is not None:
            _set_props(cam, [
                ("find_camera_component_when_view_target", True),
                ("auto_activate_for_player", unreal.AutoReceiveInput.PLAYER0),
            ])
            try:
                cc = cam.camera_component
                if cc is not None:
                    _set_props(cc, [("field_of_view", 50.0)])
            except Exception:  # noqa: BLE001
                pass
    except Exception as ex:  # noqa: BLE001
        _log("camera spawn failed: %s" % ex)

    try:
        _spawn_actor_from_class(unreal.PlayerStart, cam_loc, cam_rot)
    except Exception as ex:  # noqa: BLE001
        _log("player start spawn failed: %s" % ex)

    return cam_loc, cam_rot


def _new_level(level_path):
    # UE 5.x exposes LevelEditorSubsystem.new_level for headless level
    # creation; fall back to EditorLevelLibrary on older point releases.
    level_subsystem = None
    if hasattr(unreal, "LevelEditorSubsystem"):
        level_subsystem = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)
    if level_subsystem is not None and hasattr(level_subsystem, "new_level"):
        level_subsystem.new_level(level_path)
        return level_subsystem
    unreal.EditorLevelLibrary.new_level(level_path)
    return None


def _save_current_level(level_subsystem):
    if level_subsystem is not None and hasattr(level_subsystem, "save_current_level"):
        level_subsystem.save_current_level()
        return
    unreal.EditorLevelLibrary.save_current_level()


def main():
    start = time.time()
    try:
        editor_asset = _get_editor_asset_library()
        _ensure_directory(editor_asset, TARGET_FOLDER)

        # Idempotent re-runs: blow away any prior import for this run.
        if hasattr(editor_asset, "delete_directory") and editor_asset.does_directory_exist(TARGET_FOLDER):
            try:
                editor_asset.delete_directory(TARGET_FOLDER)
            except Exception:  # noqa: BLE001
                pass
            editor_asset.make_directory(TARGET_FOLDER)

        # Interchange is required: the only fallback (AssetImportTask)
        # is Slate-bound and crashes a `-NullRHI` commandlet. Any
        # Interchange failure bubbles up to the structured-error emit
        # below, which is the correct surface for the orchestrator.
        imported_assets = _import_via_interchange(GLTF_PATH, TARGET_FOLDER)
        static_meshes = _static_meshes(imported_assets)
        # UE 5.7's import_asset returns a results container, not the asset
        # array, so `static_meshes` is typically empty here even on a
        # successful import. Discover the StaticMesh assets Interchange
        # actually wrote into TARGET_FOLDER so the geometry gets spawned.
        if not static_meshes:
            static_meshes = _discover_static_meshes(editor_asset, TARGET_FOLDER)
            _log("discovered %d static mesh asset(s) in %s after import"
                 % (len(static_meshes), TARGET_FOLDER))

        level_path = "/Game/REBUS/Maps/" + LEVEL_NAME
        if editor_asset.does_asset_exist(level_path):
            try:
                editor_asset.delete_asset(level_path)
            except Exception:  # noqa: BLE001
                pass

        level_subsystem = _new_level(level_path)
        spawned = _spawn_meshes_into_level(static_meshes)
        actor_count = len(spawned)

        # Make the streamed frame show a lit, in-frame model. The level
        # created above is blank (no lights / sky / camera / PlayerStart),
        # which is why the v0.3.9 stream rendered solid black.
        lights = _add_lighting()
        _add_post_process()

        bounds = _compute_bounds(static_meshes, spawned)
        if bounds is not None:
            center, radius = bounds
            _log("imported bounds center=(%.1f,%.1f,%.1f) radius=%.1f meshes=%d actors=%d"
                 % (center.x, center.y, center.z, radius, len(static_meshes), actor_count))
        else:
            center, radius = unreal.Vector(0.0, 0.0, 0.0), None
            _log("no mesh/actor bounds available; framing camera at origin fallback")

        cam_loc, cam_rot = _frame_view(center, radius)
        _log("framing camera loc=(%.1f,%.1f,%.1f) rot=(pitch=%.1f,yaw=%.1f,roll=%.1f) lights=[%s]"
             % (cam_loc.x, cam_loc.y, cam_loc.z,
                cam_rot.pitch, cam_rot.yaw, cam_rot.roll, ",".join(lights)))

        _save_current_level(level_subsystem)

        elapsed = time.time() - start
        _emit_ready(level_path, actor_count, elapsed)
        sys.exit(0)
    except Exception as ex:  # noqa: BLE001
        _emit_error("import_failed", str(ex))
        sys.exit(1)


if __name__ == "__main__":
    main()
