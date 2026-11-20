param([Parameter(Mandatory=$true)][string]$PlatformDirectory)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskWork=Join-Path ([IO.Path]::GetTempPath()) ('devrefresh-onec-build-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskWork|Out-Null
try{
    & (Join-Path $PlatformDirectory 'ibcmd.exe') infobase create ('--db-path='+$taskWork+'\file') ('--data='+$taskWork+'\data') --force
    if($LASTEXITCODE-ne 0){throw 'Cannot create the processor build infobase'}
    $taskArguments=@('DESIGNER','/F',('"'+$taskWork+'\file"'),'/LoadExternalDataProcessorOrReportFromFiles',
        ('"'+(Join-Path $taskRoot 'tools\OneC\DevRefreshDomain.xml')+'"'),
        ('"'+(Join-Path $taskRoot 'src\DevRefresh\OneC\DevRefreshDomain.epf')+'"'),'/Out',('"'+$taskWork+'\build.log"'),'/DisableStartupDialogs','/DisableStartupMessages')
    $taskProcess=Start-Process -FilePath (Join-Path $PlatformDirectory '1cv8.exe') -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
    if(-not $taskProcess.WaitForExit(120000)){$taskProcess.Kill();throw 'Processor build timed out'}
    Get-Content -LiteralPath (Join-Path $taskWork 'build.log')
    if($taskProcess.ExitCode-ne 0){throw 'Processor build failed'}
}finally{
    $taskResolved=[IO.Path]::GetFullPath($taskWork)
    $taskTemp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if(-not $taskResolved.StartsWith($taskTemp,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $taskResolved -Leaf)-notmatch '\Adevrefresh-onec-build-[0-9a-f]{32}\z'){throw 'Unexpected build cleanup path'}
    Remove-Item -LiteralPath $taskResolved -Recurse -Force
}
