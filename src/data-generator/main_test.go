package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestResortStateJSONContract(t *testing.T) {
	generator, err := NewDataGenerator()
	if err != nil {
		t.Fatalf("NewDataGenerator() error = %v", err)
	}

	payload, err := json.Marshal(generator.State())
	if err != nil {
		t.Fatalf("json.Marshal() error = %v", err)
	}

	jsonText := string(payload)
	for _, field := range []string{
		`"lift_id"`,
		`"queue_length"`,
		`"wait_time_minutes"`,
		`"throughput_rate"`,
		`"serves_slopes"`,
		`"avalanche_risk_index"`,
		`"incident_reports":[]`,
		`"snow_depth_cm"`,
		`"served_by_lift_id"`,
	} {
		if !strings.Contains(jsonText, field) {
			t.Fatalf("expected JSON to contain %s, got %s", field, jsonText)
		}
	}
}

// newTestGenerator builds a generator and its HTTP handler for endpoint tests.
func newTestGenerator(t *testing.T) (*DataGenerator, http.Handler) {
	t.Helper()
	generator, err := NewDataGenerator()
	if err != nil {
		t.Fatalf("NewDataGenerator() error = %v", err)
	}
	return generator, routes(generator)
}

func TestHealthEndpointReturnsHealthy(t *testing.T) {
	_, handler := newTestGenerator(t)

	req := httptest.NewRequest(http.MethodGet, "/health", nil)
	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("GET /health status = %d, want %d", rec.Code, http.StatusOK)
	}

	var body map[string]string
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatalf("decode /health body: %v", err)
	}
	if body["status"] != "healthy" {
		t.Fatalf(`status = %q, want "healthy"`, body["status"])
	}
	if body["service"] != "data-generator" {
		t.Fatalf(`service = %q, want "data-generator"`, body["service"])
	}
}

func TestCurrentStateEndpointReturnsPopulatedState(t *testing.T) {
	_, handler := newTestGenerator(t)

	req := httptest.NewRequest(http.MethodGet, "/api/current-state", nil)
	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("GET /api/current-state status = %d, want %d", rec.Code, http.StatusOK)
	}

	var state ResortState
	if err := json.Unmarshal(rec.Body.Bytes(), &state); err != nil {
		t.Fatalf("decode current-state body: %v", err)
	}
	if len(state.Lifts) == 0 {
		t.Fatal("expected at least one lift in current-state")
	}
	if len(state.Slopes) == 0 {
		t.Fatal("expected at least one slope in current-state")
	}
}

func TestLiftByIDEndpoint(t *testing.T) {
	generator, handler := newTestGenerator(t)

	lifts := generator.Lifts()
	if len(lifts) == 0 {
		t.Fatal("generator has no lifts to look up")
	}
	knownID := lifts[0].LiftID

	t.Run("known lift returns 200", func(t *testing.T) {
		req := httptest.NewRequest(http.MethodGet, "/api/lifts/"+knownID, nil)
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, req)

		if rec.Code != http.StatusOK {
			t.Fatalf("status = %d, want %d", rec.Code, http.StatusOK)
		}
		var lift LiftData
		if err := json.Unmarshal(rec.Body.Bytes(), &lift); err != nil {
			t.Fatalf("decode lift body: %v", err)
		}
		if lift.LiftID != knownID {
			t.Fatalf("lift_id = %q, want %q", lift.LiftID, knownID)
		}
	})

	t.Run("unknown lift returns 404", func(t *testing.T) {
		req := httptest.NewRequest(http.MethodGet, "/api/lifts/does-not-exist", nil)
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, req)

		if rec.Code != http.StatusNotFound {
			t.Fatalf("status = %d, want %d", rec.Code, http.StatusNotFound)
		}
	})

	t.Run("non-GET method returns 405", func(t *testing.T) {
		req := httptest.NewRequest(http.MethodPost, "/api/lifts/"+knownID, nil)
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, req)

		if rec.Code != http.StatusMethodNotAllowed {
			t.Fatalf("status = %d, want %d", rec.Code, http.StatusMethodNotAllowed)
		}
	})
}
