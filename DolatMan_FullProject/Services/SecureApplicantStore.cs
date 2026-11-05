using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DolatMan.Models;

namespace DolatMan.Services;

/// <summary>Stores applicant data encrypted for the current Windows user with DPAPI.</summary>
public sealed class SecureApplicantStore
{
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DolatMan");
    private string FilePath => Path.Combine(_directory, "applicants.dat");

    public List<Applicant> Load()
    {
        if (!File.Exists(FilePath)) return new List<Applicant>();
        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            var json = Dpapi.Unprotect(encrypted);
            return JsonSerializer.Deserialize<List<Applicant>>(json) ?? new List<Applicant>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "فایل اطلاعات قابل رمزگشایی نیست. آن را با همان حساب ویندوزی باز کنید که اطلاعات را ذخیره کرده است.", ex);
        }
    }

    public void Save(IEnumerable<Applicant> applicants)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.SerializeToUtf8Bytes(applicants, new JsonSerializerOptions { WriteIndented = true });
        var encrypted = Dpapi.Protect(json);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, FilePath, true);
    }

    public string DataDirectory => _directory;

    private static class Dpapi
    {
        private const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int Length; public IntPtr Data; }

        [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob input, string description,
            IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
            IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("Kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Protect(byte[] data) => Transform(data, protect: true);
        public static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

        private static byte[] Transform(byte[] data, bool protect)
        {
            if (data.Length == 0) return Array.Empty<byte>();
            var inputMemory = Marshal.AllocHGlobal(data.Length);
            var input = new DataBlob { Length = data.Length, Data = inputMemory };
            DataBlob output = default;
            try
            {
                Marshal.Copy(data, 0, inputMemory, data.Length);
                var ok = protect
                    ? CryptProtectData(ref input, "DolatMan Applicant Data", IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, UiForbidden, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(inputMemory);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
    }
}
