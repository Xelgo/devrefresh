param([string]$TargetDirectory, [switch]$Rollback, [string]$BackupDirectory, [switch]$ConfirmRollback)
. (Join-Path $PSScriptRoot 'Common.ps1')
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    if (-not $TargetDirectory) { $TargetDirectory = Read-Host 'Existing DevRefresh installation folder (example C:\DevRefresh)' }
    $TargetDirectory = [IO.Path]::GetFullPath($TargetDirectory.Trim().Trim('"')).TrimEnd('\')
    $taskTargetApp = Join-Path $TargetDirectory 'app'
    $taskTargetConfig = Join-Path $TargetDirectory 'config\server.json'
    if (-not (Test-Path -LiteralPath $taskTargetConfig) -or -not (Test-Path -LiteralPath (Join-Path $taskTargetApp 'DevRefresh.exe'))) {
        throw 'Select an existing installation containing app\DevRefresh.exe and config\server.json.'
    }
    if ([string]::Equals($TargetDirectory, $taskPackageRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Run UPDATE from a separate unpacked update package, not from the installed service folder.'
    }
    $taskOperation = 'update'
    if ($Rollback) {
        $taskOperation = 'rollback'
        if (-not $BackupDirectory) { $BackupDirectory = Read-Host 'Full backup folder path from the previous update result' }
        $BackupDirectory = [IO.Path]::GetFullPath($BackupDirectory.Trim().Trim('"')).TrimEnd('\')
        if (-not $ConfirmRollback) {
            $taskConfirmation = Read-Host 'Rollback restores application and settings to the backup time. Type ROLLBACK to continue'
            if ($taskConfirmation -cne 'ROLLBACK') { throw 'Rollback was cancelled.' }
        }
    }
    $taskLogDirectory = Join-Path $TargetDirectory 'logs'
    New-Item -ItemType Directory -Path $taskLogDirectory -Force | Out-Null
    $taskLog = Join-Path $taskLogDirectory ($taskOperation + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.log')
    $taskArguments = @('--contentRoot',$taskTargetApp,'--ServerConfiguration',$taskTargetConfig,'--Maintenance',$taskOperation,'--UpdateTarget',$TargetDirectory)
    if ($Rollback) { $taskArguments += @('--UpdateBackup',$BackupDirectory) }
    Write-Output ('Operation: ' + $taskOperation + '; existing installation: ' + $TargetDirectory)
    Write-Output 'The service must be stopped. No process is killed by this script.'
    $ErrorActionPreference = 'Continue'
    & (Join-Path $taskPackageRoot 'app\DevRefresh.exe') @taskArguments 2>&1 | Tee-Object -FilePath $taskLog
    $taskCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($taskCode -ne 0) {
        Write-Output ('Operation failed. Log: ' + $taskLog)
        exit $taskCode
    }
    Write-Output ('Completed. Start the EXISTING installation using ' + (Join-Path $TargetDirectory 'STARTSERVICE.cmd') + ' as administrator, then run CHECK.cmd.')
    Write-Output ('Operation log: ' + $taskLog)
    exit 0
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
