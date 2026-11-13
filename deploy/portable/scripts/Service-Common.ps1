$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Assert-ServiceAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Запустите команду от имени администратора Windows: для управления службой нужны повышенные права.'
    }
}
function Get-ServiceFolder([string]$Value, [string]$AppPath) {
    if (-not [IO.Path]::IsPathRooted($Value)) { $Value = Join-Path $AppPath $Value }
    return [IO.Path]::GetFullPath($Value).TrimEnd('\')
}
function Assert-ServiceWritableFolder([string]$Path, [string]$PackageRoot, [string]$AppPath) {
    if ($Path -eq [IO.Path]::GetPathRoot($Path).TrimEnd('\') -or $Path -eq $PackageRoot -or
        $PackageRoot.StartsWith($Path + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $Path -eq $AppPath -or $Path.StartsWith($AppPath + '\', [StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith('\\')) {
        throw ('Каталоги данных и логов службы должны быть отдельными локальными папками вне app: ' + $Path)
    }
    if ((Test-Path -LiteralPath $Path) -and ((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw ('Каталог данных службы не должен быть ссылкой: ' + $Path)
    }
}
function Set-ServicePrivateFolder([string]$Path, [Security.Principal.SecurityIdentifier]$ServiceSid) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $principals = @('S-1-5-18', 'S-1-5-32-544', [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) | Select-Object -Unique
    foreach ($principal in $principals) {
        $sid = [Security.Principal.SecurityIdentifier]::new($principal)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($ServiceSid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Initialize-ServiceFiles([string]$PackageRoot, [string]$ServiceName) {
    $app = Join-Path $PackageRoot 'app'
    $exe = Join-Path $app 'DevRefresh.exe'
    $config = Join-Path $PackageRoot 'config\server.json'
    if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $config)) {
        throw 'Распакуйте полный пакет: необходимы app\DevRefresh.exe и config\server.json.'
    }
    $settings = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    $dataValue = $settings.DataDirectory
    if (-not $dataValue) { $dataValue = '../data' }
    $data = Get-ServiceFolder $dataValue $app
    $logValue = $settings.LogDirectory
    if (-not $logValue) { $logValue = Join-Path $data 'logs' }
    $logs = Get-ServiceFolder $logValue $app
    Assert-ServiceWritableFolder $data $PackageRoot $app
    Assert-ServiceWritableFolder $logs $PackageRoot $app
    $sidOutput = & sc.exe showsid $ServiceName
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось определить SID службы.' }
    $sidMatch = [regex]::Match(($sidOutput -join ' '), 'S-1-5-80-(?:\d+-){4}\d+')
    if (-not $sidMatch.Success) { throw 'Не удалось определить SID службы.' }
    $sid = [Security.Principal.SecurityIdentifier]::new($sidMatch.Value)
    # Restrict master key access before converting DPAPI protection to LocalMachine.
    Set-ServicePrivateFolder $data $sid
    Set-ServicePrivateFolder (Join-Path $data 'keys') $sid
    Set-ServicePrivateFolder $logs $sid
    $preparation = & $exe --contentRoot $app --ServerConfiguration $config --Maintenance prepare-service 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('Подготовка службы не выполнена. Остановите прежний процесс приложения. ' + ($preparation -join [Environment]::NewLine)) }
    $acl = Get-Acl -LiteralPath $PackageRoot
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $PackageRoot -AclObject $acl
    return [pscustomobject]@{ App=$app; Exe=$exe; Config=$config }
}
function Wait-DevRefreshService($Service, [string]$Status) {
    $Service.WaitForStatus([ServiceProcess.ServiceControllerStatus]$Status, [TimeSpan]::FromSeconds(150))
    $Service.Refresh()
}
function Start-DevRefreshService([string]$ServiceName, [string]$PackageRoot) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') { Write-Output ('Служба ' + $ServiceName + ' уже запущена.'); return }
    if ($service -and $service.Status -eq 'StartPending') {
        Wait-DevRefreshService $service 'Running'
        Write-Output ('Служба ' + $ServiceName + ' запущена.'); return
    }
    Assert-ServiceAdministrator
    if (-not $service) {
        $files = Initialize-ServiceFiles $PackageRoot $ServiceName
        $binaryPath = '"' + $files.Exe + '" --contentRoot "' + $files.App + '" --ServerConfiguration "' + $files.Config + '" --WindowsService:Name "' + $ServiceName + '"'
        # Omitting Credential creates the service under LocalSystem. Never reset an existing service account.
        New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'DevRefresh' -Description 'Актуализация баз разработки 1С' -StartupType Automatic -ErrorAction Stop | Out-Null
        Write-Output ('Служба ' + $ServiceName + ' установлена. Учётная запись: Local System (LocalSystem).')
    }
    if ($service -and $service.Status -eq 'StopPending') { Wait-DevRefreshService $service 'Stopped' }
    Start-Service -Name $ServiceName -ErrorAction Stop
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    Wait-DevRefreshService $service 'Running'
    Write-Output ('Служба ' + $ServiceName + ' запущена.')
}
function Stop-DevRefreshService([string]$ServiceName) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) { Write-Output ('Служба ' + $ServiceName + ' не установлена.'); return }
    if ($service.Status -eq 'Stopped') { Write-Output ('Служба ' + $ServiceName + ' уже остановлена.'); return }
    if ($service.Status -eq 'StopPending') {
        Wait-DevRefreshService $service 'Stopped'
        Write-Output ('Служба ' + $ServiceName + ' остановлена.'); return
    }
    Assert-ServiceAdministrator
    if ($service.Status -eq 'StartPending') { Wait-DevRefreshService $service 'Running' }
    Stop-Service -Name $ServiceName -ErrorAction Stop
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    Wait-DevRefreshService $service 'Stopped'
    Write-Output ('Служба ' + $ServiceName + ' остановлена.')
}
