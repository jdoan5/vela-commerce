# 0009 — No Log Analytics workspace

**Status:** Accepted · recorded 2026-09-05 · **with corrections to what it claimed** (Phase 0 / infra)

## Context

The Container Apps environment offers to attach a Log Analytics workspace, and every tutorial and the
portal wizard produce three lines that do it. Those three lines have no ceiling: `daily_quota_gb`
defaults to `-1`, meaning unlimited, and retention defaults above the free 31-day window.

For a demo meant to sit idle for years at $0, an unbounded metered resource is the single most likely
way the bill stops being zero — and it would arrive as a surprise, because cost data lags up to 72
hours on Pay-As-You-Go.

## Decision

No workspace. `create_log_analytics_workspace` defaults to `false`, `logs_destination` and
`log_analytics_workspace_id` resolve to `null`, and the environment's log destination is `"none"`.

If it is ever turned on, the resource is written with the ceiling already in place:
`retention_in_days = 30` is a literal rather than a variable, and a daily quota is required. The
deploy identity's role definition also withholds any permission over a Log Analytics workspace, so
the pipeline cannot create one even if the configuration changed.

## Consequences

Azure Monitor cost is $0.00, and Terraform says so in an output rather than leaving it to be
inferred.

What is given up is real: no historical query, no alerting, no retention. Logs are the live stream
(`az containerapp logs show --follow`), which is only useful while someone is watching.

**Three corrections to this record's own original claims**, all verified:

1. **The named fallback does not exist.** `observability.tf` lists "the application's own
   OpenTelemetry export to Grafana Cloud Free" as a surviving capability. There is **no
   OpenTelemetry package anywhere in `src/`**. The gap this decision creates is therefore wider than
   the comment admits, and two comments in the file contradict each other about what covers it.
2. **The arithmetic is wrong.** `0.15 GB/day` is described as "~4.5 GB" per 31-day month in two
   places. It is 4.65, and Terraform's own output computes ~4.7.
3. **The guardrails are comments, not constraints.** `-1` is called "the single edit that converts
   this from a $0 resource into an unbounded one" — and nothing stops that edit. A Terraform
   `validation` block was considered and would **not** help: it only evaluates when the variable is
   set, and `terraform.tfvars` is gitignored, so no credential-free CI check can see the effective
   value. The honest position is that this guardrail is a comment, and calling it anything else would
   be the overstatement this project keeps finding.

The decision stands. Its justification was partly resting on a capability that was never built.
