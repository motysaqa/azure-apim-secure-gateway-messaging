# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-09-08

First complete version of the portfolio project.

### Added

- **Booking.Api** — .NET 8 minimal API with create, retrieve, liveness and
  readiness endpoints. The booking row and its outbox message commit in one
  transaction; a background publisher drains the outbox to Service Bus and flips
  the booking to `CONFIRMED`. Client-supplied `Idempotency-Key` replays return the
  original booking with `200` rather than creating a second one. Errors are
  RFC 7807 problem documents carrying the correlation id.
- **Booking.Worker** — .NET 8 worker consuming the queue in peek-lock mode with
  autocomplete disabled. `BookingCreatedHandler` decides between processed,
  duplicate, dead-letter and retry; the Service Bus plumbing only acts on that
  decision. Deduplication is a durable ledger written in the same transaction as
  the booking, so concurrent deliveries produce exactly one write.
- **Booking.Contracts** — the versioned `BookingCreated` message and the
  validation both sides share. The worker re-validates rather than trusting the
  queue, but skips the past-date rule: a message that waited over a weekend is
  late, not invalid.
- **Infrastructure** — Bicep at subscription scope provisioning the resource
  group, APIM (Consumption), App Service (B1 Linux), Service Bus (Basic) with the
  `booking-created` queue, Key Vault (RBAC), and every managed identity role
  assignment scoped to the queue rather than the namespace. Service Bus local
  (SAS) auth is disabled outright. Monitoring is opt-in.
- **APIM configuration** — API policy with `validate-jwt`, `rate-limit-by-key`,
  `quota-by-key`, `ip-filter`, correlation and identity headers, outbound security
  headers and an RFC 7807 `on-error`. Operation policies for response caching and
  URL rewriting. Starter and Premium products with distinct limits and approval
  flows. The OpenAPI 3.0 contract imported into the gateway.
- **Automation** — `Deploy-Infrastructure.ps1` (CSPRNG backend key, code publish,
  App Service lockdown to the APIM outbound addresses), `Import-ApimApi.ps1`
  (revision-based import with local policy validation), `Invoke-SmokeTests.ps1`
  (token acquisition and assertions against a live gateway) and
  `reconcile_bookings.py` (drift detection between the two stores).
- **Tests** — 29 xUnit tests: 19 API integration tests through
  `WebApplicationFactory` covering creation, idempotency replay, validation,
  correlation, the gateway backend-key gate and the outbox reaching the publisher;
  10 worker tests covering idempotency under concurrency, malformed JSON,
  unsupported schema versions and business-invalid messages.
- **CI** — build and test, Bicep compilation, APIM policy and specification
  validation, PowerShell parsing and analysis, and a secret-hygiene job that fails
  on a committed connection string, local configuration file, or hard-coded
  tenant or client id.
- **Documentation** — architecture and message-flow diagrams, the APIM request
  flow with policy ordering, Entra ID app registration steps, a cost breakdown,
  a setup guide and a troubleshooting log.

### Known limitations

- Nothing has been deployed to Azure; the templates compile and the scripts parse,
  but no live deployment has been run.
- SQLite stands in for SQL Server, so the outbox and the deduplication ledger are
  per-instance.
- The worker's compute is not provisioned; `docs/setup.md` explains why and gives
  three options.
- APIM Consumption has no built-in cache and Service Bus Basic has no duplicate
  detection. Both are documented where they matter.

[1.0.0]: https://github.com/motysaqa/azure-apim-secure-gateway-messaging/releases/tag/v1.0.0
