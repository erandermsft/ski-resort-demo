package main

import (
	"context"
	_ "embed"
	"encoding/json"
	"errors"
	"log/slog"
	"math"
	"math/rand"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"sync"
	"syscall"
	"time"

	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
)

//go:embed config.json
var configBytes []byte

type Config struct {
	UpdateIntervalSeconds IntervalConfig `json:"update_interval_seconds"`
	Weather               WeatherConfig  `json:"weather"`
	Lifts                 LiftConfig     `json:"lifts"`
	Safety                SafetyConfig   `json:"safety"`
	Slopes                SlopeConfig    `json:"slopes"`
}

type IntervalConfig struct {
	Min float64 `json:"min"`
	Max float64 `json:"max"`
}

type WeatherConfig struct {
	TemperatureDrift   float64 `json:"temperature_drift"`
	WindSpeedDrift     float64 `json:"wind_speed_drift"`
	SnowIntensityDrift float64 `json:"snow_intensity_drift"`
	VisibilityDrift    float64 `json:"visibility_drift"`
}

type LiftConfig struct {
	QueueDrift              int     `json:"queue_drift"`
	StatusChangeProbability float64 `json:"status_change_probability"`
}

type SafetyConfig struct {
	RiskDrift           float64 `json:"risk_drift"`
	IncidentProbability float64 `json:"incident_probability"`
}

type SlopeConfig struct {
	DepthDrift         float64 `json:"depth_drift"`
	ReopenProbability  float64 `json:"reopen_probability"`
	GroomProbability   float64 `json:"groom_probability"`
	UngroomProbability float64 `json:"ungroom_probability"`
}

type WeatherData struct {
	Temperature   float64   `json:"temperature"`
	WindSpeed     float64   `json:"wind_speed"`
	SnowIntensity float64   `json:"snow_intensity"`
	Visibility    float64   `json:"visibility"`
	Timestamp     time.Time `json:"timestamp"`
}

type LiftData struct {
	LiftID          string    `json:"lift_id"`
	Name            string    `json:"name"`
	Status          string    `json:"status"`
	QueueLength     int       `json:"queue_length"`
	WaitTimeMinutes float64   `json:"wait_time_minutes"`
	ThroughputRate  int       `json:"throughput_rate"`
	ServesSlopes    []string  `json:"serves_slopes"`
	Timestamp       time.Time `json:"timestamp"`
}

type IncidentReport struct {
	IncidentType string    `json:"incident_type"`
	Location     string    `json:"location"`
	Severity     string    `json:"severity"`
	Timestamp    time.Time `json:"timestamp"`
}

type SafetyData struct {
	AvalancheRiskIndex float64          `json:"avalanche_risk_index"`
	IncidentReports    []IncidentReport `json:"incident_reports"`
	Timestamp          time.Time        `json:"timestamp"`
}

type SlopeData struct {
	SlopeID        string  `json:"slope_id"`
	Name           string  `json:"name"`
	Difficulty     string  `json:"difficulty"`
	IsOpen         bool    `json:"is_open"`
	Groomed        bool    `json:"groomed"`
	SnowDepthCM    float64 `json:"snow_depth_cm"`
	ServedByLiftID string  `json:"served_by_lift_id"`
}

type ResortState struct {
	Weather   WeatherData `json:"weather"`
	Lifts     []LiftData  `json:"lifts"`
	Safety    SafetyData  `json:"safety"`
	Slopes    []SlopeData `json:"slopes"`
	Timestamp time.Time   `json:"timestamp"`
}

type DataGenerator struct {
	mu              sync.RWMutex
	rng             *rand.Rand
	config          Config
	currentTime     time.Time
	weather         WeatherData
	lifts           []LiftData
	safety          SafetyData
	slopes          []SlopeData
	incidentHistory []IncidentReport
}

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	shutdownTelemetry, logger, err := setupTelemetry(ctx)
	if err != nil {
		slog.New(slog.NewTextHandler(os.Stderr, nil)).Error("initializing telemetry", "error", err)
		os.Exit(1)
	}
	defer func() {
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		if err := shutdownTelemetry(shutdownCtx); err != nil {
			logger.Error("shutting down telemetry", "error", err)
		}
	}()
	slog.SetDefault(logger)

	generator, err := NewDataGenerator()
	if err != nil {
		logger.Error("initializing data generator", "error", err)
		os.Exit(1)
	}

	go generator.Run(ctx)

	port := getenv("PORT", "8080")
	host := getenv("HOST", "0.0.0.0")
	server := &http.Server{
		Addr:    net.JoinHostPort(host, port),
		Handler: otelhttp.NewHandler(withCORS(routes(generator)), "data-generator"),
	}

	go func() {
		logger.Info("starting AlpineAI Data Generator", "address", "http://"+server.Addr)
		if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			logger.Error("serving data generator", "error", err)
			os.Exit(1)
		}
	}()

	<-ctx.Done()
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := server.Shutdown(shutdownCtx); err != nil {
		logger.Error("shutting down data generator", "error", err)
	}
}

func NewDataGenerator() (*DataGenerator, error) {
	var config Config
	if err := json.Unmarshal(configBytes, &config); err != nil {
		return nil, err
	}

	g := &DataGenerator{
		rng:             rand.New(rand.NewSource(time.Now().UnixNano())),
		config:          config,
		currentTime:     time.Now().UTC(),
		incidentHistory: []IncidentReport{},
	}

	g.slopes = g.createInitialSlopes()
	g.lifts = g.createInitialLifts()
	g.weather = WeatherData{
		Temperature:   g.randomFloat(-10, 0),
		WindSpeed:     g.randomFloat(5, 25),
		SnowIntensity: g.randomFloat(0, 2),
		Visibility:    g.randomFloat(5000, 10000),
		Timestamp:     g.currentTime,
	}
	g.safety = SafetyData{
		AvalancheRiskIndex: g.randomFloat(0.1, 0.4),
		IncidentReports:    []IncidentReport{},
		Timestamp:          g.currentTime,
	}

	return g, nil
}

func (g *DataGenerator) Run(ctx context.Context) {
	slog.Info("starting data generation loop")
	for {
		g.Update()

		timer := time.NewTimer(g.nextUpdateInterval())
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}
	}
}

func (g *DataGenerator) Update() {
	g.mu.Lock()
	defer g.mu.Unlock()

	g.currentTime = time.Now().UTC()
	g.updateWeather()
	g.updateLifts()
	g.updateSafety()
	g.updateSlopes()
}

func (g *DataGenerator) State() ResortState {
	g.mu.RLock()
	defer g.mu.RUnlock()

	return ResortState{
		Weather:   g.weather,
		Lifts:     cloneLifts(g.lifts),
		Safety:    cloneSafety(g.safety),
		Slopes:    cloneSlopes(g.slopes),
		Timestamp: g.currentTime,
	}
}

func (g *DataGenerator) Weather() WeatherData {
	g.mu.RLock()
	defer g.mu.RUnlock()
	return g.weather
}

func (g *DataGenerator) Lifts() []LiftData {
	g.mu.RLock()
	defer g.mu.RUnlock()
	return cloneLifts(g.lifts)
}

func (g *DataGenerator) LiftByID(liftID string) (LiftData, bool) {
	g.mu.RLock()
	defer g.mu.RUnlock()

	for _, lift := range g.lifts {
		if lift.LiftID == liftID {
			return cloneLift(lift), true
		}
	}
	return LiftData{}, false
}

func (g *DataGenerator) Safety() SafetyData {
	g.mu.RLock()
	defer g.mu.RUnlock()
	return cloneSafety(g.safety)
}

func (g *DataGenerator) Slopes() []SlopeData {
	g.mu.RLock()
	defer g.mu.RUnlock()
	return cloneSlopes(g.slopes)
}

func (g *DataGenerator) createInitialLifts() []LiftData {
	liftConfigs := []struct {
		id           string
		name         string
		throughput   int
		status       string
		servesSlopes []string
	}{
		{"gondola-1", "Summit Gondola", 2400, "open", []string{"summit-chute", "avalanche-alley"}},
		{"chairlift-alpha", "Alpine Express", 1800, "open", []string{"alpine-meadow", "north-face"}},
		{"chairlift-bravo", "Eagle Chair", 1600, "open", []string{"eagle-ridge", "timber-bowl"}},
		{"t-bar-1", "Beginner T-Bar", 800, "open", []string{"valley-run"}},
		{"magic-carpet-1", "Kids Magic Carpet", 400, "open", []string{"sunrise-trail"}},
	}

	lifts := make([]LiftData, 0, len(liftConfigs))
	for _, lift := range liftConfigs {
		queue := g.randomInt(10, 80)
		waitTime := 0.0
		if lift.throughput > 0 {
			waitTime = round1((float64(queue) / float64(lift.throughput)) * 60)
		}

		lifts = append(lifts, LiftData{
			LiftID:          lift.id,
			Name:            lift.name,
			Status:          lift.status,
			QueueLength:     queue,
			WaitTimeMinutes: waitTime,
			ThroughputRate:  lift.throughput,
			ServesSlopes:    append([]string(nil), lift.servesSlopes...),
			Timestamp:       g.currentTime,
		})
	}

	return lifts
}

func (g *DataGenerator) createInitialSlopes() []SlopeData {
	slopeConfigs := []struct {
		id      string
		name    string
		level   string
		open    bool
		groomed bool
		depth   float64
		liftID  string
	}{
		{"valley-run", "Valley Run", "green", true, true, 85, "t-bar-1"},
		{"sunrise-trail", "Sunrise Trail", "green", true, true, 90, "magic-carpet-1"},
		{"alpine-meadow", "Alpine Meadow", "blue", true, true, 105, "chairlift-alpha"},
		{"eagle-ridge", "Eagle Ridge", "blue", true, false, 95, "chairlift-bravo"},
		{"timber-bowl", "Timber Bowl", "blue", true, false, 110, "chairlift-bravo"},
		{"north-face", "North Face", "red", true, false, 120, "chairlift-alpha"},
		{"summit-chute", "Summit Chute", "black", true, false, 130, "gondola-1"},
		{"avalanche-alley", "Avalanche Alley", "black", true, false, 125, "gondola-1"},
	}

	slopes := make([]SlopeData, 0, len(slopeConfigs))
	for _, slope := range slopeConfigs {
		slopes = append(slopes, SlopeData{
			SlopeID:        slope.id,
			Name:           slope.name,
			Difficulty:     slope.level,
			IsOpen:         slope.open,
			Groomed:        slope.groomed,
			SnowDepthCM:    round1(slope.depth + g.randomFloat(-10, 10)),
			ServedByLiftID: slope.liftID,
		})
	}

	return slopes
}

func (g *DataGenerator) updateWeather() {
	cfg := g.config.Weather

	g.weather.Temperature = clamp(g.weather.Temperature+g.randomFloat(-cfg.TemperatureDrift, cfg.TemperatureDrift), -15, 5)
	g.weather.WindSpeed = clamp(g.weather.WindSpeed+g.randomFloat(-cfg.WindSpeedDrift, cfg.WindSpeedDrift), 0, 80)
	g.weather.SnowIntensity = clamp(g.weather.SnowIntensity+g.randomFloat(-cfg.SnowIntensityDrift, cfg.SnowIntensityDrift), 0, 5)

	visibilityDelta := g.randomFloat(-cfg.VisibilityDrift, cfg.VisibilityDrift)
	if g.weather.SnowIntensity > 2 {
		visibilityDelta -= cfg.VisibilityDrift * 2
	}
	if g.weather.WindSpeed > 40 {
		visibilityDelta -= cfg.VisibilityDrift * 1.5
	}
	g.weather.Visibility = clamp(g.weather.Visibility+visibilityDelta, 50, 10000)
	g.weather.Timestamp = g.currentTime
}

func (g *DataGenerator) updateLifts() {
	cfg := g.config.Lifts
	for i := range g.lifts {
		lift := &g.lifts[i]
		lift.QueueLength = int(clamp(float64(lift.QueueLength+g.randomInt(-cfg.QueueDrift, cfg.QueueDrift)), 0, 200))

		if g.rng.Float64() < cfg.StatusChangeProbability {
			if lift.Status == "open" {
				lift.Status = g.randomChoice([]string{"closed", "maintenance"})
			} else {
				lift.Status = "open"
			}
		}

		if lift.Status == "open" && lift.ThroughputRate > 0 {
			lift.WaitTimeMinutes = round1((float64(lift.QueueLength) / float64(lift.ThroughputRate)) * 60)
		} else {
			lift.WaitTimeMinutes = 0
		}
		lift.Timestamp = g.currentTime
	}
}

func (g *DataGenerator) updateSafety() {
	cfg := g.config.Safety
	riskDelta := g.randomFloat(-cfg.RiskDrift, cfg.RiskDrift)
	if g.weather.WindSpeed > 50 {
		riskDelta += cfg.RiskDrift * 0.5
	}
	if g.weather.SnowIntensity > 3 {
		riskDelta += cfg.RiskDrift * 0.5
	}

	g.safety.AvalancheRiskIndex = clamp(g.safety.AvalancheRiskIndex+riskDelta, 0, 1)
	if g.rng.Float64() < cfg.IncidentProbability {
		g.incidentHistory = append(g.incidentHistory, g.generateIncident())
		if len(g.incidentHistory) > 20 {
			g.incidentHistory = append([]IncidentReport(nil), g.incidentHistory[len(g.incidentHistory)-20:]...)
		}
	}

	g.safety.IncidentReports = append([]IncidentReport{}, g.incidentHistory...)
	g.safety.Timestamp = g.currentTime
}

func (g *DataGenerator) generateIncident() IncidentReport {
	incidentTypes := []string{"minor_injury", "collision", "lost_person", "equipment_failure"}
	if g.safety.AvalancheRiskIndex > 0.7 {
		incidentTypes = append(incidentTypes, "avalanche_warning", "avalanche_warning")
	}

	incidentType := g.randomChoice(incidentTypes)
	severityOptions := map[string][]string{
		"minor_injury":      {"low", "medium"},
		"collision":         {"low", "medium", "high"},
		"lost_person":       {"medium", "high"},
		"equipment_failure": {"low", "medium", "high"},
		"avalanche_warning": {"high", "critical"},
	}

	locations := make([]string, 0, len(g.slopes)+len(g.lifts))
	for _, slope := range g.slopes {
		locations = append(locations, slope.Name)
	}
	for _, lift := range g.lifts {
		locations = append(locations, lift.Name)
	}

	return IncidentReport{
		IncidentType: incidentType,
		Location:     g.randomChoice(locations),
		Severity:     g.randomChoice(severityOptions[incidentType]),
		Timestamp:    g.currentTime,
	}
}

func (g *DataGenerator) updateSlopes() {
	cfg := g.config.Slopes
	for i := range g.slopes {
		slope := &g.slopes[i]
		depthDelta := g.randomFloat(-cfg.DepthDrift, cfg.DepthDrift)
		if g.weather.SnowIntensity > 1 {
			depthDelta += g.weather.SnowIntensity * 0.1
		}
		slope.SnowDepthCM = round1(math.Max(0, slope.SnowDepthCM+depthDelta))

		if slope.Difficulty == "black" && g.safety.AvalancheRiskIndex > 0.8 {
			slope.IsOpen = false
		}
		if (slope.Difficulty == "black" || slope.Difficulty == "red") && g.weather.WindSpeed > 60 {
			slope.IsOpen = false
		}
		if !slope.IsOpen && g.rng.Float64() < cfg.ReopenProbability {
			avalancheClosure := slope.Difficulty == "black" && g.safety.AvalancheRiskIndex > 0.8
			windClosure := (slope.Difficulty == "black" || slope.Difficulty == "red") && g.weather.WindSpeed > 60
			if !avalancheClosure && !windClosure {
				slope.IsOpen = true
			}
		}

		if (slope.Difficulty == "green" || slope.Difficulty == "blue") && g.rng.Float64() < cfg.GroomProbability {
			slope.Groomed = true
		} else if slope.Groomed && g.rng.Float64() < cfg.UngroomProbability {
			slope.Groomed = false
		}
	}
}

func (g *DataGenerator) nextUpdateInterval() time.Duration {
	g.mu.RLock()
	interval := g.config.UpdateIntervalSeconds
	g.mu.RUnlock()

	seconds := g.randomFloat(interval.Min, interval.Max)
	return time.Duration(seconds * float64(time.Second))
}

func (g *DataGenerator) randomFloat(minimum, maximum float64) float64 {
	return minimum + g.rng.Float64()*(maximum-minimum)
}

func (g *DataGenerator) randomInt(minimum, maximum int) int {
	return minimum + g.rng.Intn(maximum-minimum+1)
}

func (g *DataGenerator) randomChoice(values []string) string {
	return values[g.rng.Intn(len(values))]
}

func routes(generator *DataGenerator) http.Handler {
	mux := http.NewServeMux()

	mux.HandleFunc("/health", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"status": "healthy", "service": "data-generator"})
	}))

	mux.HandleFunc("/api/current-state", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, generator.State())
	}))
	mux.HandleFunc("/api/current-state/weather", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, generator.Weather())
	}))
	mux.HandleFunc("/api/current-state/lifts", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, generator.Lifts())
	}))
	mux.HandleFunc("/api/current-state/safety", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, generator.Safety())
	}))
	mux.HandleFunc("/api/current-state/slopes", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, generator.Slopes())
	}))

	mux.HandleFunc("/api/weather", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, generator.Weather())
	}))
	mux.HandleFunc("/api/lifts", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, generator.Lifts())
	}))
	mux.HandleFunc("/api/lifts/", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		liftID := strings.TrimPrefix(r.URL.Path, "/api/lifts/")
		if liftID == "" || strings.Contains(liftID, "/") {
			writeError(w, http.StatusNotFound, "Lift not found")
			return
		}

		lift, ok := generator.LiftByID(liftID)
		if !ok {
			writeError(w, http.StatusNotFound, "Lift '"+liftID+"' not found")
			return
		}
		writeJSON(w, http.StatusOK, lift)
	}))
	mux.HandleFunc("/api/safety", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, generator.Safety())
	}))
	mux.HandleFunc("/api/slopes", method(http.MethodGet, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, generator.Slopes())
	}))

	return mux
}

func method(allowed string, next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != allowed {
			w.Header().Set("Allow", allowed)
			writeError(w, http.StatusMethodNotAllowed, "Method not allowed")
			return
		}
		next(w, r)
	}
}

func withCORS(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "*")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	encoder := json.NewEncoder(w)
	encoder.SetEscapeHTML(false)
	if err := encoder.Encode(value); err != nil {
		slog.Error("writing JSON response", "error", err)
	}
}

func writeError(w http.ResponseWriter, status int, detail string) {
	writeJSON(w, status, map[string]string{"detail": detail})
}

func cloneLifts(lifts []LiftData) []LiftData {
	cloned := make([]LiftData, len(lifts))
	for i, lift := range lifts {
		cloned[i] = cloneLift(lift)
	}
	return cloned
}

func cloneLift(lift LiftData) LiftData {
	lift.ServesSlopes = append([]string{}, lift.ServesSlopes...)
	return lift
}

func cloneSafety(safety SafetyData) SafetyData {
	safety.IncidentReports = append([]IncidentReport{}, safety.IncidentReports...)
	return safety
}

func cloneSlopes(slopes []SlopeData) []SlopeData {
	return append([]SlopeData{}, slopes...)
}

func clamp(value, minimum, maximum float64) float64 {
	return math.Max(minimum, math.Min(maximum, value))
}

func round1(value float64) float64 {
	return math.Round(value*10) / 10
}

func getenv(key, fallback string) string {
	value := os.Getenv(key)
	if value == "" {
		return fallback
	}
	return value
}
