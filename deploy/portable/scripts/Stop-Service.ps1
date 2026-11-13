param([ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,63}$')][string]$ServiceName='DevRefresh')
. (Join-Path $PSScriptRoot 'Service-Common.ps1')
try { Stop-DevRefreshService -ServiceName $ServiceName }
catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
