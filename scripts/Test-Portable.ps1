param([Parameter(Mandatory=$true)][string]$Archive)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskSourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSmoke = Join-Path $taskSourceRoot ("smoke-" + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + " package's folder")
New-Item -ItemType Directory -Path $taskSmoke | Out-Null
Expand-Archive -LiteralPath ([IO.Path]::GetFullPath($Archive)) -DestinationPath $taskSmoke
$taskListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$taskListener.Start()
$taskPort = $taskListener.LocalEndpoint.Port
$taskListener.Stop()
$taskBaseUrl = "http://127.0.0.1:$taskPort"
$taskConfigPath = Join-Path $taskSmoke 'config\server.json'
$taskConfig = Get-Content -LiteralPath $taskConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$taskConfig.Urls = $taskBaseUrl
$taskConfig.WindowsAuthentication.Enabled = $false
$taskConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $taskConfigPath -Encoding UTF8
$taskExe = Join-Path $taskSmoke 'app\DevRefresh.exe'
$taskPassword = 'Synthetic-admin-test-password!'
$taskSqlPassword = 'Synthetic-SQL-test-secret-do-not-share!'
$taskIbcmdPassword = 'Synthetic-1C-DT-secret-do-not-share!'
$taskHandler = [Net.Http.HttpClientHandler]::new()
$taskClient = [Net.Http.HttpClient]::new($taskHandler)
$taskClient.BaseAddress = [Uri]$taskBaseUrl
$taskClient.Timeout = [TimeSpan]::FromSeconds(20)
$taskLauncher = $null

function Assert-True($Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Invoke-TestApi([string]$Method, [string]$Route, $Body = $null) {
    $taskRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $Route)
    if ($null -ne $Body) {
        $taskSession = Invoke-TestApi 'GET' '/api/session'
        $taskRequest.Headers.Add('X-CSRF-TOKEN', [string]$taskSession.Body.csrfToken)
        $taskRequest.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 10), [Text.Encoding]::UTF8, 'application/json')
    }
    $taskResponse = $taskClient.SendAsync($taskRequest).GetAwaiter().GetResult()
    $taskText = $taskResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $taskResult = [pscustomobject]@{ StatusCode = [int]$taskResponse.StatusCode; Body = ($taskText | ConvertFrom-Json); Raw = $taskText }
    $taskResponse.Dispose()
    $taskRequest.Dispose()
    return $taskResult
}
function Start-TestPackage {
    $taskCmd = Join-Path $taskSmoke 'START.cmd'
    $taskArguments = '/d /c ""' + $taskCmd + '" --no-pause"'
    $script:taskLauncher = Start-Process -FilePath $env:ComSpec -ArgumentList $taskArguments -WorkingDirectory $env:TEMP -WindowStyle Hidden -PassThru
    $taskReady = $false
    for ($taskAttempt = 0; $taskAttempt -lt 40; $taskAttempt++) {
        if ($script:taskLauncher.HasExited) { throw ('START exited early. See ' + (Join-Path $taskSmoke 'logs\launcher.log')) }
        try {
            $taskHealth = Invoke-TestApi 'GET' '/health'
            if ($taskHealth.StatusCode -eq 200) { $taskReady = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    Assert-True $taskReady 'Portable executable did not become ready.'
}
function Stop-TestPackage {
    $taskOwnedProcesses = @(Get-Process -Name DevRefresh -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and [string]::Equals($_.Path, $taskExe, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($taskOwnedProcess in $taskOwnedProcesses) {
        Stop-Process -Id $taskOwnedProcess.Id
        $taskOwnedProcess.WaitForExit(10000) | Out-Null
    }
    if ($script:taskLauncher -and -not $script:taskLauncher.HasExited) { $script:taskLauncher.WaitForExit(10000) | Out-Null }
}
try {
    Start-TestPackage
    $taskSession = Invoke-TestApi 'GET' '/api/session'
    Assert-True $taskSession.Body.setupRequired 'Package must not contain a preconfigured administrator.'
    $taskSetup = Invoke-TestApi 'POST' '/api/setup' @{username='test_admin';password=$taskPassword;windowsAdmins=@()}
    Assert-True ($taskSetup.StatusCode -eq 200) 'Setup failed in portable executable.'
    $taskBackupPath = Join-Path $taskSmoke 'test-backups'
    New-Item -ItemType Directory -Path $taskBackupPath | Out-Null
    $taskSettings = @{server='tcp:127.0.0.1,1';authentication='sql';username='test_sql';password=$taskSqlPassword;backupDirectory=$taskBackupPath;sqlBackupDirectory=$taskBackupPath;trustServerCertificate=$false}
    $taskSettings.dtRestore=@{enabled=$false;backupDirectory=(Join-Path $taskSmoke 'test-dt');ibcmdDirectory='';workingDirectory='';
        infobaseUser='test_ib_admin';infobasePassword=$taskIbcmdPassword;sqlUsername='';timeoutMinutes=20}
    $taskSaved = Invoke-TestApi 'PUT' '/api/admin/settings' $taskSettings
    Assert-True ($taskSaved.StatusCode -eq 200) 'Settings save failed in portable executable.'
    Assert-True (-not $taskSaved.Raw.Contains($taskSqlPassword)) 'SQL password leaked in API response.'
    Assert-True (-not $taskSaved.Raw.Contains($taskIbcmdPassword)) 'IBCMD password leaked in API response.'
    $taskTest = Invoke-TestApi 'POST' '/api/admin/connection/test' $taskSettings
    Assert-True ($taskTest.StatusCode -eq 502) ('Native SQL driver must return a controlled connection error, got ' + $taskTest.StatusCode)
    Assert-True ([bool]$taskTest.Body.requestId) 'SQL error must include a request identifier.'
    Stop-TestPackage
    & $taskExe --contentRoot (Join-Path $taskSmoke 'app') --ServerConfiguration $taskConfigPath --Maintenance prepare-service
    Assert-True ($LASTEXITCODE -eq 0) 'Native executable could not prepare existing DPAPI keys for the service.'
    Assert-True (Test-Path -LiteralPath (Join-Path $taskSmoke 'data\.service-keys-machine')) 'Service key marker is missing.'
    Start-TestPackage
    $taskSession = Invoke-TestApi 'GET' '/api/session'
    Assert-True (-not $taskSession.Body.setupRequired) 'SQLite state was not preserved after restart.'
    $taskLogin = Invoke-TestApi 'POST' '/api/login' @{username='test_admin';password=$taskPassword}
    Assert-True ($taskLogin.StatusCode -eq 200) 'Saved administrator could not log in after restart.'
    $taskDtAfterRestart=Invoke-TestApi 'GET' '/api/admin/settings'
    Assert-True $taskDtAfterRestart.Body.connection.dtRestore.infobasePasswordConfigured 'IBCMD secret was not preserved after restart.'
    Assert-True ($taskDtAfterRestart.Body.connection.dtRestore.timeoutMinutes -eq 20) 'IBCMD configuration was not preserved.'
    $taskSettings.password = $null
    $taskAfterRestart = Invoke-TestApi 'POST' '/api/admin/connection/test' $taskSettings
    Assert-True ($taskAfterRestart.StatusCode -eq 502) 'Saved SQL password could not be decrypted after restart.'
    $taskCheck = Join-Path $taskSmoke 'CHECK.cmd'
    & $taskCheck --no-pause
    Assert-True ($LASTEXITCODE -eq 0) 'CHECK.cmd failed against the running portable executable.'
    # Make sensitive-looking fixtures: these must never be included in a support ZIP.
    [IO.File]::WriteAllText((Join-Path $taskSmoke 'data\private.key'), $taskSqlPassword)
    $taskCollect = Join-Path $taskSmoke 'COLLECT-LOGS.cmd'
    & $taskCollect --no-pause
    Assert-True ($LASTEXITCODE -eq 0) 'COLLECT-LOGS.cmd failed.'
    $taskSupportArchive = Get-ChildItem -LiteralPath (Join-Path $taskSmoke 'support') -Filter '*.zip' | Select-Object -First 1
    Assert-True ($null -ne $taskSupportArchive) 'No support archive was created.'
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskSupportArchive.FullName)
    try {
        $taskLogFound = $false
        $taskCorrelationFound = $false
        foreach ($taskEntry in $taskZip.Entries) {
            Assert-True ($taskEntry.FullName -notmatch '(?i)(sqlite|private\.key|server\.json|(^|[/\\])keys[/\\])') 'Support archive contains private application data.'
            if ($taskEntry.Length -eq 0) { continue }
            $taskReader = [IO.StreamReader]::new($taskEntry.Open())
            try { $taskContent = $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
            Assert-True (-not $taskContent.Contains($taskPassword)) 'Administrator password leaked into diagnostics.'
            Assert-True (-not $taskContent.Contains($taskSqlPassword)) 'SQL password leaked into diagnostics.'
            Assert-True (-not $taskContent.Contains($taskIbcmdPassword)) 'IBCMD password leaked into diagnostics.'
            if ($taskEntry.FullName -like '*.jsonl') { $taskLogFound = $true }
            if ($taskContent.Contains([string]$taskTest.Body.requestId)) { $taskCorrelationFound = $true }
        }
        Assert-True $taskLogFound 'Support archive contains no application logs.'
        Assert-True $taskCorrelationFound 'Request identifier was not found in collected logs.'
    } finally { $taskZip.Dispose() }
    Write-Output ('PASS: portable EXE, native SQLite/SQL, setup, persistence, DPAPI, CMD launch, CHECK and safe log collection. Fixture: ' + $taskSmoke)
} finally {
    Stop-TestPackage
    $taskClient.Dispose()
    $taskHandler.Dispose()
}
