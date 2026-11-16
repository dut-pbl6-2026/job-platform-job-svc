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
src/Job.Api            → Web API (Program.cs JWT Bearer + Swagger + /health; `migrate` one-off command)
src/Job.Core           → Domain (Job, Category, Company, SavedJob, CompanyJoinRequest, InviteCode, JobInteraction : Entity)
src/Job.Infrastructure → Data (JobDbContext Npgsql, Migrations) + Services
tests/Job.Tests        → xunit
JobService.sln         → mise run build/test
```

Dependency: `Api → Infrastructure → Core → SharedKernel` (`PackageReference JobPlatform.SharedKernel 0.4.0` via the GitHub Packages feed in `nuget.config`, never `ProjectReference` per `master-plan.md:132`). `MAINT-01` clean arch, `Result<T>` for domain failures not exceptions.

## SRS mapping (JOB-01)

- `POST /api/jobs` create job (Recruiter, required: title, description, company_id FK, location, salary_min/max, category_id, requirements), status='active'.
- `GET /api/jobs/recruiter` list recruiter's jobs, pagination (page, size), filter by status.
- `PUT /api/jobs/{id}` update job (owner only), auto-update timestamp.
- `DELETE /api/jobs/{id}` soft delete (owner only), mark as deleted.
- `GET /api/jobs/{id}` get job details, only active/pending (not deleted).
- `GET /api/categories` list categories (no auth required). Predefined: IT, Finance, Marketing, Healthcare, Education, Engineering, Sales, Hospitality, Others.
- Company self-serve (AUTH-01-10 to AUTH-01-14): internal `POST /api/internal/companies/availability` (`{companyName,taxCode?}` → `{available}`), `/domain-match` (`{domain}` → `{companyId}`), `/companies` (`{name,taxCode?,createdBy,emailDomain}` → `{companyId,emailDomain,verified}`), `/company-join-requests` and `/{id}/support-decision`; all bearer `JOB_INTERNAL_TOKEN_FOR_AUTH`, fail-closed, not gateway-routed.
- Join requests: `GET /api/companies/me/join-request`, `POST /api/companies/me/join-request/escalate`, admin `GET /api/companies/{id}/join-requests` and `POST .../{requestId}/approve|reject`. Waiting period `COMPANY_JOIN_REQUEST_TTL_DAYS` (default 7); approval calls auth-svc link-company and leaves the request Pending when that call fails.
- Invite codes: `POST /api/companies/{id}/invite-codes` `{email}` (Recruiter + creator), internal `check`/`redeem`/`release` with `{code,email}`; bound to one address, every bad code is `422 INVITE_CODE_INVALID`.
- Gateway `GW-01` routes `/api/jobs/*` → Job Service, validates JWT then forwards `X-User-Id/Role`.

## JWT (SharedKernel JwtOptions)

Gateway validates JWT before forwarding `X-User-Id` / `X-User-Role` headers. Job service also validates the bearer itself outside Development (`Auth/JwtBearerRs256.cs`, RS256 only, keys from auth-svc's JWKS via `Jwt__MetadataAddress`, no shared secret). Identity still comes from the gateway headers: when reading identity: `context.Request.Headers["X-User-Id"]` → `Guid.Parse(...)`. Validate non-empty headers on every protected endpoint.

Validate in `Program.cs` (when JWT Bearer is added in W2): `ValidateIssuer=true Audience=job-platform ClockSkew=Zero`. Reference: `shared/JwtOptions.cs SectionName=Jwt`.

## Security (SRS 6 `SEC-*`)

- `SEC-05 SQLi` — always use EF Core parameterized queries (`FromSql` with interpolation or `SqlParameter`), never raw string concat.
- `SEC-05 CSRF` — API is stateless JWT; no form-based auth, CSRF N/A. Validate `Content-Type: application/json` on POST/PUT.
- `SEC-06 rate limit` — 100 req/min per IP+user via gateway (GW-01); no local rate limit needed in W1.
- `SEC-10 CORS` — trusted origins enforced by gateway; no CORS config in job-svc.

## Reliability (NFR `REL-01`)

- Retry 3× with exponential backoff on transient DB errors (Polly or EF resilience strategy — add in W2 with Kafka).
- DB pooling: `MaxPoolSize=20` via connection string env (`DATABASE_URL_JOB`), not hardcoded.
- Schema migration is a separate deploy step: `dotnet Job.Api.dll migrate` (`Bootstrap/MigrateCommand.cs`) exits non-zero on failure; the web host never migrates on startup. An unmigrated DB → requests fail, so the compose `job-migrate` service must succeed before `job` starts.

## Data — EF Core (NFR `6-nfr.md:MAINT`)

- `JobDbContext: DbSet<JobPosting,Category,Company,SavedJob,OutboxEvent,JobInteraction,InviteCode,CompanyJoinRequest>` `UseNpgsql(ConnectionStrings:JobDb / DATABASE_URL_JOB)`.
- Fluent: `Category Name unique 128 required Description`, `Company Name unique 256 TaxCode unique nullable Verified default false LogoUrl Website Description Address Industry Size`, `Job Title 256 required Description required CompanyId FK Location SalaryMin SalaryMax SalaryCurrency CategoryId FK Requirements Benefits EmploymentType 64 ExperienceLevel 64 RecruiterId Status(JobStatus enum → string 32) HasQueryFilter(Status!=Deleted) ViewCount default 0`, `SavedJob UserId JobId FK` (SRS `saved_at` = `Entity.CreatedAt`, no extra column).
- Migrations `src/Job.Infrastructure/Data/Migrations/` — `mise run ef-check` in PR+CI, applied by the `migrate` command.
- Seed predefined categories in the `migrate` command, idempotent (IT, Finance, Marketing, Healthcare, Education, Engineering, Sales, Hospitality, Others).

## API Response Standards (7-eir.md:7.7)

**HTTP status codes** (7-eir.md:7.7.1): `200 OK` list/detail | `201 Created` POST | `204 No Content` DELETE | `400 Bad Request` invalid input | `401 Unauthorized` bad/missing token | `403 Forbidden` wrong role | `404 Not Found` | `409 Conflict` duplicate | `422 Unprocessable Entity` validation | `429 Too Many Requests` rate limit | `500 Internal Server Error`.

**Error response format** (7-eir.md:7.7.2) — `ProblemDetails` + `UseExceptionHandler()` in `Program.cs` produces RFC 7807 JSON automatically:

```json
{
  "status": 400,
  "timestamp": "2026-08-17T10:30:00Z",
  "error": "Bad Request",
  "message": "Title is required",
  "path": "/api/jobs",
  "details": { "field": "title", "issue": "Title cannot be empty" }
}
```

For W2 `feat/job-crud-api`: extend `ProblemDetails` with `timestamp` + `path` + `details` via `IProblemDetailsService` or a custom `ExceptionHandler` middleware.

## Events (SRS 8.5)

Publisher of `job.created`, `job.updated`, `job.deleted` events via Kafka topic `job-events` (Week 2-3, after Kafka config by TM1). Payload: `job_id, title, company_id, company_name, location, category, recruiter_id`. Consumer: `search-svc` indexes to ES, `notif-svc` sends notifications.

## No hard-coding (STRICT — apply to every file you touch)

**NEVER** embed literal values for any of the following in source code (`.cs`, `.json`, `.yaml`, `.toml`, …):

| Category | Examples of forbidden literals |
|----------|--------------------------------|
| Connection strings | `Host=localhost;Port=5432;Password=postgres` |
| Ports / URLs | `http://localhost:5002`, `5432` |
| Secrets / passwords | any plain-text password, API key, JWT secret |
| Database / index names | `job_platform_job` (except in migrations) |

**Always** read from `IConfiguration` / environment variables:

```csharp
// CORRECT — configuration first, env var fallback, no literal fallback
var conn = builder.Configuration.GetConnectionString("JobDb")
           ?? builder.Configuration["DATABASE_URL_JOB"]
           ?? throw new InvalidOperationException(
               "Connection string not configured. Set DATABASE_URL_JOB or ConnectionStrings:JobDb.");
```

- `appsettings.json` MAY contain **placeholder comments** like `"<set via env>"` but MUST NOT contain real credentials or real hostnames.
- `appsettings.Development.json` MAY point to `localhost` **only** for local-dev convenience; never commit real passwords.
- The single source of truth for all env values is `../job-platform-infra/envs/.env.dev.example` — use `mise run sync-env` to pull it.
- Required env vars for this service: `DATABASE_URL_JOB`, `Jwt__MetadataAddress` (outside Development), `AUTH_SERVICE_URL` and `AUTH_INTERNAL_TOKEN_FOR_JOB` for join-request approval, `COMPANY_JOIN_REQUEST_TTL_DAYS` (default 7), optional `COMPANY_EMAIL_DOMAIN_BLOCKLIST`.

## 2026 best practice (NFR `MAINT`)

- `dotnet 10.0.100` `net10.0` `nullable enable` `ImplicitUsings` file-scoped namespace, `ProblemDetails` + `UseExceptionHandler` + `ILogger` JSON `ERROR/WARN/INFO/DEBUG`, `GET /health` per `8-system-architecture.md`.
- `dotnet build --warnaserror` + `dotnet format --verify-no-changes` (mise `build/test/format`), `EF` alignment `EF10.0.4` + `Npgsql10.0.3`, coverage `>70%` `MAINT-02`.
- Never commit `.env` (`.gitignore`), `mise run sync-env` single source `../job-platform-infra/envs/.env.dev.example` (`DATABASE_URL_JOB`, `Jwt__MetadataAddress`).

## Workflow

```bash
mise trust && mise install
mise run sync-env && mise run verify
mise run build && mise run test && mise run format
mise run ef-check
mise run run  # http://localhost:5002/health → {"status":"ok","service":"job"}
```

## Git convention (git-strategy.md)

Branch: `feature/<description>` | `bugfix/<description>` | `hotfix/v<semver>-<desc>` → `main`.

Commits — `<type>(job): <subject>` (scope always `job` for this repo):

| Type | Example |
|------|---------|
| `feat` | `feat(job): add POST /api/jobs endpoint` |
| `fix` | `fix(job): guard null before Trim in Job ctor` |
| `refactor` | `refactor(job): introduce JobStatus enum` |
| `test` | `test(job): add salary range validation tests` |
| `docs` | `docs(job): update README prerequisites` |
| `chore` | `chore(job): remove unused packages from csproj` |
| `ci` | `ci(job): replace ci placeholder with real steps` |

PR checklist: Description / How to verify / Checklist `mise run build/test/format/ef-check`.

