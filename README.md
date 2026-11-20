# OpenPost

**A self-hosted, open-source email server and webmail you can actually read, run and fork.**
.NET 10 Web API + Angular 20 + PostgreSQL. One `docker compose up` on a single box gives you
inbound SMTP, authenticated submission, DKIM signing, spam/virus hooks, a retry queue and a
webmail client with an admin console.

```
  inbound SMTP :25 ─┐
                    ├─► MailRouter ─► PostgreSQL (metadata) + local disk (MIME & attachments)
  submission :587 ──┘        │
  Angular webmail ──► REST   └─► Outbound queue ─► MX lookup ─► DKIM-signed SMTP delivery
```

---

## Why this stack

| Layer | Choice | Reason |
| --- | --- | --- |
| API | **.NET 10 Web API** | Single static-ish binary, strong typing, first-class async/SMTP sockets, no runtime install on the host |
| UI | **Angular 20** (standalone + signals) | Long-term maintainable SPA, lazy routes, small production bundle (~90 kB gzip initial) |
| Database | **PostgreSQL 17** | See below |
| Blobs | **Local filesystem** | Raw `.eml` and attachments stay on your disk; the DB stays small and fast to back up |

### Database decision: PostgreSQL

Mail is a workload of *many small rows, heavy indexes, strict correctness and concurrent workers*.
PostgreSQL wins on every axis that matters here:

- **`SELECT … FOR UPDATE SKIP LOCKED`** — the outbound queue and UID allocation rely on this. It is
  the cleanest way to run several API replicas against one queue without double-sending. MySQL
  needs workarounds; SQLite serialises writers.
- **`jsonb` + GIN** — address lists, labels and authentication results are stored as JSON with
  indexable operators, so the schema stays honest without a join table per list.
- **Real full-text search** (`tsvector`, `pg_trgm`) — message search starts as `ILIKE` and upgrades
  to ranked FTS without changing the schema.
- **Partial/covering indexes and MVCC** — read-heavy mailbox listing next to write-heavy ingest,
  without readers blocking writers.
- **Operationally boring** — one binary, `pg_dump`/WAL archiving, streaming replicas, runs on a €5
  VPS or any managed service, PostgreSQL licence (not a copyleft trap for your business).
- **Correct types** — `timestamptz`, `bigint` counters, `numeric` for spam scores.

Considered and rejected: **SQL Server** (licence cost, heavy for self-host), **MySQL/MariaDB**
(weaker locking ergonomics, JSON indexing), **SQLite** (single-writer; fine for a 5-user demo, wrong
for a mail queue), **MongoDB** (no transactional guarantees across the mailbox/quota/queue writes we
need), **Redis-only** (not durable enough to be the system of record).

---

## Features

**Mail**
- Inbound ESMTP server (port 25) with `EHLO`, `MAIL`, `RCPT`, `DATA`, `RSET`, `NOOP`, `QUIT`,
  message-size limits, recipient limits and per-IP rate limiting
- Submission server (port 587) with `STARTTLS` and `AUTH PLAIN`/`AUTH LOGIN`
- DKIM signing (RSA-2048, relaxed/relaxed, `rsa-sha256`) with automatic key generation and rotation
- SPF evaluation, DKIM verification and DMARC policy lookup on inbound mail
- Spam scoring: Rspamd (`/checkv2`) when configured, built-in explainable heuristics otherwise
- Optional ClamAV scanning over `INSTREAM` (fails open, loudly logged)
- Local-to-local delivery bypasses SMTP entirely; alias expansion and catch-all mailboxes
- Durable outbound queue: MX resolution, STARTTLS, exponential backoff (1m→12h), RFC 3464 bounces
- Raw `.eml` download for every stored message

**Webmail**
- Three-pane client: folders with unread counts, message list, reading pane
- Compose / reply / forward, attachments, drafts, search, flags, bulk actions
- HTML mail rendered in a `sandbox=""` iframe with `referrerpolicy="no-referrer"` — no scripts, no
  same-origin access, no tracking
- Per-mailbox quota meter, label support, light/dark theme

**Administration**
- Multi-domain hosting with per-domain DKIM, DMARC policy, quotas and mailbox limits
- Mailbox + user management with generated strong passwords and one-click reset
- DNS helper: exactly which MX/SPF/DKIM/DMARC/PTR records to publish
- Outbound queue inspector (retry, cancel, run a delivery pass on demand)
- Instance stats with 7-day volume chart, audit log of security-relevant actions
- JWT access tokens (20 min) + rotating refresh tokens, TOTP two-factor, role separation
  (`SystemAdmin`, `DomainAdmin`, `User`), per-mailbox authorisation checks

---

## Quick start (Docker)

```bash
git clone https://github.com/your-org/openpost.git
cd openpost

cp .env.example .env
# edit: POSTGRES_PASSWORD, JWT_SIGNING_KEY (openssl rand -base64 48), MAIL_HOSTNAME,
#       BOOTSTRAP_ADMIN_EMAIL, BOOTSTRAP_ADMIN_PASSWORD, BOOTSTRAP_DOMAIN

# build the web client (outputs to src/web/dist, which nginx serves), then start everything
cd src/web && npm ci && npm run build && cd ../..
docker compose up -d --build

open https://mail.example.org      # or http://<server-ip> for a first look
```

The API creates the schema, seeds the `SystemAdmin` role, the bootstrap administrator and a
`postmaster@<domain>` mailbox on first start. Sign in with `BOOTSTRAP_ADMIN_EMAIL`.

> Ports: the container listens on 2525/5587 internally (non-root user) and Compose maps
> `25→2525` and `587→5587`. Make sure your host firewall allows both.

## Quick start (local development)

```bash
# 1. database
docker run -d --name openpost-pg -p 5432:5432 \
  -e POSTGRES_DB=openpost -e POSTGRES_USER=openpost -e POSTGRES_PASSWORD=openpost postgres:17-alpine

# 2. API (http://localhost:5080, OpenAPI at /openapi/v1.json)
cd src/OpenPost.Api
dotnet run --urls http://localhost:5080
#    dev credentials: admin@localhost / ChangeMe!12345
#    SMTP listener is disabled in appsettings.Development.json - enable MailServer:Enabled to test

# 3. web client (http://localhost:4200, proxies /api to :5080)
cd ../web
npm install
npm start
```

Run the tests:

```bash
dotnet test OpenPost.slnx
```

---

## DNS setup (the part that decides whether mail arrives)

For every hosted domain, open **Admin → Domains → DNS** and publish the records shown:

| Type | Host | Value |
| --- | --- | --- |
| MX | `example.org` | `mail.example.org` (priority 10) |
| TXT | `example.org` | `v=spf1 mx a:mail.example.org -all` |
| TXT | `openpost._domainkey.example.org` | `v=DKIM1; k=rsa; p=…` (generated per domain) |
| TXT | `_dmarc.example.org` | `v=DMARC1; p=quarantine; rua=mailto:postmaster@example.org` |
| PTR | your server IP | `mail.example.org` (set at your hosting provider) |

Then verify with `dig`, [mail-tester.com](https://www.mail-tester.com) or `swaks`:

```bash
swaks --to you@example.org --from test@example.org --server mail.example.org:587 \
      --auth-user you@example.org --auth-password '…' --tls
```

---

## Production checklist

- [ ] `Jwt__SigningKey` is 32+ random characters (the API refuses to start in Production without it)
- [ ] TLS certificate mounted at `./certs/openpost.pfx` so submission offers `STARTTLS`
- [ ] `MailServer__HostName` matches your MX and PTR record
- [ ] `Cors__AllowedOrigins` lists only your webmail origin
- [ ] `pg_dump` (or WAL archiving) scheduled **and** the `mail-data` volume snapshotted
- [ ] EF Core migrations generated and reviewed instead of `EnsureCreated`
- [ ] Rspamd and ClamAV enabled for a public-facing instance
- [ ] Rate limits and `MaxMessageBytes` tuned to your traffic
- [ ] Log shipping configured (`logs/openpost-*.log`, 14-day retention by default)

---

## Repository layout

```
openpost/
├── src/
│   ├── OpenPost.Api/                 # .NET 10 Web API, SMTP server, queue, DKIM
│   │   ├── Controllers/              # auth, mailboxes, messages, admin, system
│   │   ├── Data/                     # EF Core DbContext, entities, seeding
│   │   ├── Mail/                     # SMTP in, routing, storage, MIME, DKIM, spam, queue out
│   │   ├── Security/                 # JWT, options, mailbox authorisation
│   │   ├── Services/                 # audit writer, health checks
│   │   └── Middleware/               # RFC 7807 error mapping
│   ├── OpenPost.Api.Tests/           # xUnit unit tests
│   └── web/                          # Angular 20 client (standalone components, signals)
├── deploy/nginx.conf                 # TLS terminator + SPA hosting
├── docker-compose.yml                # db, api, web, optional rspamd/clamav
└── docs/ARCHITECTURE.md              # data model, flows, scaling, backup
```

## API at a glance

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/v1/auth/login` \| `/refresh` \| `/logout` | Session lifecycle (JWT + rotating refresh) |
| `GET` | `/api/v1/auth/me` | Current user, roles and mailboxes |
| `GET` | `/api/v1/mailboxes` | Mailboxes the caller may access |
| `GET` | `/api/v1/mailboxes/{id}/folders` \| `/labels` | Folder tree with unread/total counts |
| `GET` | `/api/v1/mailboxes/{id}/messages` | Paged list, `folder`, `q`, `unread`, `flagged` |
| `GET` | `/api/v1/messages/{id}` \| `/raw` | Message detail and original `.eml` |
| `PATCH` | `/api/v1/messages/{id}` | Flags, move, labels |
| `POST` | `/api/v1/mailboxes/{id}/send` \| `/drafts` \| `/attachments` | Compose, drafts, uploads |
| `POST` | `/api/v1/mailboxes/{id}/messages/bulk` | markRead, flag, move, delete, spam, archive |
| `GET/POST` | `/api/v1/admin/domains` | Domain lifecycle, DKIM rotation, DNS records |
| `GET/POST` | `/api/v1/admin/mailboxes` \| `/users` | Mailbox and user management |
| `GET/POST` | `/api/v1/admin/queue` | Queue inspection, retry, cancel, force a pass |
| `GET` | `/api/v1/admin/stats` \| `/audit` | Instance metrics and audit trail |
| `GET` | `/health` \| `/api/v1/system/info` | Liveness and instance metadata |

OpenAPI document: `GET /openapi/v1.json`.

---

## What is intentionally not here yet

Being explicit is cheaper than a wrong expectation:

- **IMAP/POP3/JMAP** — the web client talks to the REST API; desktop/mobile clients need an IMAP
  gateway (next milestone, `MailKit`-based).
- **SRS rewriting** for forwarded mail (currently uses the local postmaster as envelope sender).
- **Calendar/contacts (CalDAV/CardDAV)**, web push, and server-side filter rules.
- **Migrations are not committed** — generate them for production
  (`dotnet ef migrations add Initial -p src/OpenPost.Api`). A fresh clone uses `EnsureCreated` so the
  first run needs no ceremony.
- **No hosted multi-tenancy isolation beyond roles/ownership** — this is designed to be deployed by
  one organisation on its own server.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security reports: [SECURITY.md](SECURITY.md).

## Licence

MIT — see [LICENSE](LICENSE). Run it, modify it, fork it, sell support around it; keep the copyright
notice. Mail servers are infrastructure, so permissive licensing is the pragmatic default here.
