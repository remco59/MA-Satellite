"""MA Satellite announcement integration for Home Assistant."""

from __future__ import annotations

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant

from .const import DOMAIN

type MaSatelliteConfigEntry = ConfigEntry


async def async_setup(hass: HomeAssistant, config: dict) -> bool:
    """Set up the integration."""
    return True


async def async_setup_entry(
    hass: HomeAssistant, entry: MaSatelliteConfigEntry
) -> bool:
    """Set up a configured MA Satellite player."""
    await hass.config_entries.async_forward_entry_setups(entry, ["media_player"])
    return True


async def async_unload_entry(
    hass: HomeAssistant, entry: MaSatelliteConfigEntry
) -> bool:
    """Unload a configured MA Satellite player."""
    return await hass.config_entries.async_unload_platforms(entry, ["media_player"])
