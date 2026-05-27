# PRISM Visualiser — UE 5.7 DMX plugin MVR / GDTF import driver
#
# Lint-checkable twin of import_mvr.py.in. The actual per-run script the
# orchestrator hands to UE is rendered from the .in template; this file
# exists so editors / CI can parse it without a UE install. Keep the two
# files structurally identical (placeholders preserved literally).

import json
import sys
import time

import unreal  # noqa: F401 - provided by UE's PythonScriptPlugin

RUN_ID = "{{RUN_ID}}"
MVR_PATHS_JSON = r"""{{MVR_PATHS_JSON}}"""
GDTF_PATHS_JSON = r"""{{GDTF_PATHS_JSON}}"""
TARGET_FOLDER = "{{TARGET_FOLDER}}"
LEVEL_NAME = "{{LEVEL_NAME}}"


def _emit_ready(gdtf_count, mvr_count, elapsed_s):
    payload = {
        "runId": RUN_ID,
        "gdtfCount": int(gdtf_count),
        "mvrCount": int(mvr_count),
        "importDurationMs": int(elapsed_s * 1000),
    }
    print("PRISM_VISUALISER_MVR_READY " + json.dumps(payload), flush=True)


def _emit_error(code, message):
    payload = {"code": code, "message": message}
    print("PRISM_VISUALISER_MVR_ERROR " + json.dumps(payload), flush=True)


def _ensure_directory(target_folder):
    get_editor_subsystem = getattr(unreal, "get_editor_subsystem", None)
    subsystem_class = getattr(unreal, "EditorAssetSubsystem", None)
    editor_asset = None
    if get_editor_subsystem is not None and subsystem_class is not None:
        editor_asset = get_editor_subsystem(subsystem_class)
    if editor_asset is None:
        editor_asset = unreal.EditorAssetLibrary
    if hasattr(editor_asset, "does_directory_exist"):
        if not editor_asset.does_directory_exist(target_folder):
            editor_asset.make_directory(target_folder)
    else:
        editor_asset.make_directory(target_folder)


def _import_gdtf_via_factory(gdtf_path, destination):
    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()
    factory_cls = (
        getattr(unreal, "DMXImportGDTFFactory", None)
        or getattr(unreal, "DMXGDTFImportFactory", None)
        or getattr(unreal, "UDMXGDTFImportFactory", None)
    )
    if factory_cls is None:
        raise RuntimeError(
            "DMX plugin GDTF import factory not found. "
            "Confirm the DMX plugin is enabled in the UE template."
        )
    factory = factory_cls()
    task = unreal.AssetImportTask()
    task.set_editor_property("filename", gdtf_path)
    task.set_editor_property("destination_path", destination)
    task.set_editor_property("replace_existing", True)
    task.set_editor_property("automated", True)
    task.set_editor_property("save", True)
    task.set_editor_property("factory", factory)
    asset_tools.import_asset_tasks([task])
    imported = list(task.get_editor_property("imported_object_paths") or [])
    if not imported:
        unreal.log_warning(
            "GDTF import returned no assets for %s — the plugin may have "
            "rejected the file (older spec version or malformed XML)."
            % gdtf_path
        )
    return imported


def _import_mvr_via_factory(mvr_path, world):
    mvr_factory_cls = (
        getattr(unreal, "DMXImportMVRFactory", None)
        or getattr(unreal, "DMXMVRImportFactory", None)
        or getattr(unreal, "UDMXMVRImportFactory", None)
    )
    if mvr_factory_cls is not None and hasattr(mvr_factory_cls, "import_mvr_to_world"):
        mvr_factory_cls.import_mvr_to_world(world, mvr_path)
        return True

    actor_cls = (
        getattr(unreal, "DMXMVRSceneActor", None)
        or getattr(unreal, "ADMXMVRSceneActor", None)
    )
    if actor_cls is None:
        raise RuntimeError(
            "DMX plugin MVR import surface not found "
            "(neither DMXImportMVRFactory.import_mvr_to_world nor DMXMVRSceneActor). "
            "Confirm the DMX plugin is enabled in the UE template."
        )

    spawner_subsystem = None
    if hasattr(unreal, "EditorActorSubsystem"):
        spawner_subsystem = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
    location = unreal.Vector(0.0, 0.0, 0.0)
    rotation = unreal.Rotator(0.0, 0.0, 0.0)
    if spawner_subsystem is not None and hasattr(spawner_subsystem, "spawn_actor_from_class"):
        scene_actor = spawner_subsystem.spawn_actor_from_class(actor_cls, location, rotation)
    else:
        scene_actor = unreal.EditorLevelLibrary.spawn_actor_from_class(
            actor_cls, location, rotation
        )
    if scene_actor is None:
        raise RuntimeError("Could not spawn DMXMVRSceneActor for MVR import.")
    if not hasattr(scene_actor, "import_mvr_archive"):
        raise RuntimeError(
            "Spawned DMXMVRSceneActor has no import_mvr_archive method "
            "(UE 5.7 DMX plugin API surface drifted). "
            "Artist should validate v1.0.0-ue5.7 template against the installed plugin."
        )
    scene_actor.import_mvr_archive(mvr_path)
    return True


def _save_current_level():
    level_subsystem = None
    if hasattr(unreal, "LevelEditorSubsystem"):
        level_subsystem = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)
    if level_subsystem is not None and hasattr(level_subsystem, "save_current_level"):
        level_subsystem.save_current_level()
        return
    unreal.EditorLevelLibrary.save_current_level()


def _get_editor_world():
    if hasattr(unreal, "UnrealEditorSubsystem"):
        editor_subsystem = unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem)
        if editor_subsystem is not None and hasattr(editor_subsystem, "get_editor_world"):
            world = editor_subsystem.get_editor_world()
            if world is not None:
                return world
    return unreal.EditorLevelLibrary.get_editor_world()


def main():
    start = time.time()
    try:
        mvr_paths = json.loads(MVR_PATHS_JSON) if MVR_PATHS_JSON else []
        gdtf_paths = json.loads(GDTF_PATHS_JSON) if GDTF_PATHS_JSON else []

        if not isinstance(mvr_paths, list) or not isinstance(gdtf_paths, list):
            raise RuntimeError(
                "MVR_PATHS_JSON / GDTF_PATHS_JSON must decode to JSON arrays."
            )

        if not mvr_paths and not gdtf_paths:
            _emit_ready(0, 0, time.time() - start)
            sys.exit(0)

        _ensure_directory(TARGET_FOLDER + "/GDTF")

        gdtf_imported = 0
        for gdtf_path in gdtf_paths:
            try:
                _import_gdtf_via_factory(gdtf_path, TARGET_FOLDER + "/GDTF")
                gdtf_imported += 1
            except Exception as gdtf_ex:  # noqa: BLE001
                unreal.log_warning(
                    "GDTF import failed for %s: %s" % (gdtf_path, str(gdtf_ex))
                )

        world = _get_editor_world()
        if world is None and mvr_paths:
            raise RuntimeError("Editor world unavailable; cannot import MVR scene(s).")
        mvr_imported = 0
        for mvr_path in mvr_paths:
            try:
                _import_mvr_via_factory(mvr_path, world)
                mvr_imported += 1
            except Exception as mvr_ex:  # noqa: BLE001
                unreal.log_warning(
                    "MVR import failed for %s: %s" % (mvr_path, str(mvr_ex))
                )

        if mvr_imported > 0:
            try:
                _save_current_level()
            except Exception as save_ex:  # noqa: BLE001
                unreal.log_warning("save_current_level failed: %s" % str(save_ex))

        elapsed = time.time() - start
        _emit_ready(gdtf_imported, mvr_imported, elapsed)
        sys.exit(0)
    except Exception as ex:  # noqa: BLE001
        _emit_error("mvr_import_failed", str(ex))
        sys.exit(1)


if __name__ == "__main__":
    main()
