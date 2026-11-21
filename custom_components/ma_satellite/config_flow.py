"""Config flow for MA Satellite."""

from __future__ import annotations

import ipaddress
import logging
from typing import Any

import aiohttp
import voluptuous as vol

from homeassistant import config_entries
from homeassistant.const import CONF_NAME
from homeassistant.core import HomeAssistant
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .const import CONF_HOST, CONF_PORT, CONF_TOKEN, DEFAULT_PORT, DOMAIN

_LOGGER = logging.getLogger(__name__)


def _base_url(host: str, port: int) -> str:
    """Build a URL from a hostname or IP address entered by the user."""
    normalized = host.strip().removeprefix("http://").removeprefix("https://").rstrip("/")
    if not normalized or "/" in normalized or "@" in normalized:
        raise ValueError("Enter a hostname or IP address, not a URL path.")

    address = normalized[1:-1] if normalized.startswith("[") and normalized.endswith("]") else normalized
    try:
        parsed = ipaddress.ip_address(address)
    except ValueError:
        if ":" in normalized:
            raise ValueError("Do not include a port in the hostname field.")
        authority = normalized
    else:
        authority = f"[{parsed}]" if parsed.version == 6 else str(parsed)

    return f"http://{authority}:{port}"


async def _validate_input(
    hass: HomeAssistant, user_input: dict[str, Any]
) -> dict[str, str]:
    """Connect to MA Satellite and return its device identity."""
    base_url = _base_url(user_input[CONF_HOST], int(user_input[CONF_PORT]))
    session = async_get_clientsession(hass)
    timeout = aiohttp.ClientTimeout(total=8)
    async with session.get(
        f"{base_url}/api/device-info",
        headers={"Authorization": f"Bearer {user_input[CONF_TOKEN]}"},
        timeout=timeout,
    ) as response:
        response.raise_for_status()
        device = await response.json()

    mac_address = str(device.get("mac_address", "")).strip().lower()
    name = str(device.get("name", "")).strip()
    if not mac_address or len(mac_address.split(":")) != 6:
        raise ValueError("MA-Satellite did not report a valid network MAC address.")
    if not name:
        name = "MA Satellite"
    return {"base_url": base_url, "mac_address": mac_address, "name": name}


class MaSatelliteConfigFlow(config_entries.ConfigFlow, domain=DOMAIN):
    """Handle a config flow for a MA Satellite instance."""

    VERSION = 1

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> dict[str, Any]:
        """Handle the user initiated setup step."""
        errors: dict[str, str] = {}
        if user_input is not None:
            try:
                info = await _validate_input(self.hass, user_input)
            except aiohttp.ClientResponseError as err:
                errors["base"] = "invalid_auth" if err.status in (401, 403) else "cannot_connect"
            except (aiohttp.ClientError, TimeoutError, OSError):
                errors["base"] = "cannot_connect"
            except (ValueError, KeyError):
                errors["base"] = "invalid_auth"
            else:
                await self.async_set_unique_id(info["mac_address"])
                self._abort_if_unique_id_configured()
                return self.async_create_entry(
                    title=info["name"],
                    data={
                        CONF_HOST: user_input[CONF_HOST].strip(),
                        CONF_PORT: int(user_input[CONF_PORT]),
                        CONF_TOKEN: user_input[CONF_TOKEN].strip(),
                        CONF_NAME: info["name"],
                        "mac_address": info["mac_address"],
                        "base_url": info["base_url"],
                    },
                )

        schema = vol.Schema(
            {
                vol.Required(CONF_HOST): str,
                vol.Required(CONF_PORT, default=DEFAULT_PORT): vol.All(
                    vol.Coerce(int), vol.Range(min=1, max=65535)
                ),
                vol.Required(CONF_TOKEN): str,
            }
        )
        return self.async_show_form(
            step_id="user", data_schema=schema, errors=errors
        )
