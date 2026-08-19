<#
.SYNOPSIS
    Encrypts a connection string with Windows DPAPI (LocalMachine scope) and prints the
    "DPAPI:<base64>" value to paste into CBMSB2BLink's appsettings.json.

.DESCRIPTION
    Must be run ON THE MACHINE that will run the CBMSB2BLink scheduled task — DPAPI keys
    are machine-bound, so a value encrypted here cannot be decrypted on a different host.
    LocalMachine scope is used (not CurrentUser) because the task runs unattended,
    possibly under a service account whose profile is never loaded.

.PARAMETER ConnectionString
    The plaintext connection string to encrypt.

.EXAMPLE
    .\Protect-ConnectionString.ps1 -ConnectionString "Server=CBMS_SERVER;Database=CBMS;User Id=svc_cbmsb2blink;Password=P@ss;TrustServerCertificate=True;"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectionString
)

Add-Type -AssemblyName System.Security

$plainBytes = [System.Text.Encoding]::UTF8.GetBytes($ConnectionString)
$encryptedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
    $plainBytes,
    $null,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine
)
$encoded = [Convert]::ToBase64String($encryptedBytes)

Write-Output "DPAPI:$encoded"
