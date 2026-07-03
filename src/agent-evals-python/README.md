# Ski Resort Advisor — Foundry evals

On-demand Foundry cloud evaluation of the **advisor agent**. Queries in
[`data/ski_advisor_eval.jsonl`](data/ski_advisor_eval.jsonl) are sent to the deployed
advisor (a Foundry hosted agent), and each response is scored by a custom **rubric**
evaluator plus built-in **safety** and **coherence** evaluators.

This harness is intentionally **not** wired into the AppHost or the default test run —
it targets a deployed agent and incurs model-inference cost, so you run it manually.

## Why Python

Foundry's evaluation surface (built-in evaluators, rubric evaluators, agent-target and
trace evaluation) is documented and SDK-supported in **Python and cURL only** — there is
no .NET evaluation SDK at parity. Evals run through the OpenAI evals API exposed by
`azure-ai-projects` (`project_client.get_openai_client().evals`).

## Prerequisites

1. `az login`, with the **Foundry User** role on the project.
2. A deployed advisor agent (run `aspire deploy`, or provision the Foundry project so the
   agent exists).

The rubric in [`rubrics/ski_advisor_rubric.json`](rubrics/ski_advisor_rubric.json) is
**registered automatically** by the script (idempotently) in the project's evaluator
catalog — no manual portal step needed.

Environment variables:

   | Variable | Required | Default | Notes |
   | --- | --- | --- | --- |
   | `AZURE_AI_PROJECT_ENDPOINT` | yes | — | `https://<account>.services.ai.azure.com/api/projects/<project>` |
   | `AZURE_AI_MODEL_DEPLOYMENT_NAME` | no | `gpt41` | Judge model deployment (rubric + coherence) |
   | `FOUNDRY_AGENT_NAME` | no | `advisor-agent` | The deployed advisor agent name — verify this matches the registered agent |
   | `FOUNDRY_AGENT_VERSION` | no | latest | Pin a version if desired |
   | `RUBRIC_EVALUATOR_NAME` | no | `ski_advisor_rubric` | Name to register/reference the rubric under |

## Run

```pwsh
uv sync
uv run python run_eval.py
```

The script registers the rubric evaluator (if needed), uploads the dataset, creates the
evaluation, runs it against the agent target, polls until completion, and prints
per-evaluator pass rates plus the Foundry report URL.
