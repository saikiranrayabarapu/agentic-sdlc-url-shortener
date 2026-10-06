# URL Shortener Service

A small, production-minded URL shortener built with ASP.NET Core minimal APIs (.NET 10) and SQLite, with
asynchronous click analytics.

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
| POST   | `/api/links`              | Create a short link                           | 201, 400, 409 |
| GET    | `/api/links/{code}`       | Link metadata                                 | 200, 404 |
| DELETE | `/api/links/{code}`       | Delete a link (and its click history)         | 204, 404 |
| GET    | `/api/links/{code}/stats` | Click analytics                               | 200, 404 |
| GET    | `/{code}`                 | Redirect to the original URL (records a click)| 302, 404, 410 |
| GET    | `/health`                 | Liveness probe                                | 200 |
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

```json
{
  "code": "Ab3dE9x",
  "totalClicks": 42,
  "lastClickedAt": "2026-10-06T12:34:56Z",
  "clicksPerDay": [ { "date": "2026-09-30", "clicks": 3 }, "... 7 entries, oldest first ..." ],
  "topReferrers": [ { "referrer": "https://news.example.org/post/1", "clicks": 30 }, { "referrer": "(direct)", "clicks": 12 } ]
}
```

Clicks are recorded **asynchronously**: the redirect enqueues an event into a bounded in-memory queue and a
background writer persists events in batches. Redirect latency is therefore independent of analytics writes.
Under extreme load the oldest unwritten events are dropped (never the redirect); the drop count is tracked.

### Validation rules

- URL must be absolute, `http` or `https`, at most 2048 characters, without embedded credentials.
- Loopback and private-network hosts are rejected by default (`UrlValidation:BlockPrivateHosts`).
- Expired links return `410 Gone`; unknown codes return `404`.

## Configuration

| Key                               | Default                      | Purpose                           |
|-----------------------------------|------------------------------|-----------------------------------|
| `ConnectionStrings:Links`         | `Data Source=urlshortener.db`| SQLite connection string          |
| `UrlValidation:MaxLength`         | `2048`                       | Maximum accepted URL length       |
| `UrlValidation:BlockPrivateHosts` | `true`                       | Reject loopback/private hosts     |

## Design notes

- Short codes are 7 random Base62 characters; a unique index on `Code` is the source of truth and collisions are retried.
- Click events store the timestamp as a UTC `DateTime` because SQLite cannot compare or aggregate `DateTimeOffset` columns.
- The database schema is created with `EnsureCreated()` for zero-step local setup. Switch to EF migrations before production.
