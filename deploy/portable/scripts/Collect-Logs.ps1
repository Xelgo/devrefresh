. (Join-Path $PSScriptRoot 'Common.ps1')
try {
    $taskSupport = [IO.Path]::GetFullPath((Join-Path $taskPackageRoot 'support'))
    New-Item -ItemType Directory -Path $taskSupport -Force | Out-Null
    $taskSuffix = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $taskStage = [IO.Path]::GetFullPath((Join-Path $taskSupport ('staging-' + $taskSuffix)))
    New-Item -ItemType Directory -Path (Join-Path $taskStage 'logs') -Force | Out-Null
    Get-DiagnosticReport | Set-Content -LiteralPath (Join-Path $taskStage 'system-report.txt') -Encoding UTF8
    $taskLogs = Join-Path $taskPackageRoot 'logs'
    if (Test-Path -LiteralPath $taskLogs) {
        Get-ChildItem -LiteralPath $taskLogs -File -Filter 'devrefresh-*.jsonl' |
            Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 7 |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskStage 'logs') }
        $taskLauncher = Join-Path $taskLogs 'launcher.log'
        if ((Test-Path -LiteralPath $taskLauncher) -and -not ((Get-Item -LiteralPath $taskLauncher).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Get-Content -LiteralPath $taskLauncher -Tail 1000 -Encoding UTF8 |
                Set-Content -LiteralPath (Join-Path $taskStage 'logs\launcher-tail.txt') -Encoding UTF8
        }
        Get-ChildItem -LiteralPath $taskLogs -File | Where-Object {
            $_.Name -match '^(update|rollback)-[0-9-]+\.log$' -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
        } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 3 | ForEach-Object {
            # PowerShell 5.1 Tee-Object writes UTF-16; decode before adding a UTF-8 support copy.
            Get-Content -LiteralPath $_.FullName -Tail 1000 | Set-Content -LiteralPath (Join-Path $taskStage ('logs\' + $_.Name)) -Encoding UTF8
        }
    }
    $taskArchive = Join-Path $taskSupport ('DevRefresh-support-' + $taskSuffix + '.zip')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($taskStage, $taskArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    if (-not $taskStage.StartsWith($taskSupport + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staging path is outside the support directory; cleanup refused.'
    }
    Remove-Item -LiteralPath $taskStage -Recurse -Force
    Write-Output ('Support archive created: ' + $taskArchive)
    Write-Output 'Send this ZIP together with the error time and the action you were performing.'
    exit 0
} catch {
    Write-Error ('Log collection failed: ' + $_.Exception.Message)
    exit 1
}
