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
