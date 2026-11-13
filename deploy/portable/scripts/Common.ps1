$ErrorActionPreference = 'Stop'
$taskPackageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-ServerConfig {
    Get-Content -LiteralPath (Join-Path $taskPackageRoot 'config\server.json') -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-LocalHealthUrl($Configuration) {
    foreach ($taskUrl in ([string]$Configuration.Urls -split ';')) {
        $taskUri = $null
        if ([Uri]::TryCreate($taskUrl, [UriKind]::Absolute, [ref]$taskUri) -and $taskUri.IsLoopback -and $taskUri.Scheme -eq 'http') {
            return $taskUri.GetLeftPart([UriPartial]::Authority) + '/health'
        }
    }
    return $null
}

function Get-PortableFileHash([string]$Path) {
    $taskHashAlgorithm = [Security.Cryptography.SHA256]::Create()
    $taskHashStream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return [BitConverter]::ToString($taskHashAlgorithm.ComputeHash($taskHashStream)).Replace('-', '') }
    finally { $taskHashStream.Dispose(); $taskHashAlgorithm.Dispose() }
}

function Get-DiagnosticReport {
    $taskReport = [Collections.Generic.List[string]]::new()
    $taskReport.Add('DevRefresh deployment diagnostics')
    $taskReport.Add('Time UTC: ' + [DateTime]::UtcNow.ToString('O'))
    $taskReport.Add('Windows: ' + [Environment]::OSVersion.VersionString)
    $taskReport.Add('OS architecture: ' + $(if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }))
    $taskReport.Add('PowerShell: ' + $PSVersionTable.PSVersion.ToString())
    $taskReport.Add('Operator: ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name)
    $taskReport.Add('Package: ' + $taskPackageRoot)
    $taskExe = Join-Path $taskPackageRoot 'app\DevRefresh.exe'
    $taskReport.Add('Executable present: ' + (Test-Path -LiteralPath $taskExe))
    if (Test-Path -LiteralPath $taskExe) {
        $taskReport.Add('Executable version: ' + (Get-Item -LiteralPath $taskExe).VersionInfo.FileVersion)
        $taskReport.Add('Executable SHA256: ' + (Get-PortableFileHash $taskExe))
    }
    $taskBuild = Join-Path $taskPackageRoot 'BUILD-INFO.json'
    if (Test-Path -LiteralPath $taskBuild) { $taskReport.Add('Build: ' + (Get-Content -LiteralPath $taskBuild -Raw -Encoding UTF8)) }
    try {
        $taskConfig = Get-ServerConfig
        $taskReport.Add('Configured URLs: ' + [string]$taskConfig.Urls)
        $taskReport.Add('Windows Authentication: ' + [string]$taskConfig.WindowsAuthentication.Enabled)
        $taskReport.Add('HTTPS certificate configured: ' + [bool]$taskConfig.HttpsCertificate.Thumbprint)
        $taskHealthUrl = Get-LocalHealthUrl $taskConfig
        if ($taskHealthUrl) {
            try {
                $taskHealth = Invoke-WebRequest -Uri $taskHealthUrl -UseBasicParsing -TimeoutSec 5
                $taskReport.Add('Local health: HTTP ' + $taskHealth.StatusCode + ' ' + $taskHealth.Content)
            } catch { $taskReport.Add('Local health: FAILED - ' + $_.Exception.Message) }
        } else { $taskReport.Add('Local health: no loopback HTTP endpoint configured') }
    } catch { $taskReport.Add('Configuration could not be read: ' + $_.Exception.Message) }
    try {
        $taskProcesses = Get-Process -Name DevRefresh -ErrorAction SilentlyContinue
        foreach ($taskProcess in $taskProcesses) {
            if ($taskProcess.Path -and [string]::Equals($taskProcess.Path, $taskExe, [StringComparison]::OrdinalIgnoreCase)) {
                $taskReport.Add('Process PID: ' + $taskProcess.Id + '; working set MB: ' + [Math]::Round($taskProcess.WorkingSet64 / 1MB))
            }
        }
    } catch { $taskReport.Add('Process inspection unavailable') }
    try {
        $taskServices = Get-CimInstance Win32_Service -Filter "Name LIKE 'DevRefresh%'" -ErrorAction Stop
        $taskServiceFound = $false
        foreach ($taskService in $taskServices) {
            if ($taskService.PathName -and $taskService.PathName.IndexOf($taskExe, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $taskServiceFound = $true
                $taskReport.Add('Service: ' + $taskService.Name + '; state: ' + $taskService.State + '; startup: ' + $taskService.StartMode + '; account: ' + $taskService.StartName)
            }
        }
        if (-not $taskServiceFound) { $taskReport.Add('Service: not installed for this folder') }
    } catch { $taskReport.Add('Service inspection unavailable') }
    $taskReport.Add('Passwords, SQLite, encryption keys and raw configuration are not collected.')
    return $taskReport.ToArray()
}
