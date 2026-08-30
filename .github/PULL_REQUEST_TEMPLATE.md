## Summary

<!-- What + why (1-2 lines). Link Jira: PBL6-17/JOB-01 etc. Branch: feature/* → main -->

## Changes

- [ ] `mise run build` — `dotnet build JobService.sln --warnaserror`
- [ ] `mise run test` / `format` / `ef-check` as needed
- [ ] Env: `mise run sync-env` + `mise run verify` (14) if touching config/.env
- [ ] Docs updated (`README.md` / `AGENTS.md`)

## How to verify

```bash
mise trust && mise install
mise run sync-env
mise run verify  # 14
mise run build && mise run test
docker compose -f ../job-platform-infra/docker-compose.yml ps  # postgres 5432 healthy
dotnet run --project src/Job.Api  # http://localhost:5002/health
```

## Checklist

- [ ] `mise run format` no diff
- [ ] No `.env` committed (`.gitignore`)
- [ ] `SharedKernel` version bump handled via `mise run pack-shared` if needed

Closes #
