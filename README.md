# job-platform-job-svc

Job service for **Vietnam Job Platform** (`pbl6`) — `dut-pbl6-2026`. `Job CRUD` `Category` `Company` `SavedJob` `CompanyJoinRequest` `InviteCode`, `Port 5002` `net10.0`.

## Overview

- `src/Job.Api` — Web API `Swagger` `health` `migrate` command
- `src/Job.Core` — Domain (`Job` `Category` `Company` `SavedJob` `CompanyJoinRequest` `InviteCode` `JobInteraction` `: Entity`)
- `src/Job.Infrastructure` — `JobDbContext` `Npgsql` `Migrations` `SeedData`
- `tests` — `xunit` entity tests
- GitHub Packages feed (`nuget.config`) — `JobPlatform.SharedKernel 0.4.0`; restore needs `GITHUB_TOKEN` with `read:packages`

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

Update SharedKernel: bump the `PackageReference` to the version released from `job-platform-shared` (GitHub Packages).

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

`UseExceptionHandler` + `ILogger`. The host does not migrate or seed on startup: run `dotnet run --project src/Job.Api -- migrate` first (compose service `job-migrate`).

## Docker

```bash
GITHUB_TOKEN=<token with read:packages> docker build --secret id=nuget_token,env=GITHUB_TOKEN -t job .
docker run -p 5002:5002 --env-file .env job
```

The Dockerfile restores `JobPlatform.SharedKernel` from GitHub Packages through the BuildKit secret `nuget_token`; a plain `docker build` without it fails with `NU1301` (`401`). In `job-platform-infra`, `docker-compose.yml` supplies the secret from the `GITHUB_TOKEN` environment variable.

`Dockerfile` `sdk:10.0` `aspnet:10.0` `USER app` `HEALTHCHECK curl /health`.

## Health, logging and code quality

- `GET /health` — cheap liveness, always `{"status":"ok","service":"job"}`.
- `GET /health/ready` — readiness: Postgres (`CanConnect`, 3s timeout). `503` only when a check is unhealthy.
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
| `JOB_INTERNAL_TOKEN_FOR_APP` | — | bearer token app-svc presents on `/api/internal/jobs/{id}`; required outside Development |
| `JOB_INTERNAL_TOKEN_FOR_AUTH` | — | bearer token auth-svc presents on `/api/internal/stats` and the company self-serve lookups; required outside Development |
| `AUTH_SERVICE_URL` | — | auth-svc base URL for the join-request link-company call |
| `AUTH_INTERNAL_TOKEN_FOR_JOB` | — | bearer token job-svc presents on auth-svc `/api/internal/users/{id}/link-company` |
| `COMPANY_JOIN_REQUEST_TTL_DAYS` | `7` | join-request waiting period; expiry is computed on read and stored by a daily job |
| `COMPANY_EMAIL_DOMAIN_BLOCKLIST` | — | optional extra free-provider domains (comma separated) added to the built-in list |
| `INVITE_CODE_TTL_DAYS` | `7` | lifetime of a recruiter invite code (AUTH-01-09); single use |
| `APP_INTERNAL_TOKEN_FOR_JOB` | — | bearer token job-svc presents to app-svc `/api/internal/applications` (backfill) |
| `KAFKA_TOPIC_APPLICATION_EVENTS` | `application-events` | |
| `KAFKA_GROUP_JOB_RECOMMENDER` | `job-svc-recommender` | never the shared `KAFKA_GROUP_ID` |

## Company self-serve sign-up and join requests (AUTH-01-06, AUTH-01-09 to AUTH-01-15)

Company self-serve sign-up is completed by auth-svc; job-svc owns the Company records, the invite codes and the join requests.

- Internal (auth-svc only, bearer `JOB_INTERNAL_TOKEN_FOR_AUTH`, fail-closed `401`, not gateway-routed):
  - `POST /api/internal/companies/availability` — `{ companyName, taxCode? }` → `{ available }`. Both values are in the body, so they stay out of access logs.
  - `POST /api/internal/companies/domain-match` — `{ domain }` → `{ companyId }` or `{ companyId: null }` for the verified Company that holds the domain.
  - `POST /api/internal/companies` — `{ name, taxCode?, createdBy, emailDomain }` → `{ companyId, emailDomain, verified }`. Idempotent for the same founder, name, tax code and domain.
  - `POST /api/internal/company-join-requests` — `{ userId, companyId }` → the pending request, or `409 JOIN_REQUEST_EXISTS`.
  - `POST /api/internal/company-join-requests/{id}/support-decision` — `{ decision, reason, operator? }` for an Escalated request.
- Requester: `GET /api/companies/me/join-request` (404 when there is none; the view carries `status`, `expiresAt`, `createdAt`, `canEscalate`) and `POST /api/companies/me/join-request/escalate` (once, only when Expired; otherwise `409 JOIN_REQUEST_STATE_INVALID`).
- Company admin (`companies.created_by`): `GET /api/companies/{id}/join-requests` (1-based page, size 1–100), `POST /api/companies/{id}/join-requests/{requestId}/approve` and `.../reject`. Approval calls auth-svc `POST /api/internal/users/{userId}/link-company`; if that fails the request stays Pending, so the admin can retry.
- Waiting period: `COMPANY_JOIN_REQUEST_TTL_DAYS` (default 7). Expiry is computed on read, and a daily job stores the `Expired` status.
- Invite codes: `POST /api/companies/{id}/invite-codes` `{ email }` (Recruiter and company creator only; the plain code is shown once). Internal auth-svc routes are `POST /api/internal/invite-codes/check`, `/redeem` (both `{ code, email }`) and `/{id}/release`. Every bad code, including one bound to another address, is `422 INVITE_CODE_INVALID`.

Env: `AUTH_SERVICE_URL` (auth-svc base URL for the link-company call), `AUTH_INTERNAL_TOKEN_FOR_JOB` (token job-svc presents to auth-svc), `COMPANY_JOIN_REQUEST_TTL_DAYS` (default 7), `COMPANY_EMAIL_DOMAIN_BLOCKLIST` (optional extra free-provider domains, comma separated).

## Troubleshooting

- `dotnet: command not found` → `mise trust` not run
- `NU1301 401` from GitHub Packages → set `GITHUB_TOKEN` with `read:packages` (the package is public, but the feed still requires authentication)
- `password authentication failed` → `cd ../job-platform-infra && docker compose up -d && docker compose ps`
- `mise run verify` not 14 → re-run `mise run sync-env`

`feature/* → main` (see `job-platform-docs/.github/git-strategy.md`).

