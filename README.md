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

## Troubleshooting

- `dotnet: command not found` → `mise trust` not run
- `NU1301 local source` → `mise run pack-shared`
- `password authentication failed` → `cd ../job-platform-infra && docker compose up -d && docker compose ps`
- `mise run verify` not 14 → re-run `mise run sync-env`

`feature/* → main` (see `job-platform-docs/.github/git-strategy.md`).
