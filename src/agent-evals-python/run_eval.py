"""
Run a Foundry cloud evaluation of the Ski Resort Advisor agent.

Scenario: agent-target evaluation. Queries from data/ski_advisor_eval.jsonl are sent
to the deployed advisor (a Foundry hosted agent), and each response is scored by:
  - a custom RUBRIC evaluator (ski_advisor_rubric) — the primary quality measure, and
  - built-in safety (violence) and coherence evaluators.

Prerequisites (see README.md):
  * az login with the Foundry User role on the project.
  * A deployed advisor agent (FOUNDRY_AGENT_NAME) to target.
  * Environment:
      AZURE_AI_PROJECT_ENDPOINT      e.g. https://<account>.services.ai.azure.com/api/projects/<project>
      AZURE_AI_MODEL_DEPLOYMENT_NAME judge model deployment (default: gpt41)
      FOUNDRY_AGENT_NAME             advisor agent name (default: advisor-agent)
      FOUNDRY_AGENT_VERSION          optional; latest if omitted
      RUBRIC_EVALUATOR_NAME          registered rubric name (default: ski_advisor_rubric)

This is a run-on-demand harness: it is NOT wired into the AppHost or the default test run.
"""
import json
import os
import time
from pathlib import Path

from azure.core.exceptions import ResourceNotFoundError
from azure.identity import DefaultAzureCredential
from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    Dimension,
    EvaluatorCategory,
    EvaluatorType,
    EvaluatorVersion,
    RubricBasedEvaluatorDefinition,
)

HERE = Path(__file__).parent
DATASET_PATH = HERE / "data" / "ski_advisor_eval.jsonl"
RUBRIC_PATH = HERE / "rubrics" / "ski_advisor_rubric.json"


def _require(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise SystemExit(f"Missing required environment variable: {name}")
    return value


def ensure_rubric_registered(project_client: AIProjectClient, name: str) -> None:
    """Register the ski-advisor rubric evaluator from JSON if it isn't in the catalog yet.

    Custom rubric evaluators must exist in the project's evaluator catalog before an
    evaluation can reference them by name. This registers it (idempotently) from
    rubrics/ski_advisor_rubric.json so no manual portal step is required.
    """
    try:
        project_client.beta.evaluators.get_version(name=name, version="1")
        print(f"Rubric evaluator '{name}' already registered; reusing it.")
        return
    except ResourceNotFoundError:
        pass

    criteria = json.loads(RUBRIC_PATH.read_text(encoding="utf-8"))
    dimensions = [
        Dimension(
            id=c["id"],
            description=c["description"],
            weight=int(c["weight"]),
            always_applicable=bool(c.get("always_applicable", False)),
        )
        for c in criteria
    ]
    evaluator_version = EvaluatorVersion(
        display_name="Ski Advisor Rubric",
        evaluator_type=EvaluatorType.CUSTOM,
        categories=[EvaluatorCategory.QUALITY],
        definition=RubricBasedEvaluatorDefinition(dimensions=dimensions),
    )
    project_client.beta.evaluators.create_version(name=name, evaluator_version=evaluator_version)
    print(f"Registered rubric evaluator '{name}' with {len(dimensions)} dimensions.")


def main() -> None:
    endpoint = _require("AZURE_AI_PROJECT_ENDPOINT")
    judge_model = os.environ.get("AZURE_AI_MODEL_DEPLOYMENT_NAME", "gpt41")
    agent_name = os.environ.get("FOUNDRY_AGENT_NAME", "advisor-agent")
    agent_version = os.environ.get("FOUNDRY_AGENT_VERSION", "")
    rubric_name = os.environ.get("RUBRIC_EVALUATOR_NAME", "ski_advisor_rubric")
    # Datasets are immutable per (name, version); use a fresh version each run so re-runs
    # don't fail with "already exists". Override with DATASET_VERSION to reuse a version.
    dataset_version = os.environ.get("DATASET_VERSION") or time.strftime("%Y%m%d%H%M%S")

    with (
        DefaultAzureCredential() as credential,
        AIProjectClient(endpoint=endpoint, credential=credential) as project_client,
        project_client.get_openai_client() as openai_client,
    ):
        # 0) Ensure the custom rubric evaluator exists in the project catalog.
        ensure_rubric_registered(project_client, rubric_name)

        # 1) Upload the query dataset (versioned in the project).
        data_id = project_client.datasets.upload_file(
            name="ski-advisor-eval",
            version=dataset_version,
            file_path=str(DATASET_PATH),
        ).id
        print(f"Uploaded dataset ski-advisor-eval:{dataset_version}")

        # 2) Schema + evaluators. The rubric is the primary quality gate; safety and
        #    coherence are built-in guards. Responses come from the agent target at
        #    runtime, referenced via {{sample.output_text}}.
        data_source_config = {
            "type": "custom",
            "item_schema": {
                "type": "object",
                "properties": {"query": {"type": "string"}},
                "required": ["query"],
            },
            "include_sample_schema": True,
        }

        testing_criteria = [
            {
                "type": "azure_ai_evaluator",
                "name": "ski_advisor_rubric",
                "evaluator_name": rubric_name,
                "initialization_parameters": {"model": judge_model},
                "data_mapping": {
                    "query": "{{item.query}}",
                    "response": "{{sample.output_text}}",
                },
            },
            {
                "type": "azure_ai_evaluator",
                "name": "violence",
                "evaluator_name": "builtin.violence",
                "data_mapping": {
                    "query": "{{item.query}}",
                    "response": "{{sample.output_text}}",
                },
            },
            {
                "type": "azure_ai_evaluator",
                "name": "coherence",
                "evaluator_name": "builtin.coherence",
                "initialization_parameters": {"model": judge_model},
                "data_mapping": {
                    "query": "{{item.query}}",
                    "response": "{{sample.output_text}}",
                },
            },
        ]

        eval_object = openai_client.evals.create(
            name="Ski Advisor rubric evaluation",
            data_source_config=data_source_config,
            testing_criteria=testing_criteria,
        )

        # 3) Agent-target run: send each query to the advisor and score the response.
        target = {"type": "azure_ai_agent", "name": agent_name}
        if agent_version:
            target["version"] = agent_version

        data_source = {
            "type": "azure_ai_target_completions",
            "source": {"type": "file_id", "id": data_id},
            "input_messages": {
                "type": "template",
                "template": [
                    {
                        "type": "message",
                        "role": "user",
                        "content": {"type": "input_text", "text": "{{item.query}}"},
                    }
                ],
            },
            "target": target,
        }

        eval_run = openai_client.evals.runs.create(
            eval_id=eval_object.id,
            name="ski-advisor-eval-run",
            data_source=data_source,
        )

        print(f"Evaluation run started: {eval_run.id}")

        # 4) Poll until complete, then summarize.
        while True:
            run = openai_client.evals.runs.retrieve(run_id=eval_run.id, eval_id=eval_object.id)
            if run.status in ("completed", "failed"):
                break
            print(f"  status: {run.status} ...")
            time.sleep(5)

        print(f"\nRun status: {run.status}")
        report_url = getattr(run, "report_url", None)
        if report_url:
            print(f"Report: {report_url}")

        per_criteria = getattr(run, "per_testing_criteria_results", None) or []
        if per_criteria:
            print("\nPer-evaluator pass rates:")
            for c in per_criteria:
                name = c.get("name") if isinstance(c, dict) else getattr(c, "name", "?")
                passed = c.get("passed") if isinstance(c, dict) else getattr(c, "passed", "?")
                failed = c.get("failed") if isinstance(c, dict) else getattr(c, "failed", "?")
                print(f"  - {name}: passed={passed} failed={failed}")

        if run.status == "failed":
            raise SystemExit("Evaluation run failed. See the report URL for details.")


if __name__ == "__main__":
    main()
