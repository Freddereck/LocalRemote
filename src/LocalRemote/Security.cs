using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace LocalRemote;

public sealed record Invitation(string Host, int Port, string Fingerprint, string Secret)
{
    public override string ToString() => $"localremote://{Host}:{Port}/{Fingerprint}#{Secret}";
    public static Invitation Parse(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "localremote" && uri.Scheme != "newanydesk") || uri.UserInfo.Length != 0)
            throw new FormatException("Вставьте полный код подключения со второго ПК.");
        string fingerprint = uri.AbsolutePath.Trim('/').ToUpperInvariant();
        string secret = uri.Fragment.TrimStart('#');
        if (!IPAddress.TryParse(uri.Host, out var ip) || !LanAddresses.IsPrivate(ip) || uri.Port is < 1024 or > 65535 ||
            fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit) || secret.Length != 64 || !secret.All(Uri.IsHexDigit))
            throw new FormatException("Код подключения повреждён или адрес не относится к локальной сети.");
        return new(uri.Host, uri.Port, fingerprint, secret.ToUpperInvariant());
    }
}

public static class LanAddresses
{
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
    }
    public static LocalAddress[] GetAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .OrderBy(n => n.GetIPProperties().GatewayAddresses.Any(g => IsPrivate(g.Address) && !g.Address.Equals(IPAddress.Any)) ? 0 : 1)
        .ThenBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => IsPrivate(a.Address)).Select(a => new LocalAddress(a.Address, n.Name)))
        .DistinctBy(a => a.Address).ToArray();
}
public sealed record LocalAddress(IPAddress Address, string Name)
{
    public override string ToString() => $"{Address} · {Name}";
}

public sealed class HostIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string Secret { get; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData));
    private HostIdentity(X509Certificate2 certificate, string secret) { Certificate = certificate; Secret = secret; }
    public static HostIdentity Create()
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=LocalRemote", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return new(X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }
    public static HostIdentity LoadOrCreate(string? identityPath = null)
    {
        string path = identityPath ?? Settings.PathFor("host.identity");
        if (File.Exists(path))
        {
            var data = JsonSerializer.Deserialize<SavedIdentity>(Dpapi.Unprotect(File.ReadAllBytes(path))) ?? throw new InvalidDataException("Не удалось прочитать ключ ПК.");
            return new(X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(data.Pfx), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet), data.Secret);
        }
        var identity = Create();
        var saved = new SavedIdentity(Convert.ToBase64String(identity.Certificate.Export(X509ContentType.Pfx)), identity.Secret);
        Settings.AtomicWrite(path, Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(saved)));
        return identity;
    }
    public bool CheckSecret(string secret)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(secret), Convert.FromHexString(Secret)); }
        catch (FormatException) { return false; }
    }
    public void Dispose() => Certificate.Dispose();
    private sealed record SavedIdentity(string Pfx, string Secret);
}

public static class Settings
{
    public static string PathFor(string name)
    {
        string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalRemote");
        MigrateLegacySettings(directory, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewAnyDesk"));
        return System.IO.Path.Combine(directory, name);
    }
    public static void MigrateLegacySettings(string directory, string legacyDirectory)
    {
        Directory.CreateDirectory(directory);
        string marker = System.IO.Path.Combine(directory, "legacy-imported");
        if (File.Exists(marker)) return;
        foreach (string name in new[] { "host.identity", "viewer.connection" })
        {
            string source = System.IO.Path.Combine(legacyDirectory, name), target = System.IO.Path.Combine(directory, name);
            if (!File.Exists(source) || File.Exists(target)) continue;
            try { File.Copy(source, target, false); }
            catch (IOException) when (File.Exists(target)) { }
        }
        // Mark the one-time import so removing a saved key does not restore it from the old app.
        using var done = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
    }
    public static void AtomicWrite(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
    }
    public static void SaveInvitation(string? invitation)
    {
        string path = PathFor("viewer.connection");
        if (invitation == null) { if (File.Exists(path)) File.Delete(path); return; }
        AtomicWrite(path, Dpapi.Protect(Encoding.UTF8.GetBytes(invitation)));
    }
    public static string LoadInvitation()
    {
        try { string path = PathFor("viewer.connection"); return File.Exists(path) ? Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(path))) : ""; }
        catch { return ""; }
    }
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "LocalRemote", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
