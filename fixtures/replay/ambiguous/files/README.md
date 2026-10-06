# URL Shortener Service

A small, production-minded URL shortener built with ASP.NET Core minimal APIs (.NET 10) and SQLite, with
asynchronous click analytics and operational hardening (rate limiting, readiness probe, correlation ids).

## Run

```bash
dotnet run --project UrlShortener.Api
```

The service listens on the URL printed to the console (default `http://localhost:5000`). The SQLite database
`urlshortener.db` is created automatically on first start.

## Test

```bash
dotnet test
```

## API

| Method | Path                      | Description                                   | Responses |
|--------|---------------------------|-----------------------------------------------|-----------|
| POST   | `/api/links`              | Create a short link (rate limited)            | 201, 400, 409, 429 |
| GET    | `/api/links/{code}`       | Link metadata                                 | 200, 404 |
| DELETE | `/api/links/{code}`       | Delete a link (and its click history)         | 204, 404 |
| GET    | `/api/links/{code}/stats` | Click analytics                               | 200, 404 |
| GET    | `/{code}`                 | Redirect to the original URL (records a click)| 302, 404, 410 |
| GET    | `/health`                 | Liveness probe                                | 200 |
| GET    | `/health/ready`           | Readiness probe (checks database connectivity)| 200, 503 |
| GET    | `/openapi/v1.json`        | OpenAPI document                              | 200 |

### Create a link

```http
POST /api/links
Content-Type: application/json

{ "url": "https://example.com/very/long/path", "customAlias": "launch-2026", "expiresAt": "2026-12-31T00:00:00Z" }
```

`customAlias` and `expiresAt` are optional. Aliases are 4-32 characters of letters, digits, `-` or `_`; `api`, `health`,
`openapi`, `swagger` and `admin` are reserved.

### Click analytics

```http
GET /api/links/{code}/stats
```

Returns `totalClicks`, `lastClickedAt`, `clicksPerDay` (7 entries, oldest first) and `topReferrers` (top 5, direct
visits grouped as `(direct)`). Clicks are recorded asynchronously through a bounded in-memory queue and a background
batch writer, so redirect latency is independent of analytics writes.

## Reliability features

| Feature | Behaviour |
|---------|-----------|
| Rate limiting | `POST /api/links` is limited per fixed one-minute window (`RateLimiting:CreatePerMinute`, default 30). Excess requests get `429 Too Many Requests`. Redirects are not limited. |
| Readiness probe | `GET /health/ready` opens a database connection and returns `503` with problem details when it cannot. Use it for load-balancer / orchestrator readiness; use `/health` for liveness. |
| Correlation ids | Every response carries `X-Correlation-Id`. An incoming header (up to 128 chars) is echoed; otherwise the request trace identifier is used. |
| Problem details | All error responses are RFC 7807 `application/problem+json`, including unhandled exceptions (`UseExceptionHandler`) and bare status codes (`UseStatusCodePages`). |
| Graceful shutdown | The click writer stops accepting new events and flushes buffered clicks before the host exits. |
| Write retry | Click batches retry up to 3 times with backoff on transient SQLite "database is locked" errors. |
| Bounded buffering | The click queue holds 10,000 events and drops the oldest under overload; the drop count is tracked. |

## Validation rules

- URL must be absolute, `http` or `https`, at most 2048 characters, without embedded credentials.
- Loopback and private-network hosts are rejected by default (`UrlValidation:BlockPrivateHosts`).
- Expired links return `410 Gone`; unknown codes return `404`.

## Configuration

| Key                               | Default                      | Purpose                           |
|-----------------------------------|------------------------------|-----------------------------------|
| `ConnectionStrings:Links`         | `Data Source=urlshortener.db`| SQLite connection string          |
| `RateLimiting:CreatePerMinute`    | `30`                         | Create requests allowed per minute per instance |
| `UrlValidation:MaxLength`         | `2048`                       | Maximum accepted URL length       |
| `UrlValidation:BlockPrivateHosts` | `true`                       | Reject loopback/private hosts     |

## Design notes

- Short codes are 7 random Base62 characters; a unique index on `Code` is the source of truth and collisions are retried.
- Click events store the timestamp as a UTC `DateTime` because SQLite cannot compare or aggregate `DateTimeOffset` columns.
- Rate limiting is in-process (per instance). A shared store would be needed for a multi-instance deployment.
- The database schema is created with `EnsureCreated()` for zero-step local setup. Switch to EF migrations before production.
