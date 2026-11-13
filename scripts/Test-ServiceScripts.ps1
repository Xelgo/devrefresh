$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\deploy\portable\scripts\Service-Common.ps1')

# SCM is mocked: these tests never install or stop a real Windows service.
function Reset-Fixture([string]$Status) {
    $script:fixture = if($Status){[pscustomobject]@{Status=$Status}}else{$null}
    $script:calls=@{Admin=0;Prepare=0;New=0;Start=0;Stop=0;Wait=0}
    $script:allowAdmin=$true
}
function Assert-Equal($Expected,$Actual) { if($Expected -cne $Actual){throw "Expected [$Expected], got [$Actual]"} }
function Get-Service { [CmdletBinding()]param($Name) return $script:fixture }
function Assert-ServiceAdministrator { $script:calls.Admin++; if(-not $script:allowAdmin){throw 'Admin required'} }
function Initialize-ServiceFiles {
    param($PackageRoot,$ServiceName)
    $script:calls.Prepare++
    return [pscustomobject]@{Exe='C:\Dev Refresh\app\DevRefresh.exe';App='C:\Dev Refresh\app';Config='C:\Dev Refresh\config\server.json'}
}
function New-Service {
    [CmdletBinding()]param($Name,$BinaryPathName,$DisplayName,$Description,$StartupType,$Credential)
    $script:calls.New++
    Assert-Equal 'Automatic' $StartupType
    Assert-Equal $false ($PSBoundParameters.ContainsKey('Credential'))
    Assert-Equal 'DevRefresh' $DisplayName
    Assert-Equal '"C:\Dev Refresh\app\DevRefresh.exe" --contentRoot "C:\Dev Refresh\app" --ServerConfiguration "C:\Dev Refresh\config\server.json" --WindowsService:Name "DevRefresh"' $BinaryPathName
    $script:fixture=[pscustomobject]@{Status='Stopped';StartName='LocalSystem'}
}
function Start-Service { [CmdletBinding()]param($Name) $script:calls.Start++;$script:fixture.Status='Running' }
function Stop-Service { [CmdletBinding()]param($Name) $script:calls.Stop++;$script:fixture.Status='Stopped' }
function Wait-DevRefreshService($Service,[string]$Status) { $script:calls.Wait++;$Service.Status=$Status }

Reset-Fixture ''
Start-DevRefreshService 'DevRefresh' 'C:\Dev Refresh' | Out-Null
Assert-Equal 1 $calls.New; Assert-Equal 1 $calls.Start; Assert-Equal 1 $calls.Prepare
Assert-Equal 'LocalSystem' $script:fixture.StartName
Start-DevRefreshService 'DevRefresh' 'C:\Dev Refresh' | Out-Null
Assert-Equal 1 $calls.New; Assert-Equal 1 $calls.Start
Reset-Fixture 'Stopped'
$script:fixture | Add-Member -NotePropertyName StartName -NotePropertyValue 'CORP\manually-selected-user'
Start-DevRefreshService 'DevRefresh' 'C:\Dev Refresh' | Out-Null
Assert-Equal 0 $calls.New; Assert-Equal 0 $calls.Prepare; Assert-Equal 1 $calls.Start
Assert-Equal 'CORP\manually-selected-user' $script:fixture.StartName
Reset-Fixture 'Running'
Stop-DevRefreshService 'DevRefresh' | Out-Null
Assert-Equal 1 $calls.Stop; Assert-Equal 0 $calls.Prepare; Assert-Equal 0 $calls.New
Reset-Fixture ''
$message=Stop-DevRefreshService 'DevRefresh'
Assert-Equal 0 $calls.Admin; Assert-Equal 0 $calls.Stop
if(-not $message.Contains('не установлена')){throw 'Missing absent-service message'}
Reset-Fixture 'Stopped'
Stop-DevRefreshService 'DevRefresh' | Out-Null
Assert-Equal 0 $calls.Stop; Assert-Equal 0 $calls.Admin
Reset-Fixture 'StartPending'
Start-DevRefreshService 'DevRefresh' 'C:\Dev Refresh' | Out-Null
Assert-Equal 1 $calls.Wait; Assert-Equal 0 $calls.New; Assert-Equal 0 $calls.Start
Reset-Fixture 'StopPending'
Start-DevRefreshService 'DevRefresh' 'C:\Dev Refresh' | Out-Null
Assert-Equal 2 $calls.Wait; Assert-Equal 1 $calls.Start; Assert-Equal 0 $calls.New
Reset-Fixture 'StopPending'
Stop-DevRefreshService 'DevRefresh' | Out-Null
Assert-Equal 1 $calls.Wait; Assert-Equal 0 $calls.Stop
Reset-Fixture ''
$script:allowAdmin=$false
$rejected=$false
try{ Start-DevRefreshService 'DevRefresh' 'C:\Dev Refresh' | Out-Null }catch{$rejected=$true}
Assert-Equal $true $rejected; Assert-Equal 0 $calls.New; Assert-Equal 0 $calls.Prepare
Write-Output 'PASS: service lifecycle scenarios; no real SCM changes.'
