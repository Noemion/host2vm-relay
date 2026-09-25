using System.Security.Cryptography;
using System.Text;

namespace Host2VMRelay;

public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KylinTunnel/v1");
    public static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => value.Length == 0 ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
}
