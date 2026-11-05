#Requires -RunAsAdministrator
# One-time setup for the uiAccess build: creates a self-signed code-signing certificate in the current user's store
# and trusts it machine-wide. Run from an elevated PowerShell. See README.
$ErrorActionPreference = 'Stop'
$subject = 'CN=Mindows Local Code Signing'   # keep in sync with SigningSubject in Mindows.csproj
$cer = Join-Path $env:TEMP 'MindowsSigning.cer'

# signtool /n fails when two certificates match, so keep the existing one.
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    # Windows checks the signature chain at every launch and the certificate is not timestamped, so an expired one would stop Mindows from starting.
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(30)
}

# Only the public part leaves the store. uiAccess needs a trusted root; TrustedPublisher is left out on purpose.
if (-not (Test-Path "Cert:\LocalMachine\Root\$($cert.Thumbprint)")) {
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Remove-Item $cer
}
$cert | Format-List Subject, Thumbprint, NotAfter
