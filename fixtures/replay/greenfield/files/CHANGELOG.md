# Changelog

## 1.0.0 - Initial release

- Create short links with random 7-character Base62 codes or a custom alias.
- Optional expiry; expired links answer `410 Gone`.
- Redirect endpoint (`302 Found`), metadata endpoint, delete endpoint.
- URL validation: absolute http/https only, length limit, no embedded credentials, private hosts blocked.
- SQLite persistence via EF Core, schema created on startup.
- Liveness endpoint `/health` and OpenAPI document.
- xUnit integration and unit tests.
