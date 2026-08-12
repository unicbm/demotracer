# ---------------------------------------------------------------------------------------------
# Copyright (c) 2026 unicbm. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0 only.
# See LICENSE in the project root for license information.
# ---------------------------------------------------------------------------------------------

param(
    [ValidateRange(1, 90)]
    [int]$Days = 7,
    [string]$Database = "demotracer-telemetry",
    [string]$WranglerVersion = "4.118.0"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ConfigPath = Join-Path $RepoRoot "cloudflare\telemetry\wrangler.jsonc"

function Invoke-D1Query {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Title,
        [Parameter(Mandatory = $true)]
        [string]$Sql
    )

    $CompactSql = ($Sql -replace "\s+", " ").Trim()
    Write-Host ""
    Write-Host $Title
    & npx.cmd --yes "wrangler@$WranglerVersion" d1 execute $Database `
        --config $ConfigPath --remote --command $CompactSql
    if ($LASTEXITCODE -ne 0) {
        throw "D1 report query failed: $Title"
    }
}

$summarySql = @"
SELECT 'approximately_online_10m' AS metric, COUNT(*) AS value
FROM active_installations
WHERE last_seen >= unixepoch() - 600
UNION ALL
SELECT 'active_installations_utc_today', COUNT(*)
FROM daily_installations
WHERE day = date('now')
UNION ALL
SELECT 'analysis_events_last_${Days}d', COALESCE(SUM(event_count), 0)
FROM hourly_metrics
WHERE event_kind = 'analysis' AND hour >= strftime('%Y-%m-%dT%H', 'now', '-$Days days')
UNION ALL
SELECT 'conversion_events_last_${Days}d', COALESCE(SUM(event_count), 0)
FROM hourly_metrics
WHERE event_kind = 'conversion' AND hour >= strftime('%Y-%m-%dT%H', 'now', '-$Days days');
"@

$versionSql = @"
SELECT app_version, COUNT(*) AS active_installations_utc_today
FROM daily_installations
WHERE day = date('now')
GROUP BY app_version
ORDER BY active_installations_utc_today DESC, app_version DESC;
"@

$errorSql = @"
SELECT event_kind, error_code, SUM(event_count) AS failures
FROM hourly_metrics
WHERE outcome = 'failure'
  AND hour >= strftime('%Y-%m-%dT%H', 'now', '-$Days days')
GROUP BY event_kind, error_code
ORDER BY failures DESC, event_kind, error_code
LIMIT 20;
"@

$sourceSql = @"
SELECT demo_source, SUM(event_count) AS demos_analyzed
FROM hourly_metrics
WHERE event_kind = 'analysis'
  AND hour >= strftime('%Y-%m-%dT%H', 'now', '-$Days days')
GROUP BY demo_source
ORDER BY demos_analyzed DESC, demo_source;
"@

Invoke-D1Query -Title "DemoTracer telemetry summary (UTC)" -Sql $summarySql
Invoke-D1Query -Title "Active app versions today (UTC)" -Sql $versionSql
Invoke-D1Query -Title "Locally classified demo sources over the last $Days days" -Sql $sourceSql
Invoke-D1Query -Title "Top coarse failure categories over the last $Days days" -Sql $errorSql
