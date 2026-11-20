# Architecture

## 1. Components

```
                       ┌──────────────────────────────────────────────┐
  browser ──HTTPS──►   │ nginx: TLS, static Angular, /api reverse proxy│
                       └───────────────┬──────────────────────────────┘
                                       │ HTTP
                       ┌───────────────▼──────────────────────────────┐
  MTA ──SMTP:25──►     │ OpenPost.Api (.NET 10)                        │
  MUA ──SMTP:587──►    │  ├ Controllers  (REST, JWT, RBAC)             │
                       │  ├ SmtpServerHostedService  (inbound/submission)
                       │  ├ OutboundDeliveryWorker   (queue → MX)      │
                       │  ├ MailRouter / MessageStore                  │
                       │  ├ AuthenticationEvaluator (SPF/DKIM/DMARC)   │
                       │  └ SpamFilterChain, ClamAvScanner             │
                       └───────┬─────────────────────────┬────────────┘
                               │ EF Core                 │ filesystem
                       ┌───────▼────────┐        ┌───────▼────────────┐
                       │ PostgreSQL 17  │        │ /var/lib/openpost  │
                       │ metadata, queue│        │ raw .eml, attach.  │
                       └────────────────┘        └────────────────────┘
```

One process hosts both the REST API and the SMTP listeners so a small deployment is a single
container. The two background services (`SmtpServerHostedService`, `OutboundDeliveryWorker`) run
inside that process but are isolated behind `IServiceScopeFactory` scopes, so they never share a
DbContext with a request.

## 2. Data model

| Table | Purpose | Notes |
| --- | --- | --- |
| `AspNetUsers`, `AspNetRoles`, … | Identity | `AppUser` adds display name, quota default, signature, `IsActive` |
| `RefreshTokens` | Rotating refresh tokens | Only SHA-256 hashes stored; `RevokedAt` + `ReplacedByHash` give reuse detection |
| `Domains` | Hosted domains | DKIM key pair, selector, DMARC policy, catch-all mailbox, limits |
| `Mailboxes` | `user@domain` | `NextUid` for IMAP-style UIDs, `UsedBytes`/`QuotaBytes`, `IsSystem`, `IsShared` |
| `Aliases` | Forwarding addresses | `TargetsJson` (jsonb) holds destinations |
| `Folders` | Per-mailbox folders | `Kind` enum as text; system folders cannot be renamed/deleted |
| `Labels` | Per-mailbox labels | Applied through `Messages.LabelsJson` |
| `Messages` | Stored messages | Subject, addresses (jsonb), text/html bodies, flags, spam verdict, `Uid` |
| `Attachments` | Attachment metadata | `MessageId` null while composing; bytes live on disk |
| `Outbound` | Delivery jobs | Rendered MIME path, status, attempts, `NextAttemptAt`, `LockedUntil` |
| `AuditLogs` | Security-relevant actions | Actor, IP, target, jsonb detail |
| `Settings` | Editable instance settings | Key/value |

Deliberate choices:

- **Bodies in the DB, blobs on disk.** Bodies are needed for search and previews; attachments and
  raw `.eml` files are not, so they stay on the volume. The DB therefore stays small enough to
  `pg_dump` in seconds.
- **`Uid` per mailbox.** Allocated with a single atomic
  `UPDATE "Mailboxes" SET "NextUid" = "NextUid" + 1 … RETURNING` so concurrent SMTP sessions never
  collide. It is the foundation for a future IMAP layer.
- **Address lists as jsonb.** `To`/`Cc`/`Bcc` are read as a whole and never joined on, so a child
  table would add cost without benefit. `LabelsJson` follows the same rule.
- **Queue row owns its payload.** The signed MIME is written once; retries re-send identical bytes,
  which keeps DKIM valid and makes failures reproducible.

## 3. Inbound flow

1. `SmtpServerHostedService` accepts the TCP connection, applies a 200-connection cap, and hands it
   to `SmtpSession` with a 5-minute idle timeout.
2. `EHLO` advertises `SIZE`, `8BITMIME`, `PIPELINING`, `ENHANCEDSTATUSCODES`, `STARTTLS` (when a
   certificate is configured) and `AUTH PLAIN LOGIN`.
3. `RCPT TO` resolves each address through `RecipientResolver`: mailbox → alias → catch-all. Unknown
   non-local recipients get `550 5.7.1 Relay access denied`, so the server never becomes an open relay.
4. `DATA` is streamed into a bounded `MemoryStream` (dot-unstuffing applied, size limit enforced
   before memory grows).
5. ClamAV scan (if enabled) → `AuthenticationEvaluator` (SPF, DKIM, DMARC) → `SpamFilterChain`.
6. `MailRouter` delivers to local mailboxes (Junk when the verdict is `Spam`), expands aliases, and
   queues forwards.
7. `MessageStore` allocates a UID, writes the raw MIME, persists attachments, increments
   `UsedBytes`, and enforces the quota (over-quota mail is refused, not silently dropped).

## 4. Outbound flow

1. `MailSubmissionService` renders the MIME from the compose request, stores a copy in `Sent`, then
   splits recipients into local (delivered immediately through `MailRouter`) and remote.
2. `OutboundQueue` DKIM-signs with the sending domain's key and writes the payload to
   `outbound/<id>.eml`, then inserts a `Queued` row.
3. `OutboundDeliveryWorker` claims rows with
   `UPDATE … WHERE "Id" IN (SELECT … FOR UPDATE SKIP LOCKED)` — safe with N replicas.
4. Per recipient domain: MX lookup (falling back to the implicit MX), STARTTLS, `SendAsync`.
5. Success → `Sent`. Transient failure → `Deferred` with backoff `1, 5, 15, 30, 60, 180, 360, 720`
   minutes. Final failure → `Failed` plus a bounce stored in the sender's inbox.

## 5. Security model

- **Access tokens** are 20-minute HMAC-SHA256 JWTs held in memory in the browser; the refresh token
  (14 days, rotated on every use) is the only persisted secret.
- **Authorisation** is enforced twice: route policies (`SystemAdmin`, `DomainAdmin`) and
  `IMailboxAccess`, which every message/mailbox endpoint calls before touching data. Domain
  administrators are scoped by `Domains.OwnerUserId`.
- **HTML mail** is rendered in an iframe with `sandbox=""` — no scripts, no forms, no same-origin —
  plus `referrerpolicy="no-referrer"`, so remote images cannot leak the reader's identity through a
  referrer header.
- **Passwords** require 12+ characters with mixed classes (Identity defaults are tightened in
  `Program.cs`); TOTP is available per user.
- **Rate limiting** on `/auth/*` (10/min per client) and SMTP (messages per IP per minute).
- **Errors** never leak stack traces in Production; every response is RFC 7807 `problem+json` with a
  trace id.

## 6. Scaling and operations

| Concern | Approach |
| --- | --- |
| More API capacity | Run several `api` containers behind nginx. Queue claiming is lock-safe; sessions are stateless |
| Bigger mailboxes | Move `IMessageStorage` to an S3-compatible backend — it is one interface (`WriteAsync`/`OpenReadAsync`) |
| Search at scale | Add a generated `tsvector` column + GIN index and switch `ILIKE` for `websearch_to_tsquery` |
| Backup | `pg_dump -Fc` nightly **and** snapshot the mail volume; both are needed for a restore |
| Observability | `/health` for liveness (DB + storage writable), structured Serilog logs, `/api/v1/admin/stats` |
| Upgrades | Generate EF migrations in CI, run them at start-up, keep the mail volume mounted |

## 7. Testing strategy

- Unit tests (`src/OpenPost.Api.Tests`) cover address validation, DKIM key generation, password
  policy, paging and preview generation.
- CI builds the API, runs the tests, builds the Angular client (production configuration) and
  typechecks it — so a broken template fails the build, not the deploy.
- Recommended next additions: `Testcontainers` for PostgreSQL integration tests of `MessageStore`
  and the queue claim query, and a `swaks`-driven end-to-end SMTP test.
