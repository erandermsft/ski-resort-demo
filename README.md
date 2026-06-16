# 🏔️ AlpineAI – Multi-Agent Ski Resort Demo

A distributed, multi-agent ski resort system built with **Microsoft Agent Framework (MAF)**, **Azure AI Foundry**, the **A2A protocol**, **Voice Live**, and **Aspire**.

An AI-powered ski resort concierge that coordinates weather intelligence, lift traffic, safety evaluation, personalized coaching, web-backed ski research, and voice conversations through a network of specialist agents — all orchestrated by hosted advisor experiences and displayed on a real-time dashboard.

## Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│                         Frontend (Vite + React)                       │
│          Dashboard data + chat via Responses + voice via WS           │
└──────────────┬──────────────────────┬────────────────────────────────┘
               │ REST                 │ Responses API / WebSocket
               ▼                      ▼
┌──────────────────────┐  ┌──────────────────────────┐  ┌──────────────────────────┐
│   Data Generator     │  │   Advisor Agent (.NET)   │  │ Voice Advisor Agent (.NET)│
│       (Go)           │  │ Foundry hosted Responses │  │ Voice Live WebSocket      │
└──────────┬───────────┘  └────────────┬─────────────┘  └────────────┬─────────────┘
           │                           │                             │
           │                  A2A + Foundry tools            A2A + Foundry tools
           │                           │                             │
           ▼                           ▼                             ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Shared specialist/research tools                     │
├─────────────┬─────────────┬─────────────┬─────────────┬─────────────────────┤
│ Weather     │ Lift Traffic│ Safety      │ Ski Coach   │ Ski Researcher      │
│ Agent       │ Agent       │ Agent       │ Agent       │ Foundry Prompt      │
│ (Python)    │ (.NET)      │ (Python)    │ (Python)    │ Agent + Web Search  │
└─────────────┴─────────────┴─────────────┴─────────────┴─────────────────────┘
```

| Component | Language | Role |
|---|---|---|
| **Advisor Agent** | .NET | Foundry-hosted Responses orchestrator — routes chat questions to A2A specialists and the Ski Researcher prompt agent |
| **Voice Advisor Agent** | .NET | Voice Live WebSocket bridge — provides spoken conversations and invokes the same specialist/research tools |
| **Weather Agent** | Python | Current conditions, forecasts, storm alerts |
| **Lift Traffic Agent** | .NET | Lift status, wait times, congestion analysis |
| **Safety Agent** | Python | Risk evaluation, slope safety, closures |
| **Ski Coach Agent** | Python | Personalized slope recommendations, day plans |
| **Ski Researcher Agent** | Azure AI Foundry prompt agent | Web-search-backed general skiing research and background information |
| **Data Generator** | Go | Continuously generates synthetic resort telemetry |
| **Frontend** | React/Vite | Real-time dashboard with AI chat and voice controls |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Python 3.11+](https://www.python.org/downloads/)
- [uv](https://docs.astral.sh/uv/) (Python package manager)
- [Go 1.23+](https://go.dev/doc/install)
- [Node.js 20+](https://nodejs.org/)
- [Aspire CLI](https://aspire.dev/get-started/install-cli/)
- An **Azure AI Foundry** resource with a `gpt-4.1` (or similar) deployment
- **Azure CLI** authenticated (`az login`)

### Install Aspire CLI

Refer to the [official Aspire documentation](https://aspire.dev/get-started/install-cli/) for installation instructions.

## Setup

### 1. Clone the repository

```bash
git clone https://github.com/tommasodotNET/ski-resort-demo.git
cd ski-resort-demo
```

### 2. Configure Azure settings

Edit `src/apphost.settings.Development.json` with your Azure details:

```json
{
    "Azure": {
        "TenantId": "<your-tenant-id>",
        "SubscriptionId": "<your-subscription-id>",
        "AllowResourceGroupCreation": true,
        "ResourceGroup": "<your-resource-group>",
        "Location": "<your-azure-region>",
        "CredentialSource": "AzureCli"
    }
}
```

> **Note:** The Azure AI Foundry resource must have a chat completion model deployed (e.g., `gpt-4.1`). The deployment name is configured in the Aspire AppHost.

### 3. Run the application

From the `src/` directory:

```bash
cd src
aspire run
```

This single command starts **all services**:
- 3 .NET agents/services (advisor + lift traffic + voice advisor)
- 3 Python agents (weather + safety + ski coach)
- 1 Azure AI Foundry prompt agent (ski researcher with web search)
- Data generator (Go)
- Frontend (Vite dev server)
- Cosmos DB emulator

Open the **Aspire dashboard** (URL shown in terminal output) to see all services, logs, and distributed traces.

The **frontend** will be available at the URL assigned by Aspire (shown in the dashboard).

## Project Structure

```
src/
├── apphost.cs                      # Aspire orchestration (all services wired here)
├── apphost.settings.Development.json  # Azure configuration
├── advisor-agent-dotnet/           # .NET hosted Responses advisor agent
├── voice-advisor-agent/            # .NET Voice Live WebSocket advisor
├── lifttrafficagent-dotnet/      # .NET lift traffic agent (A2A)
├── weatheragent-python/           # Python weather agent (A2A)
├── safetyagent-python/            # Python safety agent (A2A)
├── skicoachagent-python/         # Python ski coach agent (A2A)
├── data-generator/                 # Go data generator
├── frontend/                       # Vite + React + Tailwind dashboard
├── shared-services/                # .NET shared library (Cosmos, thread store)
└── service-defaults/               # Aspire service defaults
```

## Configuration

### Data Generator

The data generation speed and drift magnitudes are configurable via `src/data-generator/config.json`:

```json
{
  "update_interval_seconds": { "min": 5, "max": 10 },
  "weather": { "temperature_drift": 0.1, "wind_speed_drift": 0.5, ... },
  "lifts": { "queue_drift": 3, "status_change_probability": 0.002 },
  ...
}
```

### Frontend

The dashboard polling interval is configurable via `src/frontend/public/config.json`:

```json
{
  "pollingIntervalMs": 10000
}
```

Changes are picked up automatically without restarting.

## How It Works

1. **Data Generator** continuously produces synthetic weather, lift, slope, and safety telemetry via a REST API.

2. **Specialist agents** (weather, lift, safety, coach) each wrap specific tools using MAF and expose them over the **A2A protocol**. Each agent calls the data generator's API to fetch current conditions.

3. **Ski Researcher Agent** is an Azure AI Foundry prompt agent with a web search tool. It handles general skiing questions that are not tied to live resort telemetry.

4. **Advisor Agent** is the chat orchestrator. It is published as a Foundry-hosted Responses agent, registers the A2A specialist agents and Ski Researcher as tools, and selectively invokes only the relevant tools based on the user's question.

5. **Voice Advisor Agent** bridges browser audio to Azure AI Voice Live over WebSockets. Voice Live can call the same A2A specialist agents and Ski Researcher prompt agent as function tools during a spoken conversation.

6. **Frontend** displays real-time data panels (weather, lifts, slopes, safety) by polling the data generator, provides an AI chat panel backed by the advisor's Responses endpoint, and offers voice conversations through the voice advisor WebSocket.

## Key Technologies

- **[Microsoft Agent Framework (MAF)](https://github.com/microsoft/agents)** — Agent creation, tool registration, and orchestration
- **[Azure AI Voice Live](https://learn.microsoft.com/azure/ai-services/speech-service/voice-live)** — Realtime speech-to-speech voice conversations
- **[A2A Protocol](https://github.com/google/A2A)** — Agent-to-agent communication over JSON-RPC + SSE streaming
- **[Aspire](https://aspire.dev)** — Distributed app orchestration, service discovery, observability
- **[Azure AI Foundry](https://ai.azure.com)** — LLM backend, hosted Responses agent, prompt agent, web search, and realtime deployment
- **[Vite](https://vitejs.dev) + [React](https://react.dev)** — Frontend dashboard
- **[Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/)** — Conversation thread persistence

## Further Reading

See [ARCHITECTURE.md](ARCHITECTURE.md) for the detailed system architecture document.
