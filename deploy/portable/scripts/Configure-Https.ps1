param([string]$DnsName, [string]$Thumbprint, [ValidateRange(1024,65535)][int]$Port = 5443,
    [ValidateSet('LocalMachine','CurrentUser')][string]$StoreLocation = 'LocalMachine')
. (Join-Path $PSScriptRoot 'Common.ps1')
try {
    if (-not $DnsName) { $DnsName = Read-Host 'Corporate DNS name of this site (example: devrefresh.company.local)' }
    if (-not $Thumbprint) { $Thumbprint = Read-Host 'SHA1 thumbprint of the existing HTTPS certificate in LocalMachine\My' }
    $DnsName = $DnsName.Trim().ToLowerInvariant()
    $Thumbprint = $Thumbprint.Replace(' ','').Trim().ToUpperInvariant()
    if ($DnsName.Length -gt 253 -or $DnsName -notmatch '^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$' -or $DnsName.Contains('..')) { throw 'Invalid DNS name.' }
    if ($Thumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'Use the 40-character SHA1 certificate thumbprint.' }
    $taskCert = Get-ChildItem -Path "Cert:\$StoreLocation\My" | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
    if (-not $taskCert -or -not $taskCert.HasPrivateKey) { throw 'Certificate with a private key was not found in the selected store.' }
    if ([DateTime]::Now -lt $taskCert.NotBefore -or [DateTime]::Now -ge $taskCert.NotAfter) { throw 'Certificate is expired or not yet valid.' }
    $taskDnsNames = @($taskCert.DnsNameList | ForEach-Object { [string]$_.Unicode })
    $taskNameMatches = $false
    foreach ($taskCertificateName in $taskDnsNames) {
        $taskRegex = '^' + [Regex]::Escape($taskCertificateName).Replace('\*','[^.]+') + '$'
        if ($DnsName -match $taskRegex) { $taskNameMatches = $true }
    }
    if (-not $taskNameMatches) { throw 'The requested DNS name is not covered by the certificate DNS names.' }
    $taskEku = @($taskCert.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value })
    if ($taskEku.Count -gt 0 -and $taskEku -notcontains '1.3.6.1.5.5.7.3.1' -and $taskEku -notcontains '2.5.29.37.0') { throw 'Certificate is not valid for TLS server authentication.' }
    $taskConfig = Get-ServerConfig
    $taskLocalUrl = Get-LocalHealthUrl $taskConfig
    if (-not $taskLocalUrl) { throw 'Keep a loopback HTTP URL in config/server.json for local setup and diagnostics.' }
    $taskLocalBase = ([Uri]$taskLocalUrl).GetLeftPart([UriPartial]::Authority)
    if (([Uri]$taskLocalBase).Port -eq $Port) { throw 'HTTPS and local HTTP ports must differ.' }
    $taskConfig.Urls = $taskLocalBase + ';https://0.0.0.0:' + $Port
    $taskConfig.AllowedHosts = 'localhost;127.0.0.1;[::1];' + $DnsName
    $taskConfig | Add-Member -NotePropertyName HttpsCertificate -NotePropertyValue ([pscustomobject]@{ Thumbprint = $Thumbprint; StoreLocation = $StoreLocation }) -Force
    $taskConfigPath = Join-Path $taskPackageRoot 'config\server.json'
    Copy-Item -LiteralPath $taskConfigPath -Destination ($taskConfigPath + '.before-https-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
    $taskConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $taskConfigPath -Encoding UTF8
    Write-Output ('HTTPS configured: https://' + $DnsName + ':' + $Port + '/')
    Write-Output 'Restart DevRefresh. DNS, client certificate trust, private-key permissions and firewall access must be configured on the server/network.'
    exit 0
} catch {
    Write-Error ('HTTPS setup failed: ' + $_.Exception.Message)
    exit 1
}
