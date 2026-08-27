using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HsModManager.Licensing;

public sealed record SavedLicenseCredential(
    string ServerUrl,
    string LicenseKey,
    DateTime? ExpiresAtUtc,
    DateTime? ServerTimeUtc,
    DateTime? CheckedAtUtc,
    string Status,
    string Message);

public static class LicenseCredentialStore
{
    private const string FileMagic = "HsAuto.LicenseCredential";
    private const int CurrentFileVersion = 1;
    private const uint CryptProtectUiForbidden = 0x1;
    private const string StoreMutexName = @"Local\HsAuto.LicenseCredentialStore";
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("HsAuto.License.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HsAuto",
        "license-auth.bin");

    public static SavedLicenseCredential? Load()
    {
        return WithStoreLock(() =>
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            try
            {
                byte[] clearBytes = Unprotect(File.ReadAllBytes(FilePath));
                CredentialState? state = JsonSerializer.Deserialize<CredentialState>(clearBytes, JsonOptions);
                if (state?.Magic != FileMagic
                    || state.Version != CurrentFileVersion
                    || string.IsNullOrWhiteSpace(state.ServerUrl)
                    || string.IsNullOrWhiteSpace(state.LicenseKey))
                {
                    return null;
                }

                return new SavedLicenseCredential(
                    state.ServerUrl.Trim(),
                    NormalizeKey(state.LicenseKey),
                    state.ExpiresAtUtc,
                    state.ServerTimeUtc,
                    state.CheckedAtUtc,
                    state.Status ?? "",
                    state.Message ?? "");
            }
            catch
            {
                return null;
            }
        });
    }

    public static void Save(LicenseSession session)
    {
        WithStoreLock(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var state = new CredentialState
            {
                ServerUrl = session.ServerUrl.Trim(),
                LicenseKey = NormalizeKey(session.LicenseKey),
                ExpiresAtUtc = session.ExpiresAtUtc,
                ServerTimeUtc = session.ServerTimeUtc,
                CheckedAtUtc = session.CheckedAtUtc,
                Status = session.Status,
                Message = session.Message
            };
            byte[] clearBytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            string tempPath = FilePath + ".tmp";
            File.WriteAllBytes(tempPath, Protect(clearBytes));
            File.Move(tempPath, FilePath, true);
            return true;
        });
    }

    public static string NormalizeKey(string key)
    {
        return new string(key
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .Take(8)
            .ToArray());
    }

    private static T WithStoreLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, StoreMutexName);
        bool lockTaken = false;
        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                throw new TimeoutException("Timed out waiting for the license credential store.");
            }

            return action();
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static byte[] Protect(byte[] clearBytes)
    {
        DataBlob input = CreateBlob(clearBytes);
        DataBlob entropy = CreateBlob(OptionalEntropy);
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "HsAuto license credential",
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out DataBlob output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var protectedBytes = new byte[output.Size];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Size);
                return protectedBytes;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    private static byte[] Unprotect(byte[] protectedBytes)
    {
        DataBlob input = CreateBlob(protectedBytes);
        DataBlob entropy = CreateBlob(OptionalEntropy);
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out DataBlob output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var clearBytes = new byte[output.Size];
                Marshal.Copy(output.Data, clearBytes, 0, output.Size);
                return clearBytes;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var blob = new DataBlob
        {
            Size = bytes.Length,
            Data = Marshal.AllocHGlobal(bytes.Length)
        };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private sealed class CredentialState
    {
        public string Magic { get; set; } = FileMagic;
        public int Version { get; set; } = CurrentFileVersion;
        public string ServerUrl { get; set; } = "";
        public string LicenseKey { get; set; } = "";
        public DateTime? ExpiresAtUtc { get; set; }
        public DateTime? ServerTimeUtc { get; set; }
        public DateTime? CheckedAtUtc { get; set; }
        public string Status { get; set; } = "";
        public string Message { get; set; } = "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
