using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeshCoreMqtt.Core.Tests;

public class CertificateFilesTests
{
    [Fact]
    public void Reissue_keeps_ca_and_changes_server_name()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshcore-certs-" + Guid.NewGuid().ToString("n"));
        try
        {
            CertificateFiles.Ensure(directory, "localhost");
            var firstCa = File.ReadAllText(Path.Combine(directory, "ca.crt"));
            using var first = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directory, "server.crt")));

            CertificateFiles.Reissue(directory, "MQTT.Example.com");

            var secondCa = File.ReadAllText(Path.Combine(directory, "ca.crt"));
            using var second = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directory, "server.crt")));
            var info = CertificateFiles.Describe(directory);
            Assert.Equal(firstCa, secondCa);
            Assert.NotEqual(first.RawData, second.RawData);
            Assert.Equal("mqtt.example.com", info.Host);
            Assert.Contains("mqtt.example.com", info.Names);
            Assert.Contains("localhost", info.Names);
            Assert.Contains("127.0.0.1", info.Names);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Reissue_for_ip_keeps_ca_and_adds_the_address_as_dns_name()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshcore-certs-" + Guid.NewGuid().ToString("n"));
        try
        {
            CertificateFiles.Ensure(directory, "mqtt.example");
            var firstCa = File.ReadAllText(Path.Combine(directory, "ca.crt"));

            CertificateFiles.Reissue(directory, "10.18.2.107");

            Assert.Equal(firstCa, File.ReadAllText(Path.Combine(directory, "ca.crt")));
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directory, "server.crt")));
            var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
            Assert.Contains("10.18.2.107", san.EnumerateDnsNames());
            Assert.Contains(IPAddress.Parse("10.18.2.107"), san.EnumerateIPAddresses());
            Assert.Equal("10.18.2.107", CertificateFiles.Describe(directory).Host);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Reissue_puts_comma_separated_names_into_the_certificate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshcore-certs-" + Guid.NewGuid().ToString("n"));
        try
        {
            CertificateFiles.Ensure(directory, "localhost");
            var firstCa = File.ReadAllText(Path.Combine(directory, "ca.crt"));

            var primary = CertificateFiles.Reissue(directory, " MQTT.Example.com, 10.18.2.107, mqtt.example.com, lan.local ");

            Assert.Equal("mqtt.example.com", primary);
            Assert.Equal(firstCa, File.ReadAllText(Path.Combine(directory, "ca.crt")));
            Assert.Equal(["mqtt.example.com", "10.18.2.107", "lan.local"], CertificateFiles.ConfiguredHosts(directory));
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directory, "server.crt")));
            var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
            Assert.Contains("mqtt.example.com", san.EnumerateDnsNames());
            Assert.Contains("lan.local", san.EnumerateDnsNames());
            Assert.Contains("10.18.2.107", san.EnumerateDnsNames());
            Assert.Contains("localhost", san.EnumerateDnsNames());
            Assert.Contains(IPAddress.Parse("10.18.2.107"), san.EnumerateIPAddresses());
            Assert.Contains(IPAddress.Loopback, san.EnumerateIPAddresses());
            var info = CertificateFiles.Describe(directory);
            Assert.Equal("mqtt.example.com", info.Host);
            Assert.Equal(info.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), info.Names.Count);

            CertificateFiles.Reissue(directory, "mqtt.example.com");
            Assert.Equal(["mqtt.example.com"], CertificateFiles.ConfiguredHosts(directory));
            Assert.False(File.Exists(Path.Combine(directory, "public.names")));
            Assert.Throws<CertificateException>(() => CertificateFiles.NormalizeHosts("mqtt.example, *.bad"));
            Assert.Throws<CertificateException>(() => CertificateFiles.NormalizeHosts(" , "));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ImportServerKey_keeps_extra_names()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshcore-certs-" + Guid.NewGuid().ToString("n"));
        try
        {
            CertificateFiles.Ensure(directory, "mqtt.example");
            CertificateFiles.Reissue(directory, "mqtt.example, 10.1.2.3");
            using var imported = RSA.Create(2048);
            var pem = new string(PemEncoding.Write("PRIVATE KEY", imported.ExportPkcs8PrivateKey()));

            CertificateFiles.ImportServerKey(directory, pem);

            Assert.Equal(["mqtt.example", "10.1.2.3"], CertificateFiles.ConfiguredHosts(directory));
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directory, "server.crt")));
            var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
            Assert.Contains("mqtt.example", san.EnumerateDnsNames());
            Assert.Contains(IPAddress.Parse("10.1.2.3"), san.EnumerateIPAddresses());
            using var publicKey = certificate.GetRSAPublicKey();
            Assert.Equal(imported.ExportSubjectPublicKeyInfo(), publicKey!.ExportSubjectPublicKeyInfo());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ImportServerKey_replaces_key_and_keeps_ca()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshcore-certs-" + Guid.NewGuid().ToString("n"));
        try
        {
            CertificateFiles.Ensure(directory, "mqtt.example");
            var ca = File.ReadAllText(Path.Combine(directory, "ca.crt"));
            var exported = CertificateFiles.ExportServerKey(directory);
            using var imported = RSA.Create(2048);
            var pem = new string(PemEncoding.Write("PRIVATE KEY", imported.ExportPkcs8PrivateKey()));

            CertificateFiles.ImportServerKey(directory, pem);

            Assert.Equal(ca, File.ReadAllText(Path.Combine(directory, "ca.crt")));
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directory, "server.crt")));
            using var publicKey = certificate.GetRSAPublicKey();
            Assert.NotNull(publicKey);
            Assert.Equal(imported.ExportSubjectPublicKeyInfo(), publicKey.ExportSubjectPublicKeyInfo());
            using var loaded = CertificateFiles.LoadServerCertificate(directory);
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal("mqtt.example", CertificateFiles.Describe(directory).Host);
            Assert.NotEqual(exported, CertificateFiles.ExportServerKey(directory));
            Assert.Throws<CertificateException>(() => CertificateFiles.ImportServerKey(directory, "not a key"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FirstCertificate_keeps_only_the_first_in_the_chain()
    {
        const string first = """
            -----BEGIN CERTIFICATE-----
            AAAA
            -----END CERTIFICATE-----
            """;
        const string second = """
            -----BEGIN CERTIFICATE-----
            BBBB
            -----END CERTIFICATE-----
            """;
        var chain = first.Replace("\r\n", "\n") + "\n" + second.Replace("\r\n", "\n") + "\n";

        Assert.Equal(first.Replace("\r\n", "\n"), CertificateFiles.FirstCertificate(chain));
        Assert.Throws<CertificateException>(() => CertificateFiles.FirstCertificate("   "));
    }

    [Fact]
    public async Task Handshake_presents_the_leaf_and_the_issuer_chain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meshcore-certs-" + Guid.NewGuid().ToString("n"));
        using var rootKey = RSA.Create(2048);
        using var intermediateKey = RSA.Create(2048);
        using var leafKey = RSA.Create(2048);
        var root = IssueCa("CN=Test Root", rootKey, null);
        var intermediate = IssueCa("CN=Test Intermediate", intermediateKey, root);
        var leaf = IssueLeaf("mqtt.example", leafKey, intermediate);

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "server.crt"), Pem("CERTIFICATE", intermediate.RawData) + Pem("CERTIFICATE", leaf.RawData));
            File.WriteAllText(Path.Combine(directory, "server.key"), Pem("PRIVATE KEY", leafKey.ExportPkcs8PrivateKey()));
            File.WriteAllText(Path.Combine(directory, "ca.crt"), Pem("CERTIFICATE", root.RawData));

            var identity = CertificateFiles.LoadServerIdentity(directory);
            Assert.Equal(leaf.RawData, identity.Leaf.RawData);

            var context = SslStreamCertificateContext.Create(identity.Leaf, identity.Extras, offline: true);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(accepted.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = context,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                });
            });

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck
            };
            policy.CustomTrustStore.Add(root);
            await using var client = new SslStream(tcp.GetStream());
            await client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "mqtt.example",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateChainPolicy = policy
            });

            Assert.Equal(leaf.Thumbprint, client.RemoteCertificate?.GetCertHashString());
            await server;
            listener.Stop();
        }
        finally
        {
            root.Dispose();
            intermediate.Dispose();
            leaf.Dispose();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    static X509Certificate2 IssueCa(string subject, RSA key, X509Certificate2? issuer)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(5);
        if (issuer is null)
            return request.CreateSelfSigned(notBefore, notAfter);
        var issuerEnd = new DateTimeOffset(issuer.NotAfter);
        if (notAfter > issuerEnd)
            notAfter = issuerEnd;
        using var issued = request.Create(issuer, notBefore, notAfter, [1]);
        return issued.CopyWithPrivateKey(key);
    }

    static X509Certificate2 IssueLeaf(string host, RSA key, X509Certificate2 issuer)
    {
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), [2]);
    }

    static string Pem(string label, byte[] data) => new string(PemEncoding.Write(label, data)) + "\n";

    [Fact]
    public void NormalizeHost_rejects_empty_and_wildcard()
    {
        Assert.Throws<CertificateException>(() => CertificateFiles.NormalizeHost("  "));
        Assert.Throws<CertificateException>(() => CertificateFiles.NormalizeHost("*.example.com"));
        Assert.Equal("10.0.0.8", CertificateFiles.NormalizeHost("10.0.0.8"));
    }
}
