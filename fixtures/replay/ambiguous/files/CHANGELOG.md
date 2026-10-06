# Changelog

## 1.2.0 - Reliability hardening

- Rate limiting on `POST /api/links` (fixed window, `RateLimiting:CreatePerMinute`, default 30) returning `429`.
- Readiness probe `GET /health/ready` that verifies database connectivity (`503` + problem details on failure).
- `X-Correlation-Id` propagated or generated on every response.
- Consistent RFC 7807 problem responses for unhandled exceptions and bare status codes.
- Click writer: graceful drain on shutdown and bounded retry on transient SQLite lock errors (already present, now documented and tested).
- No breaking changes to existing endpoints.

## 1.1.0 - Click analytics

- Every redirect records a click event (timestamp, referrer, user agent) asynchronously through a bounded queue and
  a background batch writer, so redirect latency is unaffected.
- New endpoint `GET /api/links/{code}/stats`: total clicks, last click, clicks per day for the last 7 days, top 5 referrers.
- Deleting a link cascades to its click history.

## 1.0.0 - Initial release

- Create short links with random 7-character Base62 codes or a custom alias.
- Optional expiry; expired links answer `410 Gone`.
- Redirect endpoint (`302 Found`), metadata endpoint, delete endpoint.
- URL validation: absolute http/https only, length limit, no embedded credentials, private hosts blocked.
- SQLite persistence via EF Core, schema created on startup.
- Liveness endpoint `/health` and OpenAPI document.
- xUnit integration and unit tests.
