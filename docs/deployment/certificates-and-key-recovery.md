# Certificates and data-key recovery

WebPass uses two independent certificates. The HTTPS certificate protects browser transport at IIS. The data-encryption certificate wraps AES data keys and must never be used as the IIS TLS certificate.

## Create or import the data-encryption certificate

Use an organization-issued RSA certificate when available. For a locally managed internal deployment, an administrator may create a dedicated RSA certificate in `LocalMachine\My`:

```powershell
$dataCert = New-SelfSignedCertificate `
  -Subject 'CN=WebPass Data Encryption' `
  -CertStoreLocation 'Cert:\LocalMachine\My' `
  -KeyAlgorithm RSA `
  -KeyLength 3072 `
  -KeyExportPolicy Exportable `
  -KeyUsage KeyEncipherment,DataEncipherment `
  -NotAfter (Get-Date).AddYears(5)
$dataCert.Thumbprint
```

Configure the printed thumbprint as `SecretEncryption:CertificateThumbprint`. Grant private-key read permission only to the dedicated IIS application-pool identity; `Initialize-WebPass.ps1` performs that ACL step.

## Offline encrypted PFX recovery copy

The data-encryption certificate private key is required to decrypt existing server passwords. Export one encrypted PFX recovery copy immediately after certificate provisioning:

```powershell
$pfxPassword = Read-Host 'PFX password' -AsSecureString
Export-PfxCertificate `
  -Cert "Cert:\LocalMachine\My\<data certificate thumbprint>" `
  -FilePath 'D:\WebPass-Recovery\webpass-data-key.pfx' `
  -Password $pfxPassword `
  -CryptoAlgorithmOption AES256_SHA256
```

Move the PFX to approved offline encrypted media. Store its password separately. Do not place the PFX, password, or private-key material in the application directory, source repository, ordinary XLSX/CSV exports, logs, or audit records. Test the recovery copy on an isolated machine before declaring it usable.

## Recovery after certificate loss

1. Stop the WebPass IIS site. Do not create a replacement data certificate and point the application at it; a new private key cannot unwrap existing data keys.
2. Import the encrypted recovery PFX into `LocalMachine\My` on the replacement server.
3. Confirm the imported certificate thumbprint exactly matches the configured thumbprint and that it has an RSA private key.
4. Grant `IIS AppPool\WebPass` read access to that private key, either with the initialization script or Certificate Manager.
5. Start the site and verify `/health`.
6. With an authorized test account, reauthenticate and reveal one known non-production test credential. Confirm the reveal audit contains metadata only and no password.

If the matching private key and its recovery copy are both lost, existing encrypted passwords cannot be recovered by WebPass. Restore the correct certificate material before making further secret changes.

## HTTPS trust verification

Install the issuing internal root certificate in each approved client's Trusted Root Certification Authorities store. Browse to the exact HTTPS IP address used in the certificate SAN. Stop acceptance if the browser reports an unknown issuer, name/IP mismatch, expiry, or any other certificate warning.
