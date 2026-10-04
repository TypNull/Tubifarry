import logging
import os
import time


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


def _has_hardlinks(path):
    for folder, _, files in os.walk(path):
        for name in files:
            if os.stat(os.path.join(folder, name)).st_nlink > 1:
                return True
    return False


def _keep_files_in_place(write_tags):
    import beets
    import beets.ui

    original = getattr(beets.ui, "_bootstrap_config", None)
    if original is None:
        raise RuntimeError(f"beets {beets.__version__} is not supported: its configuration loader changed")

    def bootstrap(options):
        error = original(options)
        overrides = {
            "copy": False,
            "move": False,
            "link": False,
            "hardlink": False,
            "reflink": False,
            "delete": False,
            "duplicate_action": "skip",
        }
        if not write_tags:
            overrides["write"] = False
        beets.config["import"].set(overrides)
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
    original = _keep_files_in_place(not hardlinked)
    try:
        _run(params, ["import", "-q", "-C", "-M", params["path"]])
    finally:
        beets.ui._bootstrap_config = original

    library = Library(params["library"], beets.config["directory"].as_filename())
    try:
        items = library.items(PathQuery("path", params["path"]))
        added = sum(1 for item in items if item.added >= started)
        count = len(items)
    finally:
        library._close()

    return {"path": params["path"], "items": count, "added": added, "hardlinked": hardlinked}
