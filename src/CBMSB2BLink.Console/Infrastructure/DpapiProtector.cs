using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace CBMSB2BLink.App.Infrastructure;

/// <summary>
/// Decrypts "DPAPI:&lt;base64&gt;" connection-string values produced by
/// tools/Protect-ConnectionString.ps1. Uses DataProtectionScope.LocalMachine because the
/// app runs unattended via Task Scheduler, possibly under a service account whose
/// profile is never loaded — CurrentUser-scoped keys would not reliably decrypt in that
/// case. This also means any process on the same machine can decrypt the value; treat
/// the app's install directory ACLs as the real protection boundary, DPAPI as
/// defense against the config file being copied off the machine.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DpapiProtector
{
    private const string Prefix = "DPAPI:";

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Unprotect(string value)
    {
        if (!IsProtected(value))
        {
            return value;
        }

        var payload = value[Prefix.Length..];
        var encryptedBytes = Convert.FromBase64String(payload);
        var plainBytes = ProtectedData.Unprotect(encryptedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public static string Protect(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(encryptedBytes);
    }
}
