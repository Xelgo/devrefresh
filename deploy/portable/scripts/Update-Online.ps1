param(
    [string]$TargetDirectory,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,63}$')][string]$ServiceName='DevRefresh',
    [switch]$CheckOnly,
    [switch]$Yes
)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Service-Common.ps1')

function ConvertTo-DevRefreshVersion([string]$Value) {
    if ($Value -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw ('Invalid version: ' + $Value) }
    $version = [version]$Value
    return [version]::new($version.Major,$version.Minor,$version.Build,[Math]::Max($version.Revision,0))
}

function Wait-UpdatedHealth([string]$Url,[version]$Version) {
    if (-not $Url) { return }
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        try {
            $health = Invoke-RestMethod -Uri $Url -TimeoutSec 3 -ErrorAction Stop
            if ($health.status -eq 'ok' -and (ConvertTo-DevRefreshVersion $health.version) -eq $Version) { return }
        } catch { }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The service started, but /health did not confirm the new version. Check the service logs and last-update.json.'
}

$stage = $null
$updateLock = $null
try {
    $repository = 'Xelgo/devrefresh'
    $assetName = 'DevRefresh-update-win-x64.zip'
    if (-not $TargetDirectory) { $TargetDirectory = $taskPackageRoot }
    $TargetDirectory = [IO.Path]::GetFullPath($TargetDirectory.Trim().Trim('"')).TrimEnd('\')
    $exe = Join-Path $TargetDirectory 'app\DevRefresh.exe'
    $configPath = Join-Path $TargetDirectory 'config\server.json'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or -not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw 'Specify the installed DevRefresh folder with -TargetDirectory.'
    }
    $current = ConvertTo-DevRefreshVersion (Get-Item -LiteralPath $exe).VersionInfo.FileVersion

    # Windows PowerShell 5.1 can otherwise default to an obsolete TLS version.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" -Headers $headers -TimeoutSec 30
    if ($release.draft -or $release.prerelease -or $release.tag_name -cnotmatch '^v\d+\.\d+\.\d+$') {
        throw 'The latest GitHub release does not have a stable DevRefresh version tag.'
    }
    $available = ConvertTo-DevRefreshVersion $release.tag_name.Substring(1)
    $asset = @($release.assets | Where-Object { $_.name -ceq $assetName -and $_.state -eq 'uploaded' })
    if ($asset.Count -ne 1 -or $asset[0].digest -cnotmatch '^sha256:[0-9a-fA-F]{64}$') {
        throw 'The GitHub release is missing the update ZIP or its SHA-256 digest.'
    }
    Write-Output "Installed version: $current. GitHub release: $($release.tag_name)."
    if ($available -le $current) { Write-Output 'No newer version is available.'; exit 0 }
    if ($CheckOnly) { Write-Output 'An online update is available.'; exit 0 }
    Assert-ServiceAdministrator

    $serviceInfo = @(Get-CimInstance Win32_Service -Filter "Name='$ServiceName'")
    if ($serviceInfo.Count -ne 1 -or $serviceInfo[0].PathName -notmatch '^\s*"([^"]+)"(?:\s|$)' -or
        -not [string]::Equals($Matches[1],$exe,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Windows service does not point to the selected DevRefresh installation.'
    }
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $healthUrl = Get-LocalHealthUrl $config

    $stage = [IO.Path]::GetFullPath((Join-Path $TargetDirectory ('.online-update-' + [guid]::NewGuid().ToString('N'))))
    Set-ServicePrivateFolder $stage ([Security.Principal.WindowsIdentity]::GetCurrent().User)
    $archive = Join-Path $stage $assetName
    $package = Join-Path $stage 'package'
    $download = "https://github.com/$repository/releases/download/$($release.tag_name)/$assetName"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $download -UseBasicParsing -OutFile $archive -TimeoutSec 600
    if ((Get-PortableFileHash $archive) -ine $asset[0].digest.Substring(7)) { throw 'The downloaded ZIP failed SHA-256 verification.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $package
    $manifest = Get-Content -LiteralPath (Join-Path $package 'BUILD-INFO.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.product -cne 'DevRefresh' -or $manifest.packageKind -cne 'update' -or $manifest.runtime -cne 'win-x64' -or
        (ConvertTo-DevRefreshVersion $manifest.version) -ne $available -or
        (ConvertTo-DevRefreshVersion (Get-Item -LiteralPath (Join-Path $package 'app\DevRefresh.exe')).VersionInfo.FileVersion) -ne $available) {
        throw 'The package does not match the GitHub release.'
    }

    if (-not $Yes -and (Read-Host "Install $($release.tag_name)? Running DevRefresh jobs can be interrupted. Type UPDATE to continue") -cne 'UPDATE') {
        Write-Output 'Update cancelled.'
        exit 0
    }
    $updateLock = [IO.File]::Open((Join-Path $TargetDirectory '.online-update.lock'),[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    if ((ConvertTo-DevRefreshVersion (Get-Item -LiteralPath $exe).VersionInfo.FileVersion) -ge $available) {
        Write-Output 'No newer version is available.'
        exit 0
    }
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    if ($service.Status -notin @('Running','Stopped')) { throw 'Wait until the service is running or stopped, then retry.' }
    $restart = $service.Status -eq 'Running'
    Stop-DevRefreshService $ServiceName
    $windowsPowerShell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'scripts\Update.ps1') -TargetDirectory $TargetDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Installation failed. Inspect the update log before starting the service.' }

    if ($restart) {
        Start-DevRefreshService $ServiceName $TargetDirectory
        Wait-UpdatedHealth $healthUrl $available
    }
    Write-Output "DevRefresh $($release.tag_name) installed."
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    if ($updateLock) { $updateLock.Dispose() }
    if ($stage -and $stage.StartsWith($TargetDirectory + '\.online-update-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $stage -PathType Container)) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
