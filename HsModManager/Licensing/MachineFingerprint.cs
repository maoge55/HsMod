using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HsModManager.Licensing;

public static class MachineFingerprint
{
    public static string Create()
    {
        string machineGuid = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                "")
            ?.ToString() ?? "";
        string material = $"{Environment.MachineName}|{Environment.UserName}|{machineGuid}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..32];
    }
}
