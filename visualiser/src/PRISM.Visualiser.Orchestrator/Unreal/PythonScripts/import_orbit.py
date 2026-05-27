# This is the unrendered template. ProjectScaffolder writes the rendered version to the per-run project copy.
# PRISM Visualiser — UE 5.7 Interchange import driver
#
# This file is a TEMPLATE rendered per-run by ProjectScaffolder.cs.
# Placeholders ({{RUN_ID}}, {{GLTF_PATH}}, {{TARGET_FOLDER}},
# {{LEVEL_NAME}}) are substituted before the script reaches the editor.
# The orchestrator invokes the rendered version via:
#
#     UnrealEditor-Cmd.exe REBUSVis.uproject -run=PythonScript \
#         -ExecutePythonScript=<path-to-rendered-import_orbit.py>
#
# Contract with the orchestrator:
#   * Emit exactly one PRISM_VISUALISER_READY <json> line on success.
#   * Emit exactly one PRISM_VISUALISER_ERROR <json> line on failure
#     and sys.exit(1).
#   * No other line on stdout matters to the launcher's marker parser,
#     but every line is forwarded to the orchestrator's Serilog under
#     the "ue-editor" channel.
#
# Phase E note: BP_OrbitImporter.uasset (the Blueprint wrapper Phase D
# would have shipped) does not yet exist in the v0.1.0-ue5.7-scaffold
# template. We therefore use the Interchange Python API directly. Once
# the artist-populated v1.0.0-ue5.7 release lands, prefer the BP
# wrapper's `BP_OrbitImporter.RunImport(GLTF_PATH, TARGET_FOLDER)`
# entrypoint; the public stdout contract is unchanged.

import json
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


def _build_import_parameters(target_folder):
    # UE 5.7's Interchange exposes ImportAssetParameters on the
    # AssetTools surface. Construct a default parameters object and
    # tighten the fields we care about.
    params = unreal.ImportAssetParameters()
    setattr(params, "is_automated", True)
    setattr(params, "replace_existing", True)
    setattr(params, "destination_path", target_folder)
    return params


def _import_via_interchange(gltf_path, target_folder):
    # InterchangeManager.import_asset handles the source-data
    # construction internally given a file path; some 5.7 point
    # releases expose `import_asset_with_params` with a separate
    # parameters object, which we prefer when present for explicit
    # is_automated / replace_existing semantics.
    interchange_manager = unreal.InterchangeManager.get_interchange_manager()

    if hasattr(interchange_manager, "import_asset_with_params"):
        params = _build_import_parameters(target_folder)
        result = interchange_manager.import_asset_with_params(gltf_path, params)
    else:
        # Fall back to the older API. The destination_path is conveyed
        # via the source data's destination convention.
        params = _build_import_parameters(target_folder)
        result = interchange_manager.import_asset(gltf_path, params)

    # The Interchange API returns an iterable of imported assets in
    # most builds; some return a single result object with an
    # `imported_assets` property. Normalise.
    if hasattr(result, "imported_assets"):
        return list(result.imported_assets)
    if isinstance(result, list):
        return result
    if result is None:
        return []
    return [result]


def _import_via_asset_task(gltf_path, target_folder):
    # Fallback path used when InterchangeManager is unavailable (e.g.
    # the engine build was compiled without the Interchange editor
    # plugin). Drives the legacy AssetImportTask + AssetTools pipeline.
    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()
    task = unreal.AssetImportTask()
    task.set_editor_property("filename", gltf_path)
    task.set_editor_property("destination_path", target_folder)
    task.set_editor_property("automated", True)
    task.set_editor_property("replace_existing", True)
    task.set_editor_property("save", True)
    asset_tools.import_asset_tasks([task])
    imported_paths = list(task.get_editor_property("imported_object_paths") or [])
    editor_asset = _get_editor_asset_library()
    out = []
    for path in imported_paths:
        try:
            obj = editor_asset.load_asset(path)
            if obj is not None:
                out.append(obj)
        except Exception:  # noqa: BLE001 - tolerate per-asset load failures
            continue
    return out


def _spawn_meshes_into_level(imported_assets):
    actor_count = 0
    static_mesh_cls = getattr(unreal, "StaticMesh", None)
    spawner_subsystem = None
    if hasattr(unreal, "EditorActorSubsystem"):
        spawner_subsystem = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)

    for asset in imported_assets:
        if static_mesh_cls is not None and not isinstance(asset, static_mesh_cls):
            continue
        location = unreal.Vector(0.0, 0.0, 0.0)
        rotation = unreal.Rotator(0.0, 0.0, 0.0)
        if spawner_subsystem is not None and hasattr(spawner_subsystem, "spawn_actor_from_object"):
            actor = spawner_subsystem.spawn_actor_from_object(asset, location, rotation)
        else:
            actor = unreal.EditorLevelLibrary.spawn_actor_from_object(asset, location, rotation)
        if actor is not None:
            actor_count += 1
    return actor_count


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

        try:
            imported_assets = _import_via_interchange(GLTF_PATH, TARGET_FOLDER)
        except Exception as interchange_ex:  # noqa: BLE001
            unreal.log_warning(
                "Interchange import failed; falling back to AssetImportTask: %s"
                % str(interchange_ex)
            )
            imported_assets = _import_via_asset_task(GLTF_PATH, TARGET_FOLDER)

        level_path = "/Game/REBUS/Maps/" + LEVEL_NAME
        if editor_asset.does_asset_exist(level_path):
            try:
                editor_asset.delete_asset(level_path)
            except Exception:  # noqa: BLE001
                pass

        level_subsystem = _new_level(level_path)
        actor_count = _spawn_meshes_into_level(imported_assets)
        _save_current_level(level_subsystem)

        elapsed = time.time() - start
        _emit_ready(level_path, actor_count, elapsed)
        sys.exit(0)
    except Exception as ex:  # noqa: BLE001
        _emit_error("import_failed", str(ex))
        sys.exit(1)


if __name__ == "__main__":
    main()
