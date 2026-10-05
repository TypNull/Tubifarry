import asyncio
import hashlib
import json
import logging
import os
import pathlib
import time

import truststore

truststore.inject_into_ssl()

_AUDIO_EXTENSIONS = {".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".alac"}
_SOUNDCLOUD_ALBUM_TYPES = {"album", "ep", "single", "compilation"}

_loop = asyncio.new_event_loop()
_clients = {}


class _ErrorCollector(logging.Handler):
    def __init__(self):
        super().__init__(logging.ERROR)
        self.messages = []

    def emit(self, record):
        message = record.getMessage()
        if ", retrying:" not in message:
            self.messages.append(message)


class _Progress:
    def __init__(self, context, expected_tracks):
        self.context = context
        self.expected_tracks = max(expected_tracks, 1)
        self.tracks = []
        self.last_report = 0.0

    def callback(self, enabled, total, description):
        from streamrip.progress import Handle

        state = [0, max(total or 0, 1)]
        self.tracks.append(state)

        def update(amount):
            state[0] += amount
            self.report(False)

        def done():
            state[0] = state[1]
            self.report(True)

        return Handle(update, done)

    def report(self, force):
        now = time.monotonic()
        if not force and now - self.last_report < 1:
            return
        self.last_report = now
        finished = sum(min(done / total, 1) for done, total in self.tracks)
        self.context.progress(min(finished / max(self.expected_tracks, len(self.tracks)), 1), None)


def _config(params):
    import streamrip
    from streamrip.config import Config

    config = Config(str(pathlib.Path(streamrip.__file__).parent / "config.toml"))
    session = config.session

    session.downloads.folder = params.get("folder") or ""
    session.downloads.source_subdirectories = False
    session.downloads.disc_subdirectories = True
    session.downloads.concurrency = True
    session.downloads.max_connections = int(params.get("connections") or 3)
    session.downloads.verify_ssl = True
    session.database.downloads_enabled = False
    session.database.failed_downloads_enabled = False
    session.conversion.enabled = False
    session.cli.text_output = False
    session.cli.progress_bars = False
    session.misc.check_for_updates = False
    session.filepaths.add_singles_to_folder = True
    session.filepaths.folder_format = "{albumartist} - {title}"
    session.filepaths.track_format = "{tracknumber:02}. {artist} - {title}"
    session.metadata.set_playlist_to_album = True
    session.metadata.renumber_playlist_tracks = True

    quality = params.get("quality")
    source = params["source"]

    if source == "qobuz":
        session.qobuz.use_auth_token = bool(params.get("use_token"))
        session.qobuz.email_or_userid = params.get("user") or ""
        secret = params.get("secret") or ""
        if not session.qobuz.use_auth_token and secret:
            secret = hashlib.md5(secret.encode("utf-8")).hexdigest()
        session.qobuz.password_or_token = secret
        session.qobuz.download_booklets = False
        if params.get("app_id") and params.get("app_secret"):
            session.qobuz.app_id = str(params["app_id"])
            session.qobuz.secrets = [params["app_secret"]]
        if quality is not None:
            session.qobuz.quality = int(quality)
    elif source == "deezer":
        session.deezer.arl = params.get("secret") or ""
        session.deezer.use_deezloader = False
        session.deezer.deezloader_warnings = False
        if quality is not None:
            session.deezer.quality = int(quality)
    elif source == "tidal":
        for key, value in _read_tidal_tokens(params).items():
            setattr(session.tidal, key, str(value or ""))
        session.tidal.download_videos = False
        if quality is not None:
            session.tidal.quality = int(quality)
    elif source != "soundcloud":
        raise ValueError(f"Unknown source '{source}'")

    return config


def _client_type(source):
    from streamrip.client import DeezerClient, QobuzClient, SoundcloudClient, TidalClient

    return {"qobuz": QobuzClient, "deezer": DeezerClient, "tidal": TidalClient, "soundcloud": SoundcloudClient}[source]


_TIDAL_TOKEN_KEYS = ("user_id", "country_code", "access_token", "refresh_token", "token_expiry")
_TIDAL_BLOCKED = "tidal blocked the login request (HTTP {}), its firewall refuses this network or client. Try again later or from another network."


def _read_tidal_tokens(params):
    path = params.get("token_file")
    if not path or not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as file:
        data = json.load(file)
    return {key: data.get(key) for key in _TIDAL_TOKEN_KEYS}


def _write_tidal_tokens(params, values):
    path = params.get("token_file")
    if not path:
        return
    tokens = {key: str(values.get(key) or "") for key in _TIDAL_TOKEN_KEYS}
    if {key: str(value or "") for key, value in _read_tidal_tokens(params).items()} == tokens:
        return
    os.makedirs(os.path.dirname(path), exist_ok=True)
    temporary = f"{path}.{os.getpid()}.{time.monotonic_ns()}.tmp"
    try:
        with os.fdopen(os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w", encoding="utf-8") as file:
            json.dump(tokens, file)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.remove(temporary)


def _tidal_expiring(params, client):
    if params["source"] != "tidal":
        return False
    try:
        return float(client.config.token_expiry) - time.time() < 86400
    except (TypeError, ValueError):
        return True


async def _logged_in_client(params):
    identity = {k: params.get(k) for k in ("source", "user", "secret", "use_token", "app_id", "app_secret", "quality")}
    if params["source"] == "tidal":
        saved = _read_tidal_tokens(params)
        identity["tidal"] = [saved.get("user_id"), saved.get("refresh_token"), saved.get("access_token")]
    key = json.dumps(identity, sort_keys=True)
    client = _clients.get(key)
    if client is not None and client.logged_in and not _tidal_expiring(params, client):
        return client

    from streamrip.exceptions import AuthenticationError, MissingCredentialsError

    if params["source"] == "tidal" and not _read_tidal_tokens(params).get("access_token"):
        raise RuntimeError("tidal needs a login")

    client = _client_type(params["source"])(_config(params))
    try:
        await client.login()
    except MissingCredentialsError:
        raise RuntimeError(f"{params['source']} needs credentials") from None
    except AuthenticationError:
        raise RuntimeError(f"{params['source']} rejected the credentials") from None
    except Exception as error:
        import aiohttp

        if params["source"] == "tidal" and isinstance(error, aiohttp.ContentTypeError):
            raise RuntimeError(_TIDAL_BLOCKED.format(error.status)) from None
        if params["source"] == "tidal" and str(error).startswith(("Refresh failed", "Login failed", "User id mismatch")):
            raise RuntimeError("tidal rejected the saved login") from None
        raise
    if params["source"] == "tidal":
        _write_tidal_tokens(params, {key: getattr(client.config, key, "") for key in _TIDAL_TOKEN_KEYS})
    _clients[key] = client
    return client


async def _tidal_device_code(params):
    from streamrip.client import TidalClient

    client = TidalClient(_config(params))
    try:
        device_code, url = await client._get_device_code()
    finally:
        await client.session.close()
    return {"device_code": device_code, "url": url if url.startswith("http") else f"https://{url}"}


async def _tidal_poll(params):
    from streamrip.client import TidalClient

    import aiohttp

    client = TidalClient(_config(params))
    client.session = await client.get_session(verify_ssl=True)
    try:
        status, tokens = await client._get_auth_status(params["device_code"])
    except aiohttp.ContentTypeError as error:
        raise RuntimeError(_TIDAL_BLOCKED.format(error.status)) from None
    finally:
        await client.session.close()

    if status == 0:
        _write_tidal_tokens(params, tokens)
        return {"status": "ready"}
    return {"status": "pending" if status == 2 else "failed"}


def tidal_login_start(params, context):
    return _loop.run_until_complete(_tidal_device_code(params))


def tidal_login_poll(params, context):
    return _loop.run_until_complete(_tidal_poll(params))


def _date(item):
    for key in ("release_date_original", "release_date", "releaseDate", "display_date", "created_at"):
        value = item.get(key)
        if isinstance(value, str) and value:
            return value[:10]
    return None


def _artist(item):
    for value in (item.get("performer"), item.get("artist"), item.get("user")):
        if isinstance(value, dict) and (value.get("name") or value.get("username")):
            return value.get("name") or value.get("username")
        if isinstance(value, str) and value:
            return value
    publisher = item.get("publisher_metadata") or {}
    return publisher.get("artist") or "Unknown"


def _album(source, item):
    title = (item.get("title") or "").strip()
    version = (item.get("version") or "").strip()
    duration = item.get("duration") or 0
    if source == "soundcloud":
        duration = duration // 1000
    image = item.get("image") if isinstance(item.get("image"), dict) else {}
    tidal_quality = str(item.get("audioQuality") or "")
    tidal_cover = item.get("cover") if source == "tidal" and isinstance(item.get("cover"), str) else ""
    return {
        "id": str(item["id"]),
        "type": "playlist" if source == "soundcloud" else "album",
        "title": f"{title} ({version})" if version else title,
        "artist": _artist(item),
        "tracks": item.get("tracks_count") or item.get("nb_tracks") or item.get("numberOfTracks") or item.get("track_count") or 0,
        "date": _date(item),
        "duration": duration,
        "explicit": bool(item.get("parental_warning") or item.get("explicit_lyrics") or item.get("explicit")),
        "bit_depth": item.get("maximum_bit_depth") or (24 if tidal_quality.startswith("HI_RES") else 16 if tidal_quality == "LOSSLESS" else 0),
        "sample_rate": item.get("maximum_sampling_rate") or 0,
        "url": item.get("url") or item.get("link") or item.get("permalink_url") or "",
        "cover": image.get("large") or item.get("cover_xl") or item.get("artwork_url") or (f"https://resources.tidal.com/images/{tidal_cover.replace('-', '/')}/640x640.jpg" if tidal_cover else ""),
    }


def _items(source, pages):
    for page in pages:
        if source == "soundcloud":
            yield from page.get("collection", [])
        elif source == "qobuz":
            yield from page.get("albums", {}).get("items", [])
        elif source == "deezer":
            yield from page.get("data", [])
        else:
            yield from page.get("items", [])


async def _search(params):
    source = params["source"]
    limit = int(params.get("limit") or 50)
    client = await _logged_in_client(params)

    if source == "soundcloud":
        pages = await client.search("playlist", params["query"], limit=limit)
        items = [i for i in _items(source, pages) if i.get("is_album") or (i.get("set_type") or "") in _SOUNDCLOUD_ALBUM_TYPES]
    else:
        pages = await client.search("album", params["query"], limit=limit)
        items = list(_items(source, pages))

    return [_album(source, item) for item in items[:limit]]


def search(params, context):
    return {"albums": _loop.run_until_complete(_search(params))}


def check(params, context):
    _loop.run_until_complete(_logged_in_client(params))
    return {"source": params["source"]}


async def _download(params, context):
    from streamrip import media
    from streamrip.db import Database, Dummy

    client = await _logged_in_client(params)
    config = _config(params)
    database = Database(Dummy(), Dummy())
    pending_type = {"album": media.PendingAlbum, "playlist": media.PendingPlaylist, "track": media.PendingSingle}[params["type"]]

    resolved = await pending_type(params["id"], client, config, database).resolve()
    if resolved is None:
        raise RuntimeError(f"{params['source']} {params['type']} {params['id']} is not available")

    try:
        await resolved.rip()
    finally:
        media.remove_artwork_tempdirs()


def download(params, context):
    import streamrip.media.track as track_module

    folder = pathlib.Path(params["folder"])
    folder.mkdir(parents=True, exist_ok=True)

    collector = _ErrorCollector()
    streamrip_logger = logging.getLogger("streamrip")
    streamrip_logger.addHandler(collector)
    progress = _Progress(context, int(params.get("tracks") or 0))
    original_callback = track_module.get_progress_callback
    track_module.get_progress_callback = progress.callback

    try:
        _loop.run_until_complete(_download(params, context))
    finally:
        track_module.get_progress_callback = original_callback
        streamrip_logger.removeHandler(collector)

    encrypted = [message for message in collector.messages if "'url'" in message]
    errors = [message for message in collector.messages if "'url'" not in message]
    if encrypted and params["source"] == "soundcloud":
        errors.insert(0, f"SoundCloud only offers an encrypted stream for {len(encrypted)} track(s), which is common for label and distributor uploads")
    else:
        errors = collector.messages

    files = sorted(str(path) for path in folder.rglob("*") if path.is_file() and path.suffix.lower() in _AUDIO_EXTENSIONS)
    return {"files": files, "errors": errors}
