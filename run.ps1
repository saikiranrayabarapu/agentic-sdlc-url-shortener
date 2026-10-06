<#
.SYNOPSIS
  One-command entry points for the Agentic SDLC prototype (Windows PowerShell / pwsh).

.EXAMPLE
  .\run.ps1 test                       # build everything and run both test suites
  .\run.ps1 scenario greenfield        # run a scenario headless (interactive approvals)
  .\run.ps1 scenario brownfield -Auto  # same, auto-approve (CI/demo)
  .\run.ps1 serve                      # API + dashboard on http://localhost:5080
  .\run.ps1 shortener                  # run the URL shortener itself on http://localhost:5000
#>
param(
    [Parameter(Position = 0)] [ValidateSet("test", "scenario", "serve", "shortener", "dashboard-build", "list", "metrics")] [string] $Command = "serve",
    [Parameter(Position = 1)] [string] $Scenario = "greenfield",
    [switch] $Auto,
    [string] $Mode = "replay",
    [string] $InjectFailure
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$host_ = "src/AgenticSdlc.Host"

switch ($Command) {
    "test" {
        dotnet test -nologo
    }
    "scenario" {
        $args_ = @("run", $Scenario, "--mode", $Mode)
        if ($Auto) { $args_ += "--auto-approve" }
        if ($InjectFailure) { $args_ += @("--inject-failure", $InjectFailure) }
        dotnet run --project $host_ -- @args_
    }
    "serve" {
        if (-not (Test-Path "dashboard/dist/dashboard/browser/index.html")) {
            Write-Host "Dashboard not built yet; building it (requires Node 24)..." -ForegroundColor Yellow
            Push-Location dashboard; npm install; npm run build; Pop-Location
        }
        dotnet run --project $host_ -- serve --port 5080
    }
    "shortener" {
        dotnet run --project src/UrlShortener.Api --urls http://localhost:5000
    }
    "dashboard-build" {
        Push-Location dashboard; npm install; npm run build; Pop-Location
    }
    "list" { dotnet run --project $host_ -- list }
    "metrics" { dotnet run --project $host_ -- metrics }
}
exit $LASTEXITCODE
