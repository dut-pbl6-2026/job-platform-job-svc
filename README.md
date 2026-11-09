# job-platform-job-svc

Job service for **Vietnam Job Platform** (`pbl6`) — `dut-pbl6-2026`. `Job CRUD` `Category` `Company` `SavedJob`, `Port 5002` `net10.0`.

## Overview

- `src/Job.Api` — Web API `Swagger` `health` `auto-migrate`
- `src/Job.Core` — Domain (`Job` `Category` `Company` `SavedJob` `: Entity`)
- `src/Job.Infrastructure` — `JobDbContext` `Npgsql` `Migrations` `SeedData`
- `tests` — `xunit` entity tests
- `local-feed` — `JobPlatform.SharedKernel 0.1.0` via `nuget.config`

## Prerequisites

- `mise` https://mise.jdx.dev
- `docker` + `docker compose v2`
- `git` + `gh` `gh auth login`
- `dotnet 10.0.100` via `mise` — `mise trust && mise install`

See `AGENTS.md` for shell activation (`mise activate`) and agent `mise exec` notes.

## Clone

```bash
mkdir -p ~/projects/personal/job-platform && cd ~/projects/personal/job-platform
for r in infra shared auth-svc job-svc; do gh repo clone dut-pbl6-2026/job-platform-$r; done
cd job-platform-job-svc
```

## Setup

```bash
mise trust && mise install
mise run sync-env
mise run verify  # 14
cat .env | grep DATABASE_URL_JOB
```

Env single source: `../job-platform-infra/envs/.env.dev.example` → `.env` via `mise run sync-env`.

## Build

```bash
mise run build     # dotnet build --warnaserror
mise run test      # dotnet test
mise run format    # dotnet format --verify-no-changes
mise run ef-check
```

Update SharedKernel: `mise run pack-shared`.

## Run

```bash
mise run run              # dotnet run --project src/Job.Api
curl http://localhost:5002/health   # {"status":"ok","service":"job"}
```

Sample request:

```bash
curl -X POST http://localhost:5002/api/jobs \
  -H "Content-Type: application/json" \
  -H "X-User-Id: <uuid>" \
  -H "X-User-Role: Recruiter" \
  -d '{"title":"Backend Developer","description":"Build and maintain APIs","companyId":"<uuid>","location":"Da Nang","categoryId":"7d09cd31-5580-41a2-948e-00bfbfdc8e3b"}'
```

`auto-migrate` on startup, `UseExceptionHandler` + `ILogger`.

## Docker

```bash
docker build -t job .
docker run -p 5002:5002 --env-file .env job
```

`Dockerfile` `sdk:10.0` `aspnet:10.0` `USER app` `HEALTHCHECK curl /health`.

## Health, logging and code quality

- `GET /health` — cheap liveness, always `{"status":"ok","service":"job"}`.
- `GET /health/ready` — readiness: Postgres (`CanConnect`, 3s timeout) and Kafka circuit-breaker state. `503` only when a check is unhealthy; Kafka down is `degraded` (HTTP sync fallback).
- Logging via Serilog: compact JSON console in non-Development, request logging, `ServiceName` enricher, `X-Correlation-Id` (generated if absent, echoed in the response). Secrets and `Authorization` headers are never logged.
- SonarCloud (optional): `ci.yml` job `sonarcloud` analyses `dut-pbl6-2026_job-platform-job-svc` only when the `SONAR_TOKEN` repo secret exists; otherwise it skips.

## Job recommendations (PBL6-10, RECOMMEND-01)

`GET /api/jobs/recommended?limit=10&refresh=false` — personal recommendations for a job seeker.

| Item | Value |
|------|-------|
| Auth | Role `User`. Missing identity → `401`, other role → `403`, both RFC 7807 Problem Details (`type`, `title`, `status`, `detail`). The gateway enforces the same policy on route `jobs-recommended` |
| Query | `limit` int 1–10 (default 10), `refresh` bool (default `false`). Invalid → `400` `ValidationProblem` with `errors.limit` / `errors.refresh` |
| Paging | None: bounded top-N (≤ 10), not a resource list |

```json
{
  "items": [
    {
      "id": "8c1f…",
      "title": "Lập trình viên .NET",
      "companyId": "a12b…",
      "company": { "id": "a12b…", "name": "Công ty ABC" },
      "categoryId": "c34d…",
      "category": { "id": "c34d…", "name": "IT" },
      "location": "Đà Nẵng",
      "salaryMin": 15000000,
      "salaryMax": 25000000,
      "salaryCurrency": "VND",
      "employmentType": "FullTime",
      "experienceLevel": "Mid",
      "createdAt": "2026-11-02T03:00:00Z",
      "strategy": "Collaborative",
      "reason": "Người ứng tuyển \"Lập trình viên Java\" cũng ứng tuyển việc này"
    }
  ],
  "total": 1,
  "isCached": false,
  "generatedAt": "2026-11-09T02:15:00Z"
}
```

- `items` are in rank order, `total = items.length`. Only `Active` jobs. `items` is empty only when no job is `Active`.
- `company` / `category` are nested (no flat `companyName`); `categoryId` and `category` are `null` when the job has no category. No `description`, no score.
- `strategy` ∈ `Collaborative | Content | Popular` (for tests/analytics). Clients display `reason` (Vietnamese, built from real data only) and translate enum labels themselves.
- Tiers: item-item collaborative filtering over applications → same category/location as jobs the user applied to → popular (applications in the last 30 days, then views). A user with no history gets `Popular` only.
- Data: `JobInteractions` read model fed by Kafka `application-events` (consumer group `KAFKA_GROUP_JOB_RECOMMENDER`, default `job-svc-recommender`) and a one-time startup backfill from app-svc `GET /api/internal/applications` when `RECOMMENDATION_BACKFILL_ENABLED=true`.
- Cache: in-process, per user, `RECOMMENDATION_CACHE_TTL_HOURS` (6). A new application event for the user clears it; `refresh=true` recomputes (at most once a minute per user). The cache is per replica (one replica today).

| Env | Default | Notes |
|-----|---------|-------|
| `RECOMMENDATION_CACHE_TTL_HOURS` | 6 | |
| `RECOMMENDATION_POPULAR_TTL_MINUTES` | 15 | shared popular list |
| `RECOMMENDATION_MAX_NEIGHBORS` | 500 | |
| `RECOMMENDATION_MIN_COOCCURRENCE` | 1 | |
| `RECOMMENDATION_BACKFILL_ENABLED` | false | compose turns it on |
| `APP_SERVICE_URL` | — | required when backfill is on |
| `INTERNAL_SERVICE_TOKEN` | — | bearer token for app-svc |
| `KAFKA_TOPIC_APPLICATION_EVENTS` | `application-events` | |
| `KAFKA_GROUP_JOB_RECOMMENDER` | `job-svc-recommender` | never the shared `KAFKA_GROUP_ID` |

## Troubleshooting

- `dotnet: command not found` → `mise trust` not run
- `NU1301 local source` → `mise run pack-shared`
- `password authentication failed` → `cd ../job-platform-infra && docker compose up -d && docker compose ps`
- `mise run verify` not 14 → re-run `mise run sync-env`

`feature/* → main` (see `job-platform-docs/.github/git-strategy.md`).

