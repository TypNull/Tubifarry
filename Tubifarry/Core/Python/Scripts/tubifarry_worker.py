import importlib.util
import json
import os
import sys
import threading
import traceback

_protocol = os.fdopen(os.dup(1), "w", encoding="utf-8", buffering=1)
os.dup2(2, 1)
sys.stdout = sys.stderr
if os.name == "nt":
    import ctypes
    import msvcrt

    ctypes.windll.kernel32.SetStdHandle(-11, msvcrt.get_osfhandle(1))
_lock = threading.Lock()


def _send(message):
    line = json.dumps(message, ensure_ascii=False, default=str, allow_nan=False)
    with _lock:
        _protocol.write(line + "\n")
        _protocol.flush()


class Context:
    def __init__(self, request_id):
        self.id = request_id

    def progress(self, value, message=None):
        _send({"id": self.id, "progress": value, "message": message})


def _load_handler(path):
    spec = importlib.util.spec_from_file_location("tubifarry_handler", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _handle(handler, request):
    request_id = request.get("id")
    method = request.get("method") or ""
    function = None if method.startswith("_") else getattr(handler, method, None)
    if not callable(function):
        _send({"id": request_id, "error": {"type": "MethodNotFound", "message": f"Unknown method '{method}'", "traceback": ""}})
        return
    try:
        result = function(request.get("params") or {}, Context(request_id))
        _send({"id": request_id, "result": result})
    except BaseException as error:
        if isinstance(error, (KeyboardInterrupt, GeneratorExit)):
            raise
        _send({"id": request_id, "error": {"type": type(error).__name__, "message": str(error), "traceback": traceback.format_exc()}})


def main():
    handler = _load_handler(sys.argv[1])
    _send({"event": "ready", "python": sys.version.split()[0], "pid": os.getpid()})
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except ValueError as error:
            _send({"id": None, "error": {"type": "InvalidRequest", "message": str(error), "traceback": ""}})
            continue
        _handle(handler, request)


if __name__ == "__main__":
    main()
