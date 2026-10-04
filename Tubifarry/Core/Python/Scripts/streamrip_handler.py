import asyncio
import hashlib
import json
import pathlib

import truststore

truststore.inject_into_ssl()

_SOUNDCLOUD_ALBUM_TYPES = {"album", "ep", "single", "compilation"}

_loop = asyncio.new_event_loop()
_clients = {}


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
        for key in ("user_id", "country_code", "access_token", "refresh_token", "token_expiry"):
            setattr(session.tidal, key, str(params.get(key) or ""))
        session.tidal.download_videos = False
        if quality is not None:
            session.tidal.quality = int(quality)
    elif source != "soundcloud":
        raise ValueError(f"Unknown source '{source}'")

    return config


def _client_type(source):
    from streamrip.client import DeezerClient, QobuzClient, SoundcloudClient, TidalClient

    return {"qobuz": QobuzClient, "deezer": DeezerClient, "tidal": TidalClient, "soundcloud": SoundcloudClient}[source]


async def _logged_in_client(params):
    key = json.dumps({k: params.get(k) for k in ("source", "user", "secret", "use_token", "app_id", "app_secret", "quality")}, sort_keys=True)
    client = _clients.get(key)
    if client is not None and client.logged_in:
        return client

    from streamrip.exceptions import AuthenticationError, MissingCredentialsError

    client = _client_type(params["source"])(_config(params))
    try:
        await client.login()
    except MissingCredentialsError:
        raise RuntimeError(f"{params['source']} needs credentials") from None
    except AuthenticationError:
        raise RuntimeError(f"{params['source']} rejected the credentials") from None
    _clients[key] = client
    return client


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
    return {
        "id": str(item["id"]),
        "type": "playlist" if source == "soundcloud" else "album",
        "title": f"{title} ({version})" if version else title,
        "artist": _artist(item),
        "tracks": item.get("tracks_count") or item.get("nb_tracks") or item.get("numberOfTracks") or item.get("track_count") or 0,
        "date": _date(item),
        "duration": duration,
        "explicit": bool(item.get("parental_warning") or item.get("explicit_lyrics") or item.get("explicit")),
        "bit_depth": item.get("maximum_bit_depth") or 0,
        "sample_rate": item.get("maximum_sampling_rate") or 0,
        "url": item.get("url") or item.get("link") or item.get("permalink_url") or "",
        "cover": image.get("large") or item.get("cover_xl") or item.get("artwork_url") or "",
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
