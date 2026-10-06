# Agentic SDLC dashboard

Angular dashboard for the orchestrator API served by `src/AgenticSdlc.Host`. Standalone components,
Angular router, plain CSS, no UI component libraries.

Pages:

- `/` Runs: table of runs (polled every 2s) and a "Start run" form.
- `/runs/:id` Run detail: status header with Stop/Resume, stage graph in topological waves,
  pending approval / clarification cards, decision lineage, metrics, artifacts, workspace browser,
  and the incrementally polled, hash-chain-verifiable audit log.
- `/metrics` Aggregated reliability metrics across all runs (overall, by status, by scenario).

## Develop with `ng serve`

```bash
# 1. start the API host (listens on http://localhost:5080)
dotnet run --project src/AgenticSdlc.Host -- serve

# 2. in another terminal
cd dashboard
npm install
npm start          # = ng serve, http://localhost:4200
```

`angular.json` wires `proxy.conf.json` into `ng serve`, so every `/api/*` request from the dev
server is forwarded to `http://localhost:5080`. The app only ever uses relative `/api/...` URLs.

## Production build served by the .NET host

```bash
cd dashboard
npm run build      # outputs to dashboard/dist/dashboard/browser
dotnet run --project src/AgenticSdlc.Host -- serve
```

`RepoPaths.Dashboard` in the host points at `dashboard/dist/dashboard/browser`. When that folder
exists the host serves it as static files with an SPA fallback to `index.html`, so the dashboard and
the API share the same origin (`http://localhost:5080`) and no proxy is needed.

## Layout

```
src/app/api/models.ts          typed API models (camelCase JSON, PascalCase string enums)
src/app/api/api.service.ts     ApiService: typed HttpClient wrapper over /api
src/app/shared/format.ts       small formatting helpers
src/app/pages/runs/            Runs list + start form
src/app/pages/run-detail/      Run detail (graph.ts computes topological waves)
src/app/pages/metrics/         Aggregate metrics
```
