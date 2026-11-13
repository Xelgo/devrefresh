param([ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,63}$')][string]$ServiceName='DevRefresh')
. (Join-Path $PSScriptRoot 'Service-Common.ps1')
try {
    Start-DevRefreshService -ServiceName $ServiceName -PackageRoot ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
