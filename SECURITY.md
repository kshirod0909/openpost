# Security policy

OpenPost accepts untrusted SMTP input and holds an organisation's mail, so security reports are
taken seriously.

## Reporting a vulnerability

**Do not open a public issue.** Email `security@openpost.dev` with:

- affected version or commit,
- a description of the issue and its impact,
- reproduction steps or a proof of concept,
- any suggested fix.

You will get an acknowledgement within 3 working days and an assessment within 10. Please give us
90 days to ship a fix before public disclosure; we will credit you in the release notes unless you
prefer otherwise.

## Supported versions

The latest minor release on `main` receives security fixes. Older tags are not patched.

## Threat model (what is defended, and what is not)

**Defended**
- Open relay: `RCPT TO` for non-local domains is refused unless the session is authenticated.
- Credential stuffing: `/auth/login` is rate limited (10/min/client); Identity lockout after 10
  failures; TOTP available per user.
- Token theft: access tokens live 20 minutes and only in memory; refresh tokens are rotated, stored
  as SHA-256 hashes, and reuse of a rotated token is rejected.
- Privilege escalation: every mailbox endpoint authorises through `IMailboxAccess`; domain admins are
  scoped by `Domains.OwnerUserId`; roles are only assignable by `SystemAdmin`.
- HTML mail attacks: bodies render in an iframe with `sandbox=""` and `referrerpolicy="no-referrer"`
  — no scripts, no forms, no same-origin access, no referrer leak on remote images.
- Path traversal: attachment/raw paths are resolved through `LocalDiskStorage.ResolvePath`, which
  rejects anything escaping the storage root; file names are sanitised on write.
- Memory exhaustion: message size, recipient count, connection count and line length are all bounded
  before buffers grow.
- Malware: optional ClamAV `INSTREAM` scanning; infected messages are rejected with `550 5.7.1`.

**Not defended (documented honestly)**
- **Inbound SMTP has no per-sender reputation or greylisting** — put Rspamd in front for a public
  instance.
- **Forwarded mail uses the local postmaster as envelope sender** rather than SRS, so strict DMARC
  receivers may quarantine forwards.
- **No audit of message content access** — the audit log records administrative actions, not reads.
- **ClamAV fails open**: if the scanner is unreachable, mail is accepted unscanned (and logged as an
  error). Change `ClamAvScanner.ScanAsync` to fail closed if your threat model requires it.
- **No encryption at rest** — use full-disk encryption or an encrypted volume for
  `/var/lib/openpost` and PostgreSQL.

## Hardening checklist for operators

- [ ] TLS on submission (mount a PFX, keep `MailServer:TlsCertificatePath` set)
- [ ] `Jwt__SigningKey` 32+ random characters, rotated with a full sign-out
- [ ] Postgres not exposed outside the Docker network
- [ ] `Cors__AllowedOrigins` limited to your webmail host
- [ ] Fail2ban or provider firewall in front of ports 25/587/443
- [ ] Rspamd and ClamAV enabled for internet-facing deployments
- [ ] Backups encrypted, and a restore actually tested
