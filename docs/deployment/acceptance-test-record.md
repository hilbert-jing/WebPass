# WebPass production acceptance record

Complete this record on the target Windows Server and from an approved LAN client. Empty boxes mean not yet verified; this template is not evidence that deployment has occurred.

## Deployment identity

- Date/time:
- Operator:
- Reviewer:
- Source commit:
- Publish directory:
- Server IPv4 address:
- Allowed LAN CIDRs:
- HTTPS certificate thumbprint and expiry:
- Data-encryption certificate thumbprint and expiry:
- Database instance and database name:

## Automated evidence

- [ ] `dotnet test WebPass.sln -c Release` passed; Unit ___ / Integration ___ / Failed ___ / Skipped ___.
- [ ] `dotnet publish src\WebPass.Web -c Release -r win-x64 --self-contained false` passed.
- [ ] Published output contains `web.config`.
- [ ] EF reports no pending model changes.

Attach command output or record its approved storage location:

## Server checks

- [ ] IIS was installed before the .NET Hosting Bundle; `AspNetCoreModuleV2` is present.
- [ ] WebPass uses a dedicated low-privilege application pool identity.
- [ ] The site has an HTTPS binding and no HTTP binding.
- [ ] The HTTPS certificate SAN contains the client access IPv4 address.
- [ ] The data-encryption certificate is separate from the HTTPS certificate.
- [ ] Only the application-pool identity and approved administrators can read the data certificate private key.
- [ ] Windows Firewall permits TCP 443 only from the approved LAN CIDRs.
- [ ] SQL Server is reachable locally by WebPass and is not reachable from a LAN client.
- [ ] `/health` returns only application/database availability and no version, path, stack trace, or exception detail.
- [ ] A server restart restores SQL Server, IIS, the application pool, and `/health` without manual repair.

## Application checks

- [ ] An unauthenticated request is redirected to login over HTTPS.
- [ ] Login lockout and login request limiting behave as specified.
- [ ] Ordinary users cannot open administrator pages or the password export.
- [ ] A user without `SecretReveal` cannot reveal a password.
- [ ] Reauthentication expires after five minutes and is bound to the current session.
- [ ] Revealed values disappear from the page after 30 seconds and responses are `no-store`.
- [ ] Ordinary CSV/XLSX exports contain no password or cryptographic fields.
- [ ] Administrator password export is XLSX only, requires reauthentication, and is `no-store`.
- [ ] Import rejects blocking row errors atomically and does not persist plaintext staging data.
- [ ] State-changing forms reject a missing antiforgery token.
- [ ] Responses contain the expected CSP, `nosniff`, and referrer policy headers.
- [ ] Login, reauthentication, Ping, reveal, import, export, and administrative actions produce redacted audit records.

## Client certificate trust

- Client device/user:
- Browser:
- Tested URL:
- [ ] No certificate warning is shown.
- [ ] Issuer, validity period, and IPv4 SAN are correct.

## Result

- [ ] PASS
- [ ] FAIL
- Blocking observations:
- Follow-up owner and due date:
- Operator signature/date:
- Reviewer signature/date:
