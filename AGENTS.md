# AGENTS — job-platform-job-svc

> Job microservice. SRS: `job-platform-docs/docs/master-plan.md:165`, `docs/srs/en/{3-must-have-fr:JOB-01,8-system-architecture,10-appendices:E.2,6-nfr}`, `7-eir`. Git: `job-platform-docs/.github/git-strategy.md` (`feature/* → main`).

## Mise activation

Activate `mise` for bare `dotnet`/`infisical` without `mise exec`:

| Shell | Add to config file | Activate |
|-------|--------------------|----------|
| `bash` | `~/.bashrc` or `~/.bash_profile` | `eval "$(mise activate bash)"` |
| `zsh` | `~/.zshrc` | `eval "$(mise activate zsh)"` |
| `fish` | `~/.config/fish/config.fish` | `mise activate fish \| source` |
| `PowerShell` | `$PROFILE` | `mise activate pwsh \| Out-String \| Invoke-Expression` |

Agent uses `mise exec -- dotnet ...` / `mise exec -- infisical ...` due to non-interactive shell without `mise activate`; humans just use `dotnet` / `infisical` after `mise install`.

## Scope

`PBL6-17` MUST `JOB-01` — Job CRUD + Category management, `Port 5002` `net10.0` `YARP gateway`. Owner TM2 W2. DB `job_platform_job`.

## Architecture — clean Api/Core/Infrastructure

```
src/Job.Api            → Web API (Program.cs JWT Bearer + Swagger + /health + auto-migrate)
src/Job.Core           → Domain (Job, Category, Company, SavedJob : Entity)
src/Job.Infrastructure → Data (JobDbContext Npgsql, Migrations) + Services
tests/Job.Tests        → xunit
JobService.sln         → mise run build/test
```

Dependency: `Api → Infrastructure → Core → SharedKernel` (`PackageReference JobPlatform.SharedKernel 0.1.0` via `local-feed` + `nuget.config`, never `ProjectReference` per `master-plan.md:132`). `MAINT-01` clean arch, `Result<T>` for domain failures not exceptions.

## SRS mapping (JOB-01)

- `POST /api/jobs` create job (Recruiter, required: title, description, company_id FK, location, salary_min/max, category_id, requirements), status='active'.
- `GET /api/jobs/recruiter` list recruiter's jobs, pagination (page, size), filter by status.
- `PUT /api/jobs/{id}` update job (owner only), auto-update timestamp.
- `DELETE /api/jobs/{id}` soft delete (owner only), mark as deleted.
- `GET /api/jobs/{id}` get job details, only active/pending (not deleted).
- `GET /api/categories` list categories (no auth required). Predefined: IT, Finance, Marketing, Healthcare, Education, Engineering, Sales, Hospitality, Others.
- Gateway `GW-01` routes `/api/jobs/*` → Job Service, validates JWT then forwards `X-User-Id/Role`.

## Data — EF Core (NFR `6-nfr.md:MAINT`)

- `JobDbContext: DbSet<Job,Category,Company,SavedJob>` `UseNpgsql(ConnectionStrings:JobDb / DATABASE_URL_JOB)`.
- Fluent: `Category Name unique 128 required Description`, `Company Name unique 256 TaxCode unique nullable Verified default false LogoUrl Website Description Address Industry Size`, `Job Title 256 required Description required CompanyId FK LocationFK SalaryMin SalaryMax SalaryCurrency CategoryId FK Requirements Benefits EmploymentType 64 ExperienceLevel 64 RecruiterId Status 32 default Active ViewCount default 0`, `SavedJob UserId JobId FK SavedAt`.
- Migrations `src/Job.Infrastructure/Data/Migrations/` — `mise run ef-check` in PR+CI, auto-migrate on startup with `ILogger`.
- Seed predefined categories on startup (IT, Finance, Marketing, Healthcare, Education, Engineering, Sales, Hospitality, Others).

## Events (SRS 8.5)

Publisher of `job.created`, `job.updated`, `job.deleted` events via Kafka topic `job-events` (Week 2-3, after Kafka config by TM1). Payload: `job_id, title, company_id, company_name, location, category, recruiter_id`. Consumer: `search-svc` indexes to ES, `notif-svc` sends notifications.

## 2026 best practice (NFR `MAINT`)

- `dotnet 10.0.100` `net10.0` `nullable enable` `ImplicitUsings` file-scoped namespace, `ProblemDetails` + `UseExceptionHandler` + `ILogger` JSON `ERROR/WARN/INFO/DEBUG`, `GET /health` per `8-system-architecture.md`.
- `dotnet build --warnaserror` + `dotnet format --verify-no-changes` (mise `build/test/format`), `EF` alignment `EF10.0.4` + `Npgsql10.0.3`, coverage `>70%` `MAINT-02`.
- Never commit `.env` (`.gitignore`), `mise run sync-env` single source `../job-platform-infra/envs/.env.dev.example` (`DATABASE_URL_JOB`, `JWT_SECRET`).

## Workflow

```bash
mise trust && mise install
mise run sync-env && mise run verify
mise run build && mise run test && mise run format
mise run ef-check
mise run run  # http://localhost:5002/health → {"status":"ok","service":"job"}
```

`feature/* → main` (e.g., `feature/job-crud-category`), PR must: Description/How to verify/Checklist `mise run build/test/format/ef-check`.
