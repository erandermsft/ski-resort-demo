package main

import (
	"encoding/json"
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
