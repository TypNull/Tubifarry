import logging
import os
import time

_AUDIO_EXTENSIONS = {".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".alac", ".ape", ".wv", ".aiff"}


class _ProblemCollector(logging.Handler):
    def __init__(self, strict):
        super().__init__(logging.WARNING)
        self.strict = strict
        self.messages = []

    def emit(self, record):
        message = record.getMessage()
        if record.levelno >= logging.ERROR or (self.strict and "error loading plugin" in message):
            self.messages.append(message.lstrip("* "))


def _run(params, command, strict=False):
    import beets.ui

    arguments = ["-l", params["library"]]
    if params.get("config"):
        arguments += ["-c", params["config"]]
    arguments += command

    collector = _ProblemCollector(strict)
    beets_logger = logging.getLogger("beets")
    original_logging = getattr(beets.ui, "_bootstrap_logging", None)

    if original_logging is None:
        beets_logger.addHandler(logging.StreamHandler())
        beets_logger.addHandler(collector)
    else:
        def bootstrap_logging():
            original_logging()
            beets_logger.addHandler(collector)

        beets.ui._bootstrap_logging = bootstrap_logging

    try:
        beets.ui.main(arguments)
    except SystemExit as exit_signal:
        code = exit_signal.code
        if code not in (None, 0):
            details = "; ".join(collector.messages) or f"beets exited with {code}"
            raise RuntimeError(details) from None
    finally:
        beets_logger.removeHandler(collector)
        if original_logging is not None:
            beets.ui._bootstrap_logging = original_logging

    if strict and collector.messages:
        raise RuntimeError("; ".join(collector.messages))


def check(params, context):
    import beets

    _run(params, ["version"], strict=True)
    return {"version": beets.__version__}


def _audio_files(path):
    if os.path.isfile(path):
        return [path]
    return [os.path.join(folder, name) for folder, _, names in os.walk(path) for name in names if os.path.splitext(name)[1].lower() in _AUDIO_EXTENSIONS]


def _has_hardlinks(path):
    if os.path.isfile(path):
        return os.stat(path).st_nlink > 1
    for folder, _, files in os.walk(path):
        for name in files:
            if os.stat(os.path.join(folder, name)).st_nlink > 1:
                return True
    return False


def _keep_files_in_place(extra, match=None, plugins=None):
    import beets
    import beets.ui

    original = getattr(beets.ui, "_bootstrap_config", None)
    if original is None:
        raise RuntimeError(f"beets {beets.__version__} is not supported: its configuration loader changed")

    def bootstrap(options):
        error = original(options)
        beets.config["import"].set({
            "copy": False,
            "move": False,
            "link": False,
            "hardlink": False,
            "reflink": False,
            "delete": False,
            "incremental": False,
            "singletons": False,
            "duplicate_action": "skip",
            **extra,
        })
        if match:
            beets.config["match"].set(match)
        if plugins:
            enabled = beets.config["plugins"].as_str_seq()
            beets.config["plugins"].set(list(dict.fromkeys([*enabled, *plugins])))
        return error

    beets.ui._bootstrap_config = bootstrap
    return original


def import_album(params, context):
    import beets
    import beets.ui
    from beets.dbcore.query import PathQuery
    from beets.library import Library

    started = time.time()
    hardlinked = _has_hardlinks(params["path"])
    original = _keep_files_in_place({"write": False} if hardlinked or not params.get("write", True) else {})
    try:
        _run(params, ["import", "-q", "-C", "-M", params["path"]])
    finally:
        beets.ui._bootstrap_config = original

    library = Library(params["library"], beets.config["directory"].as_filename())
    try:
        items = library.items(PathQuery("path", params["path"]))
        added = [item for item in items if item.added >= started]
        if hardlinked or not params.get("write", True):
            for item in added:
                _remember_pending(item, _differences(item))
        count = len(items)
    finally:
        library._close()

    return {"path": params["path"], "items": count, "added": len(added), "hardlinked": hardlinked}


_PENDING = "tubifarry_pending"


def _differences(item):
    from beets.library import Item

    try:
        clean = Item.from_path(item.path)
    except Exception:
        return set()
    return {field for field in Item._media_tag_fields if item.get(field) != clean.get(field)}


def _pending(item):
    return {field for field in str(item.get(_PENDING) or "").split(",") if field}


def _remember_pending(item, fields):
    value = ",".join(sorted(fields))
    if value != (item.get(_PENDING) or ""):
        item[_PENDING] = value
        item.store()


def _fingerprint(paths, release_tracks):
    if not os.environ.get("FPCALC"):
        return {}

    try:
        import acoustid
        from beetsplug.chroma import API_KEY
    except ImportError:
        return {}

    log = logging.getLogger("beets")
    candidates = set(release_tracks)
    confirmed = {}
    deadline = time.monotonic() + 180
    failures = 0
    for path in paths:
        if failures >= 3 or time.monotonic() > deadline:
            log.warning("fingerprinting stopped early, continuing with tags only")
            break
        try:
            duration, fingerprint = acoustid.fingerprint_file(path)
            response = acoustid.lookup(API_KEY, fingerprint, duration, meta="recordings releaseids", timeout=15)
            failures = 0
        except Exception as error:
            failures += 1
            log.warning("fingerprinting {} failed: {}", path, error)
            continue

        supported = {}
        for result in response.get("results") or []:
            if result.get("score", 0) < 0.5:
                continue
            for recording in result.get("recordings") or []:
                for release in recording.get("releases") or []:
                    if release["id"] in candidates:
                        supported.setdefault(release["id"], set()).add(recording["id"])
        if supported:
            confirmed[path] = supported
    return confirmed


def _fully_confirmed_release(paths, confirmed, release_tracks):
    for release, tracks in release_tracks.items():
        recordings = [confirmed[path][release] for path in paths if release in confirmed.get(path, {})]
        unique = [next(iter(options)) for options in recordings if len(options) == 1]
        if len(unique) == len(paths) == tracks and len(set(unique)) == len(unique):
            return release
    return None


def _recordings_to_write(confirmed, pinned):
    recordings = {}
    for path, supported in confirmed.items():
        options = supported.get(pinned, set()) if pinned else set().union(*supported.values())
        if len(options) == 1:
            recordings[path] = next(iter(options))
    return recordings


def _write_recording_ids(recordings):
    from mediafile import MediaFile

    previous = {}
    for path, recording in recordings.items():
        try:
            media = MediaFile(path)
            if media.mb_trackid == recording:
                continue
            previous[path] = media.mb_trackid
            media.mb_trackid = recording
            media.save()
        except Exception:
            previous.pop(path, None)
    return previous


def _restore_recording_ids(previous):
    from mediafile import MediaFile

    for path, value in previous.items():
        try:
            media = MediaFile(path)
            if value:
                media.mb_trackid = value
            else:
                delattr(media, "mb_trackid")
            media.save()
        except Exception:
            pass


def _guard_fingerprints(confirmed, rejected):
    from beets.importer import Action
    from beets.plugins import BeetsPlugin

    expected = {os.path.realpath(path): supported for path, supported in confirmed.items()}

    def conflicts_with(item, track, release):
        supported = expected.get(os.path.realpath(os.fsdecode(item.path)))
        return supported is not None and track.track_id not in supported.get(release, set())

    def guard(session=None, task=None, **_):
        match = getattr(task, "match", None)
        if task is None or match is None or task.choice_flag is not Action.APPLY:
            return
        conflicts = [os.fsdecode(item.path) for item, track in match.mapping.items() if conflicts_with(item, track, match.info.album_id)]
        if conflicts:
            rejected.extend(conflicts)
            task.set_choice(Action.SKIP)

    BeetsPlugin.listeners["import_task_choice"].append(guard)
    return guard


def retag(params, context):
    import tempfile

    import beets
    import beets.ui
    from beets.library import Library
    from mediafile import MediaFile

    path = params["path"]
    release_tracks = {release["id"]: int(release.get("tracks") or 0) for release in params["releases"]}
    releases = list(release_tracks)
    audio_files = _audio_files(path)

    if _has_hardlinks(path):
        return {"retagged": 0, "files": len(audio_files), "release": None, "fingerprinted": 0, "hardlinked": True}

    confirmed = _fingerprint(audio_files, release_tracks)
    pinned = _fully_confirmed_release(audio_files, confirmed, release_tracks)

    if pinned:
        search, match = [pinned], {"strong_rec_thresh": 1.0, "distance_weights": {"track_id": 100.0}}
    else:
        search, match = releases, {"strong_rec_thresh": 0.25, "distance_weights": {"album": 0.0, "artist": 0.0}}

    previous = _write_recording_ids(_recordings_to_write(confirmed, pinned))
    written = {}
    rejected = []
    guard = _guard_fingerprints(confirmed, rejected)
    try:
        with tempfile.TemporaryDirectory(prefix="tubifarry-beets-") as temporary:
            library_path = os.path.join(temporary, "retag.db")
            original = _keep_files_in_place({"write": True, "autotag": True, "timid": False, "quiet_fallback": "skip"}, match)
            try:
                arguments = [argument for release in search for argument in ("--search-id", release)]
                _run(dict(params, library=library_path), ["import", "-q", "-C", "-M", *arguments, path])
            finally:
                beets.ui._bootstrap_config = original

            if os.path.exists(library_path):
                library = Library(library_path, beets.config["directory"].as_filename())
                try:
                    imported = [os.fsdecode(item.path) for item in library.items() if item.mb_albumid in releases]
                finally:
                    library._close()

                for item_path in imported:
                    try:
                        media = MediaFile(item_path)
                    except Exception:
                        continue
                    if media.mb_albumid in releases:
                        written[os.path.realpath(item_path)] = (media.mb_albumid, media.mb_trackid)
    finally:
        from beets.plugins import BeetsPlugin

        BeetsPlugin.listeners["import_task_choice"].remove(guard)
        mismatched = [
            file for file, supported in confirmed.items()
            if os.path.realpath(file) in written and written[os.path.realpath(file)][1] not in supported.get(written[os.path.realpath(file)][0], set())
        ]
        _restore_recording_ids({file: value for file, value in previous.items() if mismatched or os.path.realpath(file) not in written})

    matched = [album_id for album_id, _ in written.values()]
    release = max(set(matched), key=matched.count) if matched and not mismatched else None

    return {
        "retagged": matched.count(release) if release else 0,
        "files": len(audio_files),
        "release": release,
        "fingerprinted": len(confirmed),
        "pinned": pinned is not None,
        "mismatched": len(mismatched) + len(rejected),
        "hardlinked": False,
    }


def _open_library(params):
    import beets
    from beets.library import Library

    beets.config.read(user=True, defaults=True)
    if params.get("config"):
        beets.config.set_file(params["config"])
    return Library(params["library"], beets.config["directory"].as_filename())


def known_folders(params, context):
    if not os.path.exists(params["library"]):
        return {"folders": []}

    library = _open_library(params)
    try:
        folders = set()
        for item in library.items():
            folder = os.path.dirname(os.fsdecode(item.path))
            folders.update((folder, os.path.dirname(folder)))
    finally:
        library._close()

    return {"folders": sorted(folders)}


def _snapshot(params):
    import json

    from beets.library import Item

    snapshot_path = params["library"] + ".tubifarry-sync.json"
    if os.path.exists(snapshot_path):
        try:
            with open(snapshot_path, encoding="utf-8") as file:
                return snapshot_path, {int(key): value for key, value in json.load(file).items()}
        except (OSError, ValueError):
            pass

    library = _open_library(params)
    try:
        snapshot = {item.id: {field: item.get(field) for field in Item._media_tag_fields} for item in library.items()}
    finally:
        library._close()

    with open(snapshot_path, "w", encoding="utf-8") as file:
        json.dump(snapshot, file, default=str)
    with open(snapshot_path, encoding="utf-8") as file:
        return snapshot_path, {int(key): value for key, value in json.load(file).items()}


def sync(params, context):
    import beets
    import beets.ui
    from beets.library import Item
    from mediafile import MediaFile

    if not os.path.exists(params["library"]):
        return {"items": 0, "written": 0, "hardlinked": 0}

    snapshot_path, before = _snapshot(params)

    original = _keep_files_in_place({}, plugins=["mbsync"])
    try:
        _run(params, ["mbsync", "-M", "-W"])
    finally:
        beets.ui._bootstrap_config = original

    library = _open_library(params)
    items = written = hardlinked = 0
    try:
        id3v23 = beets.config["id3v23"].get(bool)
        for item in library.items():
            path = os.fsdecode(item.path)
            previous = before.get(item.id, {})
            fields = {field for field in Item._media_tag_fields if field in previous and item.get(field) != previous[field]} | _pending(item)
            if not os.path.exists(path):
                if fields:
                    _remember_pending(item, fields)
                continue
            items += 1
            if not fields:
                continue
            if os.stat(path).st_nlink > 1:
                hardlinked += 1
                _remember_pending(item, fields)
                continue
            try:
                media = MediaFile(path, id3v23=id3v23)
                media.update({field: item.get(field) for field in fields})
                media.save()
                written += 1
                _remember_pending(item, set())
            except Exception as error:
                _remember_pending(item, fields)
                logging.getLogger("beets").warning("writing {} failed: {}", path, error)
    finally:
        library._close()

    os.remove(snapshot_path)
    return {"items": items, "written": written, "hardlinked": hardlinked}
