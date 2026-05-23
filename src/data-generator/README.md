# Data Generator

Real-time ski resort data generator service written in Go.

The service exposes the same REST API consumed by the dashboard and specialist agents:

- `/health`
- `/api/current-state`
- `/api/current-state/weather`
- `/api/current-state/lifts`
- `/api/current-state/safety`
- `/api/current-state/slopes`
- `/api/weather`
- `/api/lifts`
- `/api/lifts/{lift_id}`
- `/api/safety`
- `/api/slopes`

Aspire hosts it with `CommunityToolkit.Aspire.Hosting.Golang`, which runs the app locally and generates the publish-mode container automatically.

## Observability

The service exports OpenTelemetry traces and logs when Aspire injects OTLP environment variables such as `OTEL_EXPORTER_OTLP_ENDPOINT`. HTTP requests are traced automatically, and structured application logs are sent through the OTLP logs exporter.
