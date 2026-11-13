. (Join-Path $PSScriptRoot 'Common.ps1')
try {
    $taskConfiguration = Get-ServerConfig
    $taskLocalHealth = Get-LocalHealthUrl $taskConfiguration
    Write-Output ('Configured URLs: ' + $taskConfiguration.Urls)
    if ($taskLocalHealth) { Write-Output ('Local administrator: ' + $taskLocalHealth.Replace('/health','/login')) }
    exit 0
} catch {
    Write-Error ('Could not read server configuration: ' + $_.Exception.Message)
    exit 1
}
