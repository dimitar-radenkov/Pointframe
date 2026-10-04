using System.Security.Cryptography;
using System.Text;

namespace Pointframe.Services;

internal static class ShareHeaderProtection
{
    public static ProtectedShareHeader Protect(string name, string value)
    {
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return new ProtectedShareHeader
        {
            Name = name,
            ProtectedValue = Convert.ToBase64String(encrypted),
        };
    }

    public static string Unprotect(ProtectedShareHeader header)
    {
        var encrypted = Convert.FromBase64String(header.ProtectedValue);
        var value = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(value);
    }
}
