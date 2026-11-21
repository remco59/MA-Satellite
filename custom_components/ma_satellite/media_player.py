"""Media player entity that receives announcements from Home Assistant."""

from __future__ import annotations

import logging
from urllib.parse import urlsplit

import aiohttp

from homeassistant import media_source
from homeassistant.components.media_player import (
    MediaPlayerDeviceClass,
    MediaPlayerEntity,
    MediaPlayerEntityFeature,
)
from homeassistant.components.media_player.browse_media import (
    async_process_play_media_url,
)
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_NAME
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .const import (
    CONF_HOST,
    CONF_PORT,
    CONF_TOKEN,
    DOMAIN,
    MAX_ANNOUNCEMENT_BYTES,
)

_LOGGER = logging.getLogger(__name__)


async def async_setup_entry(
    hass: HomeAssistant,
    entry: ConfigEntry,
    async_add_entities: AddEntitiesCallback,
) -> None:
    """Set up the MA Satellite announcement player."""
    async_add_entities([MaSatelliteAnnouncementPlayer(hass, entry)])


class MaSatelliteAnnouncementPlayer(MediaPlayerEntity):
    """A virtual HA player that queues speech over MA-Satellite's existing audio."""

    _attr_has_entity_name = True
    _attr_device_class = MediaPlayerDeviceClass.SPEAKER
    _attr_supported_features = MediaPlayerEntityFeature.MEDIA_ANNOUNCE

    def __init__(self, hass: HomeAssistant, entry: ConfigEntry) -> None:
        """Initialize the announcement target."""
        self.hass = hass
        self._entry = entry
        self._base_url = (
            f"http://{entry.data[CONF_HOST].strip().removeprefix('http://').removeprefix('https://').rstrip('/')}:{entry.data[CONF_PORT]}"
        )
        self._token = entry.data[CONF_TOKEN]
        self._mac = entry.data["mac_address"].lower()
        self._attr_unique_id = self._mac
        self._attr_name = "Announcements"
        self._attr_device_info = dr.DeviceInfo(
            identifiers={(DOMAIN, self._mac)},
            connections={(dr.CONNECTION_NETWORK_MAC, self._mac)},
            manufacturer="MA-Satellite",
            model="Windows Sendspin player",
            name=entry.data.get(CONF_NAME, "MA Satellite"),
            configuration_url=self._base_url,
        )

    @property
    def available(self) -> bool:
        """Assume the endpoint is reachable; calls report connectivity errors."""
        return True

    async def async_play_media(
        self,
        media_type: str,
        media_id: str,
        **kwargs,
    ) -> None:
        """Fetch an announcement and submit it to MA-Satellite's local audio mixer."""
        if not kwargs.get("announce", False):
            raise HomeAssistantError(
                "MA Satellite only accepts media_player.play_media with announce: true."
            )

        if media_source.is_media_source_id(media_id):
            resolved = await media_source.async_resolve_media(
                self.hass, media_id, self.entity_id
            )
            media_id = resolved.url

        media_url = async_process_play_media_url(self.hass, media_id)
        parsed = urlsplit(media_url)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            raise HomeAssistantError("The announcement must resolve to an HTTP(S) URL.")

        session = async_get_clientsession(self.hass)
        timeout = aiohttp.ClientTimeout(total=60)
        try:
            async with session.get(media_url, timeout=timeout) as response:
                response.raise_for_status()
                if response.content_length is not None and response.content_length > MAX_ANNOUNCEMENT_BYTES:
                    raise HomeAssistantError("Announcement media exceeds the 30 MB limit.")
                content_type = response.headers.get("Content-Type", "application/octet-stream").split(";", 1)[0]
                media = bytearray()
                async for chunk in response.content.iter_chunked(64 * 1024):
                    media.extend(chunk)
                    if len(media) > MAX_ANNOUNCEMENT_BYTES:
                        raise HomeAssistantError("Announcement media exceeds the 30 MB limit.")
        except aiohttp.ClientError as err:
            raise HomeAssistantError(f"Could not retrieve announcement media: {err}") from err

        if not media:
            raise HomeAssistantError("The announcement media was empty.")

        try:
            async with session.post(
                f"{self._base_url}/api/announcement",
                data=media,
                headers={
                    "Authorization": f"Bearer {self._token}",
                    "Content-Type": content_type,
                },
                timeout=timeout,
            ) as response:
                response.raise_for_status()
        except aiohttp.ClientError as err:
            raise HomeAssistantError(
                f"Could not send the announcement to MA-Satellite: {err}"
            ) from err
