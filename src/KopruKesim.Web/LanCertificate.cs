using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Ag modu icin kendinden imzali https sertifikasi. Bilgisayarin yerel IP adreslerini kapsar; veri/
/// klasorunde saklanir ve IP adresleri degismedikce yeniden uretilmez (telefon uyariyi bir kez onaylar).
/// Sertifika yalnizca bu bilgisayarda uretilir ve hicbir yere gonderilmez.
/// </summary>
static class LanCertificate
{
    public static (X509Certificate2 cert, string[] ips) LoadOrCreate(string dir)
    {
        string[] ips = LocalIPv4();
        string pfx = Path.Combine(dir, "ag-sertifika.pfx");
        string ipFile = Path.Combine(dir, "ag-sertifika-ip.txt");
        string ipKey = string.Join(",", ips);
        if (File.Exists(pfx) && File.Exists(ipFile) && File.ReadAllText(ipFile).Trim() == ipKey)
        {
            var existing = new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.Exportable);
            if (existing.NotAfter > DateTime.Now.AddDays(30)) return (existing, ips);
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=KopruKesim yerel", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        foreach (string ip in ips) san.AddIpAddress(IPAddress.Parse(ip));
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using X509Certificate2 created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));
        File.WriteAllBytes(pfx, created.Export(X509ContentType.Pfx));
        File.WriteAllText(ipFile, ipKey);
        return (new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.Exportable), ips);
    }

    /// <summary>Calisan, sanal olmayan ag kartlarinin IPv4 adresleri (Wi-Fi, Ethernet).</summary>
    private static string[] LocalIPv4()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                     && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                     && !n.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                     && !n.Name.Contains("WSL", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                     && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address.ToString())
            .Distinct()
            .OrderBy(s => s)
            .ToArray();
    }
}
