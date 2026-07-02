"""Unit tests for WeatherService forecast + storm logic (mocked data-generator)."""
import asyncio

from services.weather_service import WeatherService


def _service() -> WeatherService:
    return WeatherService()


def test_get_forecast_clamps_hours_and_projects(monkeypatch):
    svc = _service()

    async def fake_current():
        return {"temperature": -3.0, "wind_speed": 10.0, "snow_intensity": 1, "visibility": 4000}

    monkeypatch.setattr(svc, "get_current_conditions", fake_current)

    # Requesting more than the max (24) clamps down to 24 hourly entries.
    result = asyncio.run(svc.get_forecast(100))
    assert result["forecast_hours"] == 24
    assert len(result["hourly_forecast"]) == 24

    # Requesting less than the min (1) clamps up to 1.
    result = asyncio.run(svc.get_forecast(0))
    assert result["forecast_hours"] == 1
    assert len(result["hourly_forecast"]) == 1


def test_is_storm_incoming_detects_high_wind(monkeypatch):
    svc = _service()

    async def fake_current():
        return {"wind_speed": 60, "snow_intensity": 1, "visibility": 4000}

    monkeypatch.setattr(svc, "get_current_conditions", fake_current)

    result = asyncio.run(svc.is_storm_incoming())
    assert result["storm_incoming"] is True


def test_is_storm_incoming_calm_is_false(monkeypatch):
    svc = _service()

    async def fake_current():
        return {"wind_speed": 5, "snow_intensity": 0, "visibility": 8000}

    monkeypatch.setattr(svc, "get_current_conditions", fake_current)

    result = asyncio.run(svc.is_storm_incoming())
    assert result["storm_incoming"] is False
