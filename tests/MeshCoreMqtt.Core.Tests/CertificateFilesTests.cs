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
    public void NormalizeHost_rejects_empty_and_wildcard()
    {
        Assert.Throws<CertificateException>(() => CertificateFiles.NormalizeHost("  "));
        Assert.Throws<CertificateException>(() => CertificateFiles.NormalizeHost("*.example.com"));
        Assert.Equal("10.0.0.8", CertificateFiles.NormalizeHost("10.0.0.8"));
    }
}
