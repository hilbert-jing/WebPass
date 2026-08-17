#Requires -RunAsAdministrator
#Requires -Modules WebAdministration

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$PublishPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40,128}$')]
    [string]$HttpsCertificateThumbprint,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40,128}$')]
    [string]$DataEncryptionCertificateThumbprint,

    [Parameter(Mandatory)]
    [ValidateScript({ $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork })]
    [ipaddress]$ListenAddress,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LanRemoteAddress,

    [string]$SiteName = 'WebPass',
    [string]$AppPoolName = 'WebPass',
    [ValidateRange(1, 65535)]
    [int]$HttpsPort = 443
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

function Get-LocalMachineCertificate {
    param([Parameter(Mandatory)][string]$Thumbprint)

    $normalized = $Thumbprint.Replace(' ', '').ToUpperInvariant()
    $certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$normalized" -ErrorAction Stop
    if ($certificate.NotAfter -le (Get-Date)) {
        throw "Certificate $normalized is expired."
    }

    return $certificate
}

function Grant-CertificatePrivateKeyRead {
    param(
        [Parameter(Mandatory)]$Certificate,
        [Parameter(Mandatory)][string]$Identity
    )

    if (-not $Certificate.HasPrivateKey) {
        throw "Certificate $($Certificate.Thumbprint) has no private key."
    }

    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    try {
        if ($rsa -is [System.Security.Cryptography.RSACng]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)"
        }
        elseif ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            $container = $rsa.CspKeyContainerInfo.UniqueKeyContainerName
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$container"
        }
        else {
            throw 'The data-encryption certificate must use a Windows RSA private key.'
        }

        if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
            throw "Private-key file was not found for certificate $($Certificate.Thumbprint)."
        }

        if ($PSCmdlet.ShouldProcess($keyPath, "Grant read permission to $Identity")) {
            $acl = Get-Acl -LiteralPath $keyPath
            $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
                $Identity,
                [System.Security.AccessControl.FileSystemRights]::Read,
                [System.Security.AccessControl.AccessControlType]::Allow)
            $acl.SetAccessRule($rule)
            Set-Acl -LiteralPath $keyPath -AclObject $acl
        }
    }
    finally {
        if ($null -ne $rsa) {
            $rsa.Dispose()
        }
    }
}

if (-not (Get-WebGlobalModule -Name AspNetCoreModuleV2 -ErrorAction SilentlyContinue)) {
    throw 'ASP.NET Core Module V2 is missing. Install IIS first, then install or repair the .NET Hosting Bundle.'
}

$httpsCertificate = Get-LocalMachineCertificate -Thumbprint $HttpsCertificateThumbprint
$dataCertificate = Get-LocalMachineCertificate -Thumbprint $DataEncryptionCertificateThumbprint
if ($httpsCertificate.Thumbprint -eq $dataCertificate.Thumbprint) {
    throw 'HTTPS and data encryption must use separate certificates.'
}
if (-not $httpsCertificate.HasPrivateKey) {
    throw 'The HTTPS certificate must include its private key.'
}
if (-not $dataCertificate.HasPrivateKey) {
    throw 'The data-encryption certificate must include its private key.'
}

$resolvedPublishPath = (Resolve-Path -LiteralPath $PublishPath).Path
$appPoolIdentity = "IIS AppPool\$AppPoolName"

if (-not (Test-Path -LiteralPath "IIS:\AppPools\$AppPoolName")) {
    if ($PSCmdlet.ShouldProcess($AppPoolName, 'Create dedicated IIS application pool')) {
        New-WebAppPool -Name $AppPoolName | Out-Null
    }
}

if ($PSCmdlet.ShouldProcess($AppPoolName, 'Configure low-privilege application-pool identity')) {
    Set-ItemProperty -LiteralPath "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty -LiteralPath "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value ApplicationPoolIdentity
}

if ($PSCmdlet.ShouldProcess($resolvedPublishPath, "Grant read and execute permission to $appPoolIdentity")) {
    $acl = Get-Acl -LiteralPath $resolvedPublishPath
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $appPoolIdentity,
        [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $resolvedPublishPath -AclObject $acl
}

if (-not (Test-Path -LiteralPath "IIS:\Sites\$SiteName")) {
    if ($PSCmdlet.ShouldProcess($SiteName, 'Create HTTPS-only IIS site')) {
        New-Website -Name $SiteName -PhysicalPath $resolvedPublishPath -ApplicationPool $AppPoolName `
            -IPAddress $ListenAddress.IPAddressToString -Port $HttpsPort -Ssl | Out-Null
    }
}

$httpBindings = @(Get-WebBinding -Name $SiteName -Protocol http -ErrorAction SilentlyContinue)
if ($httpBindings.Count -gt 0) {
    throw "Site $SiteName has an HTTP binding. Remove it before production acceptance."
}

$bindingInformation = "$($ListenAddress.IPAddressToString):$HttpsPort`:"
$httpsBinding = Get-WebBinding -Name $SiteName -Protocol https -ErrorAction SilentlyContinue |
    Where-Object bindingInformation -EQ $bindingInformation
if (-not $httpsBinding) {
    if ($PSCmdlet.ShouldProcess($SiteName, "Add HTTPS binding $bindingInformation")) {
        New-WebBinding -Name $SiteName -Protocol https -IPAddress $ListenAddress.IPAddressToString `
            -Port $HttpsPort | Out-Null
    }
}

$sslBindingPath = "IIS:\SslBindings\$($ListenAddress.IPAddressToString)!$HttpsPort"
if (-not (Test-Path -LiteralPath $sslBindingPath)) {
    if ($PSCmdlet.ShouldProcess($sslBindingPath, 'Bind HTTPS certificate')) {
        $httpsCertificate | New-Item -Path $sslBindingPath | Out-Null
    }
}
else {
    $boundThumbprint = (Get-Item -LiteralPath $sslBindingPath).Thumbprint
    if ($boundThumbprint -ne $httpsCertificate.Thumbprint) {
        throw "HTTPS binding already uses certificate $boundThumbprint. Review it manually; the script will not replace it."
    }
}

Grant-CertificatePrivateKeyRead -Certificate $dataCertificate -Identity $appPoolIdentity

$firewallRuleName = "$SiteName HTTPS - LAN only"
$existingRule = Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue
if (-not $existingRule) {
    if ($PSCmdlet.ShouldProcess($firewallRuleName, 'Create LAN-only inbound firewall rule')) {
        New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $HttpsPort -RemoteAddress $LanRemoteAddress `
            -Profile Domain,Private | Out-Null
    }
}
elseif ($PSCmdlet.ShouldProcess($firewallRuleName, 'Refresh LAN-only firewall scope')) {
    Set-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow `
        -Profile Domain,Private | Out-Null
    $existingRule |
    Get-NetFirewallAddressFilter |
    Set-NetFirewallAddressFilter -RemoteAddress $LanRemoteAddress |
    Out-Null
}

Write-Host 'IIS initialization completed.'
Write-Host "Application pool identity: $appPoolIdentity"
Write-Host "Set SecretEncryption:CertificateThumbprint to $($dataCertificate.Thumbprint) in production configuration."
Write-Host 'Verify SQL Server is local-only and grant the application-pool identity only required database permissions.'
Write-Host 'Complete the verification section in DEPLOYMENT.md from a trusted LAN client.'
