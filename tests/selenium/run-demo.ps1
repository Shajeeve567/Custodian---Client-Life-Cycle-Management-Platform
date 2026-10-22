<#
.SYNOPSIS
    Runs Custodian Selenium UI/E2E tests sequentially in visible Chrome demonstration or headless CI mode.
.DESCRIPTION
    SE3112 Role D - Reusable Demonstration & CI Script.
    Executes high-signal microservice workflow tests sequentially with deliberate visual pacing
    during live academic demonstrations, or fast and headless in automated CI pipelines.

    Streamlined Workflow Sets (11 Tests, ~85s Headless):
      1. Authentication & RBAC:   AUTH01 -> AUTH02
      2. Lifecycle & Gate Lock:   LIFE01 -> LIFE02
      3. CSTD-25 Approvals:       APPR01 (Sprint 4 Flagship)
      4. Document Compliance:     DOC01 -> DOC02
      5. SLA Stall Queue:         STALL01
      6. Audit, Report & Sec:     AUDIT01 -> REPORT01 -> SEC01

    Academic Viva Quick Demo:
      -QuickDemo: Executes CSTD-25 Client Approval Workflow (APPR01, ~65s with visual pacing)
.PARAMETER DelayMs
    Milliseconds to pause after key UI actions (default: 800 in demo mode).
.PARAMETER Filter
    Optional test filter pattern (e.g. 'APPR01', 'LIFE01', 'DOC'). Defaults to running all routine suite tests sequentially.
.PARAMETER Headless
    Switch to force headless execution (e.g. for fast CI verification in GitHub Actions).
.PARAMETER QuickDemo
    Switch to execute the curated 60-second live demonstration for the SE3112 2-minute presentation slot.
.PARAMETER IncludeGoldenPath
    Explicit opt-in switch to include E2E06 Full Client Lifecycle Golden Path at the end of the suite.
#>
param(
    [int]$DelayMs = 800,
    [string]$Filter = "",
    [string]$BaseUrl = "",
    [switch]$Headless,
    [switch]$QuickDemo,
    [switch]$IncludeGoldenPath
)

$ErrorActionPreference = "Continue"
$env:DOTNET_ROLL_FORWARD = "Major"

if ($BaseUrl) {
    $env:E2E_BASE_URL = $BaseUrl
    Write-Host "[TARGET] Target Base URL set to: $BaseUrl" -ForegroundColor Cyan
}

if ($Headless) {
    $env:SELENIUM_HEADLESS = "true"
    $env:SELENIUM_DEMO_MODE = "false"
    Write-Host "[MODE] Running in HEADLESS verification mode." -ForegroundColor Cyan
} else {
    Remove-Item Env:\SELENIUM_HEADLESS -ErrorAction SilentlyContinue
    $env:SELENIUM_DEMO_MODE = "true"
    $env:SELENIUM_DEMO_DELAY_MS = "$DelayMs"
    Write-Host "[MODE] Running in VISIBLE CHROME Demonstration Mode (Delay: ${DelayMs}ms per action)." -ForegroundColor Yellow
}

if ($IncludeGoldenPath) {
    $env:RUN_GOLDEN_PATH_E2E = "true"
    Write-Host "[OPT-IN] E2E06 Full Client Lifecycle Golden Path is ENABLED." -ForegroundColor Magenta
} else {
    Remove-Item Env:\RUN_GOLDEN_PATH_E2E -ErrorAction SilentlyContinue
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $ScriptDir "Custodian.System.Tests\Custodian.System.Tests.csproj"

Write-Host "`n[BUILD] Building Selenium test project..." -ForegroundColor Cyan
dotnet build $Project
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Build failed. Aborting test run." -ForegroundColor Red
    exit 1
}

# Streamlined SE3112 microservice workflow sequence
$AllTests = @(
    "AUTH01_",
    "AUTH02_",
    "LIFE01_",
    "LIFE02_",
    "APPR01_",
    "DOC01_",
    "DOC02_",
    "STALL01_",
    "AUDIT01_",
    "REPORT01_",
    "SEC01_"
)

if ($IncludeGoldenPath) {
    $AllTests += "E2E06_"
}

if ($QuickDemo) {
    Write-Host "[QUICK-DEMO] Running curated CSTD-25 Client Approval Workflow for 2-Minute Viva." -ForegroundColor Green
    $RunList = @("APPR01_")
} elseif (![string]::IsNullOrWhiteSpace($Filter)) {
    if ($Filter -like "*E2E06*") {
        $env:RUN_GOLDEN_PATH_E2E = "true"
        Write-Host "[OPT-IN] Explicit filter for E2E06 detected. RUN_GOLDEN_PATH_E2E set to true." -ForegroundColor Magenta
    }
    $RunList = $AllTests | Where-Object { $_ -like "*$Filter*" }
    if ($RunList.Count -eq 0) {
        $RunList = @($Filter)
    }
} else {
    $RunList = $AllTests
}

$Results = @()
$TotalStart = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($t in $RunList) {
    Write-Host "`n========================================================" -ForegroundColor DarkGray
    Write-Host ">>> RUNNING TEST: $t" -ForegroundColor Cyan
    Write-Host "========================================================" -ForegroundColor DarkGray

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    dotnet test $Project --no-build --filter "FullyQualifiedName~$t" --logger "console;verbosity=normal"
    $sw.Stop()
    $dur = "{0:N1}s" -f $sw.Elapsed.TotalSeconds

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[PASS] $t ($dur)" -ForegroundColor Green
        $Results += [PSCustomObject]@{ Test = $t; Status = "PASS"; Duration = $dur }
    } else {
        Write-Host "[FAIL] $t ($dur)" -ForegroundColor Red
        $Results += [PSCustomObject]@{ Test = $t; Status = "FAIL"; Duration = $dur }
    }
}

$TotalStart.Stop()

Write-Host "`n========================================================" -ForegroundColor DarkCyan
Write-Host "                    TEST RUN SUMMARY                    " -ForegroundColor DarkCyan
Write-Host "========================================================" -ForegroundColor DarkCyan
$Results | Format-Table -AutoSize

$PassCount = @($Results | Where-Object { $_.Status -eq "PASS" }).Count
$FailCount = @($Results | Where-Object { $_.Status -eq "FAIL" }).Count
$TotalTime = "{0:N1}s" -f $TotalStart.Elapsed.TotalSeconds

Write-Host "Total Tests: $($Results.Count) | Passed: $PassCount | Failed: $FailCount | Total Time: $TotalTime" -ForegroundColor $(if ($FailCount -eq 0) { "Green" } else { "Red" })

if ($FailCount -gt 0) {
    exit 1
}
exit 0
