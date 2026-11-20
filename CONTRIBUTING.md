# Contributing to OpenPost

Thanks for taking the time to improve a self-hosted mail server. This document keeps contributions
fast to review.

## Ground rules

- Be civil. Assume the other person is tired, not malicious.
- Security issues go to the process in [SECURITY.md](SECURITY.md), never a public issue.
- One logical change per pull request. Refactors and behaviour changes in the same PR are hard to review.

## Local setup

```bash
# database
docker run -d --name openpost-pg -p 5432:5432 \
  -e POSTGRES_DB=openpost -e POSTGRES_USER=openpost -e POSTGRES_PASSWORD=openpost postgres:17-alpine

# api (watch mode)
cd src/OpenPost.Api && dotnet watch run --urls http://localhost:5080

# web
cd src/web && npm install && npm start
```

## Before you open a PR

```bash
dotnet build OpenPost.slnx -warnaserror     # API must compile with no warnings
dotnet test  OpenPost.slnx                  # unit tests
cd src/web && npm run build                 # Angular production build (also typechecks templates)
```

Both of those run in CI (`.github/workflows/ci.yml`); a red pipeline blocks the merge.

## Code conventions

**Backend**
- File-scoped namespaces, `sealed` classes by default, records for DTOs.
- One controller per resource area; controllers stay thin and delegate to `Mail/*` services.
- Every endpoint that touches a mailbox calls `IMailboxAccess.GetAsync` first — do not add a second
  authorisation pattern.
- Throw `NotFoundException` / `ForbiddenException` / `ConflictException` / `ValidationFailedException`;
  `ErrorHandlingMiddleware` turns them into RFC 7807 responses. Never return raw exception text.
- Do not log message bodies, passwords or tokens. Log addresses, ids and outcomes.

**Frontend**
- Standalone components, signals for state, `inject()` instead of constructor parameters.
- All HTTP goes through `ApiService`; components never call `HttpClient` directly.
- HTML mail is only ever rendered inside the sandboxed iframe. Do not add `innerHTML` bindings for
  message content.
- Styling uses the tokens in `src/styles.css` (`--surface`, `--brand-500`, …) so dark mode keeps working.

**Database**
- New columns need an EF migration and an index plan in the PR description.
- Anything hot-path must be index-backed: mailbox+received-at, mailbox+uid, queue status+next-attempt.

## Adding a feature: the short checklist

1. Entity + `OnModelCreating` mapping (indexes, jsonb, enum-as-text).
2. Service in `Mail/` or `Services/` with an interface.
3. Controller endpoint + DTOs in `Contracts/Dtos.cs`.
4. Angular model in `core/models.ts`, method in `ApiService`, UI.
5. Unit test for the non-obvious logic; extend `docs/ARCHITECTURE.md` if a flow changed.

## Commit messages

`area: imperative summary` — e.g. `smtp: reject recipients over the per-domain limit`,
`web: keep unread count in sync after bulk delete`. Explain *why* in the body when the change is
non-obvious.

## Areas where help is most welcome

- IMAP gateway (MailKit server side) so desktop and mobile clients can connect.
- SRS rewriting for forwarded mail.
- `Testcontainers` integration tests for `MessageStore` and the queue claim query.
- CalDAV/CardDAV, server-side filter rules, and web push.
- Translations of the web client.
