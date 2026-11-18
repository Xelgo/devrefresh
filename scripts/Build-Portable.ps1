param([string]$OutputDirectory, [ValidateSet('Install','Update','Both')][string]$Kind = 'Both')
$ErrorActionPreference = 'Stop'
$taskSourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSdk = Join-Path $taskSourceRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source }
$taskProject = Join-Path $taskSourceRoot 'src\DevRefresh\DevRefresh.csproj'
$taskVersion = ([xml](Get-Content -LiteralPath $taskProject -Raw)).Project.PropertyGroup.Version
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskSourceRoot 'dist' }
$taskOutputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutputRoot -Force | Out-Null
$taskPackageName = "DevRefresh-$taskVersion-install-win-x64-$taskStamp"
$taskPackage = Join-Path $taskOutputRoot $taskPackageName
if (Test-Path -LiteralPath $taskPackage) { throw 'Package directory already exists. Wait one second or choose another output directory.' }
New-Item -ItemType Directory -Path $taskPackage | Out-Null
& $taskSdk publish $taskProject -c Portable -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:Optimize=true' '-p:IsTransformWebConfigDisabled=true' -o (Join-Path $taskPackage 'app') --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }
$taskTemplate = Join-Path $taskSourceRoot 'deploy\portable'
Get-ChildItem -LiteralPath $taskTemplate -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskPackage -Recurse }
$taskUnwanted = @(Get-ChildItem -LiteralPath $taskPackage -Recurse -Force | Where-Object {
    $_.Name -in @('App_Data','data','keys') -or $_.Name -match '\.(sqlite|sqlite3|db|pfx|key)(-wal|-shm)?$'
})
if ($taskUnwanted.Count -gt 0) { throw 'Runtime data or secrets were found in the package. Packaging refused.' }
$taskExe = Join-Path $taskPackage 'app\DevRefresh.exe'
# cmd.exe requires CRLF for reliable execution on all supported Windows hosts.
Get-ChildItem -LiteralPath $taskPackage -Filter '*.cmd' -File | ForEach-Object {
    $taskCmdText = [IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n").Replace("`n", "`r`n")
    [IO.File]::WriteAllText($_.FullName, $taskCmdText, [Text.Encoding]::ASCII)
}
$taskFiles = [ordered]@{}
Get-ChildItem -LiteralPath $taskPackage -Recurse -File | Where-Object { $_.FullName -notlike (Join-Path $taskPackage 'config\*') } | ForEach-Object {
    $taskRelative = $_.FullName.Substring($taskPackage.Length + 1).Replace('\','/')
    $taskFiles[$taskRelative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$taskBuild = [ordered]@{
    product='DevRefresh'; version=[string]$taskVersion; schemaVersion=11; packageKind='install'; runtime='win-x64'; selfContained=$true
    builtAtUtc=[DateTime]::UtcNow.ToString('O'); executableSha256=$taskFiles['app/DevRefresh.exe']; files=$taskFiles
}
$taskBuild | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskPackage 'BUILD-INFO.json') -Encoding UTF8
$taskPackages = @()
if ($Kind -in @('Install','Both')) { $taskPackages += $taskPackage }
if ($Kind -in @('Update','Both')) {
    $taskUpdatePackage = Join-Path $taskOutputRoot ("DevRefresh-$taskVersion-update-win-x64-$taskStamp")
    New-Item -ItemType Directory -Path $taskUpdatePackage | Out-Null
    Get-ChildItem -LiteralPath $taskPackage -Force | Where-Object { $_.Name -ne 'config' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskUpdatePackage -Recurse }
    $taskBuild.packageKind = 'update'
    $taskBuild | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskUpdatePackage 'BUILD-INFO.json') -Encoding UTF8
    $taskPackages += $taskUpdatePackage
}
foreach ($taskReadyPackage in $taskPackages) {
    $taskArchive = $taskReadyPackage + '.zip'
    Compress-Archive -Path (Join-Path $taskReadyPackage '*') -DestinationPath $taskArchive -CompressionLevel Optimal
    $taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(($taskArchive + '.sha256'), $taskHash + '  ' + [IO.Path]::GetFileName($taskArchive) + "`r`n", [Text.Encoding]::ASCII)
    Write-Output ('Package: ' + $taskArchive)
    Write-Output ('SHA256: ' + $taskHash)
}
