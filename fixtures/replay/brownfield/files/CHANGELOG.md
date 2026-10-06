# Changelog

## 1.1.0 - Click analytics

- Every redirect records a click event (timestamp, referrer, user agent) asynchronously through a bounded queue and
  a background batch writer, so redirect latency is unaffected.
- New endpoint `GET /api/links/{code}/stats`: total clicks, last click, clicks per day for the last 7 days, top 5 referrers.
- Deleting a link cascades to its click history.
- No changes to existing endpoints or response shapes.

## 1.0.0 - Initial release

- Create short links with random 7-character Base62 codes or a custom alias.
- Optional expiry; expired links answer `410 Gone`.
- Redirect endpoint (`302 Found`), metadata endpoint, delete endpoint.
- URL validation: absolute http/https only, length limit, no embedded credentials, private hosts blocked.
- SQLite persistence via EF Core, schema created on startup.
- Liveness endpoint `/health` and OpenAPI document.
- xUnit integration and unit tests.
