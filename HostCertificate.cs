using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenDesk;

internal static class HostCertificate
{
    internal static X509Certificate2 Certificate { get; } = Load();
    internal static string Fingerprint => Certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static X509Certificate2 Load()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDesk");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "host-certificate.dpapi");
        if (!File.Exists(path))
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=OpenDesk", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
            byte[] exported = generated.Export(X509ContentType.Pkcs12);
            try
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temporary, ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser));
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
                finally { File.Delete(temporary); }
            }
            finally { CryptographicOperations.ZeroMemory(exported); }
        }

        byte[] pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}
