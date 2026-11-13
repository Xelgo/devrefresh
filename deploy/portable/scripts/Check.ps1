. (Join-Path $PSScriptRoot 'Common.ps1')
try {
    $taskReport = @(Get-DiagnosticReport)
    $taskReport | ForEach-Object { Write-Output $_ }
    if (@($taskReport | Where-Object { $_ -like 'Local health: HTTP 200 *' }).Count -gt 0) { exit 0 }
    exit 1
} catch {
    Write-Error ('Diagnostics failed: ' + $_.Exception.Message)
    exit 1
}
