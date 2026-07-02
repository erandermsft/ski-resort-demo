"""Unit tests for SafetyService risk logic (pure rules + mocked data-generator)."""
import asyncio

from services.safety_service import SafetyService


def _service() -> SafetyService:
    # No network is touched: we test pure logic and mock the fetch methods.
    return SafetyService()


def test_calculate_risk_score_calm_conditions_is_low():
    svc = _service()
    weather = {"wind_speed": 5, "visibility": 5000, "snow_intensity": 0}
    safety = {"avalanche_risk_index": 0.0}

    risk, factors = svc._calculate_risk_score(weather, safety)

    assert risk == 0.0
    assert factors == []


def test_calculate_risk_score_extreme_conditions_clamped_and_flagged():
    svc = _service()
    weather = {"wind_speed": 60, "visibility": 300, "snow_intensity": 4}
    safety = {"avalanche_risk_index": 0.9}

    risk, factors = svc._calculate_risk_score(weather, safety)

    # Base 0.9 + 0.2 (wind) + 0.15 (visibility) + 0.1 (snow) clamps to 1.0.
    assert risk == 1.0
    assert any("Extreme wind speed" in f for f in factors)
    assert any("Very low visibility" in f for f in factors)
    assert any("Heavy snowfall" in f for f in factors)
    assert any("Avalanche risk index" in f for f in factors)


def test_get_risk_level_thresholds():
    svc = _service()
    assert svc._get_risk_level(0.0) == "low"
    assert svc._get_risk_level(0.29) == "low"
    assert svc._get_risk_level(0.3) == "moderate"
    assert svc._get_risk_level(0.49) == "moderate"
    assert svc._get_risk_level(0.5) == "high"
    assert svc._get_risk_level(0.69) == "high"
    assert svc._get_risk_level(0.7) == "critical"
    assert svc._get_risk_level(1.0) == "critical"


def test_evaluate_risk_filters_slopes_by_area(monkeypatch):
    svc = _service()

    async def fake_weather():
        return {"wind_speed": 10, "visibility": 5000, "snow_intensity": 0}

    async def fake_safety():
        return {"avalanche_risk_index": 0.1, "incident_reports": []}

    async def fake_slopes():
        return [
            {"slope_id": "north-face", "name": "North Face", "difficulty": "expert", "is_open": True},
            {"slope_id": "valley-run", "name": "Valley Run", "difficulty": "beginner", "is_open": True},
        ]

    monkeypatch.setattr(svc, "_fetch_weather", fake_weather)
    monkeypatch.setattr(svc, "_fetch_safety", fake_safety)
    monkeypatch.setattr(svc, "_fetch_slopes", fake_slopes)

    result = asyncio.run(svc.evaluate_risk("north"))

    assert result["area"] == "north"
    assert result["risk_level"] in {"low", "moderate", "high", "critical"}
    assert [s["slope_id"] for s in result["affected_slopes"]] == ["north-face"]


def test_evaluate_risk_all_returns_every_slope(monkeypatch):
    svc = _service()

    async def fake_weather():
        return {"wind_speed": 10, "visibility": 5000, "snow_intensity": 0}

    async def fake_safety():
        return {"avalanche_risk_index": 0.0, "incident_reports": []}

    async def fake_slopes():
        return [
            {"slope_id": "north-face", "name": "North Face", "difficulty": "expert", "is_open": True},
            {"slope_id": "valley-run", "name": "Valley Run", "difficulty": "beginner", "is_open": False},
        ]

    monkeypatch.setattr(svc, "_fetch_weather", fake_weather)
    monkeypatch.setattr(svc, "_fetch_safety", fake_safety)
    monkeypatch.setattr(svc, "_fetch_slopes", fake_slopes)

    result = asyncio.run(svc.evaluate_risk("all"))

    assert len(result["affected_slopes"]) == 2
    assert result["risk_level"] == "low"
