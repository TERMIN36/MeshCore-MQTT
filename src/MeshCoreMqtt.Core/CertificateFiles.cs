using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeshCoreMqtt.Core;

public static class CertificateFiles
{
    public static void Ensure(string directory, string host)
    {
        host = NormalizeHost(host);
        Directory.CreateDirectory(directory);
        var caCertPath = Path.Combine(directory, "ca.crt");
        var serverCertPath = Path.Combine(directory, "server.crt");
        var serverKeyPath = Path.Combine(directory, "server.key");
        if (Ready(caCertPath, serverCertPath, serverKeyPath))
        {
            RememberHost(directory, host);
            return;
        }

        WithLock(directory, () =>
        {
            if (Ready(caCertPath, serverCertPath, serverKeyPath))
            {
                RememberHost(directory, host);
                return;
            }

            Create(directory, [host], caCertPath, serverCertPath, serverKeyPath);
        });
    }

    public static string Reissue(string directory, string? hostsText)
    {
        var hosts = NormalizeHosts(hostsText);
        Directory.CreateDirectory(directory);
        var caCertPath = Path.Combine(directory, "ca.crt");
        var caKeyPath = Path.Combine(directory, "ca.key");
        var serverCertPath = Path.Combine(directory, "server.crt");
        var serverKeyPath = Path.Combine(directory, "server.key");
        WithLock(directory, () =>
        {
            if (!File.Exists(caCertPath) || !File.Exists(caKeyPath))
                Create(directory, hosts, caCertPath, serverCertPath, serverKeyPath);
            else
                ReplaceServer(directory, hosts, caCertPath, caKeyPath, serverCertPath, serverKeyPath);
        });
        return hosts[0];
    }

    public static string FirstCertificate(string? pem)
    {
        var text = (pem ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
            throw new CertificateException("Сертификат ещё не выпущен");
        var stop = text.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
        if (stop < 0)
            throw new CertificateException("Сертификат ещё не выпущен");
        return text[start..(stop + end.Length)];
    }

    public static string? ReadHost(string directory)
    {
        var path = Path.Combine(directory, "public.host");
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path).Trim();
        return text.Length == 0 ? null : text;
    }

    public static CertificateInfo Describe(string directory)
    {
        var path = Path.Combine(directory, "server.crt");
        if (!File.Exists(path))
            throw new CertificateException("Сертификат ещё не выпущен");
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension alternative)
                continue;
            foreach (var name in alternative.EnumerateDnsNames())
            {
                if (seen.Add(name))
                    names.Add(name);
            }

            foreach (var ip in alternative.EnumerateIPAddresses())
            {
                var name = ip.ToString();
                if (seen.Add(name))
                    names.Add(name);
            }
        }

        var host = ReadHost(directory) ?? certificate.GetNameInfo(X509NameType.SimpleName, false);
        return new CertificateInfo(host, new DateTimeOffset(certificate.NotAfter), names);
    }

    public static string NormalizeHost(string? host)
    {
        var value = (host ?? "").Trim();
        if (value.Length == 0 || value.Length > 253)
            throw new CertificateException("Нужно доменное имя или IP-адрес");
        if (IPAddress.TryParse(value, out var ip))
        {
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                throw new CertificateException("Нужно конкретное доменное имя или IP-адрес");
            return ip.ToString();
        }

        value = value.TrimEnd('.').ToLowerInvariant();
        var labels = value.Split('.');
        if (labels.Length == 0 || value.Length > 253 || labels.Any(label => !DnsLabel(label)))
            throw new CertificateException("Нужно доменное имя или IP-адрес");
        return value;
    }

    public static IReadOnlyList<string> NormalizeHosts(string? text)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new CertificateException("Нужно доменное имя или IP-адрес");
        if (parts.Length > 32)
            throw new CertificateException("В сертификат можно записать не больше 32 имён");
        var names = new List<string>(parts.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            var normalized = NormalizeHost(part);
            if (seen.Add(normalized))
                names.Add(normalized);
        }

        return names;
    }

    public static IReadOnlyList<string> ConfiguredHosts(string directory)
    {
        var primary = ReadHost(directory);
        if (primary is null)
            return [];
        var hosts = new List<string> { primary };
        var path = Path.Combine(directory, "public.names");
        if (!File.Exists(path))
            return hosts;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary };
        foreach (var line in File.ReadAllLines(path))
        {
            var name = line.Trim();
            if (name.Length > 0 && seen.Add(name))
                hosts.Add(name);
        }

        return hosts;
    }

    static bool DnsLabel(string label) =>
        label.Length is > 0 and <= 63
        && !label.StartsWith('-')
        && !label.EndsWith('-')
        && label.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    static void RememberHost(string directory, string fallback)
    {
        if (ReadHost(directory) is not null)
            return;
        var host = fallback;
        var certificatePath = Path.Combine(directory, "server.crt");
        if (File.Exists(certificatePath))
        {
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
            var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.IsNullOrWhiteSpace(name))
                host = name;
        }

        WriteHost(directory, host);
    }

    static void WithLock(string directory, Action action)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var lockStream = new FileStream(
                    Path.Combine(directory, "generate.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                action();
                return;
            }
            catch (IOException) when (attempt < 49)
            {
                Thread.Sleep(200);
            }
        }

        throw new IOException("Не удалось занять файл выпуска сертификата");
    }

    static bool Ready(string caCertPath, string serverCertPath, string serverKeyPath) =>
        File.Exists(caCertPath) && File.Exists(serverCertPath) && File.Exists(serverKeyPath);

    static void Create(string directory, IReadOnlyList<string> hosts, string caCertPath, string serverCertPath, string serverKeyPath)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=MeshCore MQTT CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        var caCert = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        using var serverKey = RSA.Create(2048);
        var serverCert = IssueServer(hosts, caCert, serverKey);
        WritePem(caCertPath, "CERTIFICATE", caCert.RawData);
        WritePem(Path.Combine(directory, "ca.key"), "PRIVATE KEY", caKey.ExportPkcs8PrivateKey());
        WritePem(serverCertPath, "CERTIFICATE", serverCert.RawData);
        WritePem(serverKeyPath, "PRIVATE KEY", serverKey.ExportPkcs8PrivateKey());
        WriteHosts(directory, hosts);
    }

    static void ReplaceServer(string directory, IReadOnlyList<string> hosts, string caCertPath, string caKeyPath, string serverCertPath, string serverKeyPath)
    {
        using var loaded = X509Certificate2.CreateFromPemFile(caCertPath, caKeyPath);
        using var caCert = new X509Certificate2(loaded.Export(X509ContentType.Pkcs12));
        using var serverKey = RSA.Create(2048);
        var serverCert = IssueServer(hosts, caCert, serverKey);
        WritePem(serverCertPath, "CERTIFICATE", serverCert.RawData);
        WritePem(serverKeyPath, "PRIVATE KEY", serverKey.ExportPkcs8PrivateKey());
        WriteHosts(directory, hosts);
    }

    public static string ExportServerKey(string directory)
    {
        var path = Path.Combine(directory, "server.key");
        if (!File.Exists(path))
            throw new CertificateException("Приватный ключ ещё не выпущен");
        var pem = File.ReadAllText(path).Trim();
        using var key = ReadServerKey(pem);
        return pem + "\n";
    }

    public static void ImportServerKey(string directory, string? pem)
    {
        using var serverKey = ReadServerKey(pem);
        Directory.CreateDirectory(directory);
        var caCertPath = Path.Combine(directory, "ca.crt");
        var caKeyPath = Path.Combine(directory, "ca.key");
        var serverCertPath = Path.Combine(directory, "server.crt");
        var serverKeyPath = Path.Combine(directory, "server.key");
        if (!File.Exists(caCertPath) || !File.Exists(caKeyPath))
            throw new CertificateException("Сертификат ещё не выпущен");
        var hosts = ConfiguredHosts(directory);
        if (hosts.Count == 0)
            hosts = [NormalizeHost(Describe(directory).Host)];
        WithLock(directory, () =>
        {
            using var loaded = X509Certificate2.CreateFromPemFile(caCertPath, caKeyPath);
            using var caCert = new X509Certificate2(loaded.Export(X509ContentType.Pkcs12));
            var serverCert = IssueServer(hosts, caCert, serverKey);
            WritePem(serverCertPath, "CERTIFICATE", serverCert.RawData);
            WritePem(serverKeyPath, "PRIVATE KEY", serverKey.ExportPkcs8PrivateKey());
            WriteHosts(directory, hosts);
        });
    }

    static RSA ReadServerKey(string? pem)
    {
        var text = (pem ?? "").Trim();
        if (text.Length == 0 || text.Length > 16_384 || !text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new CertificateException("Нужен PEM приватного ключа");
        if (text.Contains("ENCRYPTED", StringComparison.Ordinal))
            throw new CertificateException("Ключ не должен быть зашифрован");
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(text);
            if (key.KeySize < 2048)
                throw new CertificateException("Ключ должен быть не короче 2048 бит");
            return key;
        }
        catch (Exception ex) when (ex is not CertificateException)
        {
            key.Dispose();
            throw new CertificateException("Не удалось прочитать приватный ключ");
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    static X509Certificate2 IssueServer(IReadOnlyList<string> hosts, X509Certificate2 caCert, RSA serverKey)
    {
        var serverRequest = new CertificateRequest(Subject(hosts[0]), serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        var dns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ips = new HashSet<IPAddress>();
        foreach (var host in hosts)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                if (ips.Add(ip))
                    san.AddIpAddress(ip);
                // Репитер сверяет адрес как DNS-имя, даже когда в настройках указан IP.
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    AddDns(san, dns, host);
            }
            else
                AddDns(san, dns, host);
        }

        AddDns(san, dns, "localhost");
        if (ips.Add(IPAddress.Loopback))
            san.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        return serverRequest.Create(
            caCert,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(2),
            RandomNumberGenerator.GetBytes(16));
    }

    static void AddDns(SubjectAlternativeNameBuilder san, HashSet<string> dns, string name)
    {
        if (dns.Add(name))
            san.AddDnsName(name);
    }

    static string Subject(string host)
    {
        var escaped = host
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("+", "\\+", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace("<", "\\<", StringComparison.Ordinal)
            .Replace(">", "\\>", StringComparison.Ordinal);
        return $"CN={escaped}";
    }

    static void WriteHost(string directory, string host)
    {
        var path = Path.Combine(directory, "public.host");
        var temp = path + ".tmp";
        File.WriteAllText(temp, host + "\n");
        File.Move(temp, path, true);
    }

    static void WriteHosts(string directory, IReadOnlyList<string> hosts)
    {
        WriteHost(directory, hosts[0]);
        var path = Path.Combine(directory, "public.names");
        if (hosts.Count == 1)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, string.Join('\n', hosts.Skip(1)) + "\n");
        File.Move(temp, path, true);
    }

    public static X509Certificate2 LoadServerCertificate(string directory)
    {
        var identity = LoadServerIdentity(directory);
        foreach (var extra in identity.Extras)
            extra.Dispose();
        return identity.Leaf;
    }

    public static ServerIdentity LoadServerIdentity(string directory)
    {
        var serverPem = File.ReadAllText(Path.Combine(directory, "server.crt"));
        var keyPem = File.ReadAllText(Path.Combine(directory, "server.key"));
        using var key = RSA.Create();
        try
        {
            key.ImportFromPem(keyPem);
        }
        catch (Exception ex) when (ex is not CertificateException)
        {
            throw new CertificateException("Не удалось прочитать приватный ключ");
        }

        var keyInfo = key.ExportSubjectPublicKeyInfo();
        var all = new X509Certificate2Collection();
        try
        {
            all.ImportFromPem(serverPem);
        }
        catch (Exception ex) when (ex is not CertificateException)
        {
            throw new CertificateException("Сертификат ещё не выпущен");
        }

        var caPath = Path.Combine(directory, "ca.crt");
        if (File.Exists(caPath))
        {
            var caPem = File.ReadAllText(caPath);
            if (caPem.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            {
                try
                {
                    all.ImportFromPem(caPem);
                }
                catch (Exception ex) when (ex is not CertificateException)
                {
                    throw new CertificateException("Сертификат ещё не выпущен");
                }
            }
        }

        X509Certificate2? leaf = null;
        foreach (var cert in all)
        {
            using var publicKey = cert.GetRSAPublicKey();
            if (publicKey is not null && publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(keyInfo))
            {
                leaf = cert;
                break;
            }
        }

        if (leaf is null)
            throw new CertificateException("Сертификат не соответствует приватному ключу");

        using var withKey = leaf.CopyWithPrivateKey(key);
        var leafWithKey = new X509Certificate2(withKey.Export(X509ContentType.Pkcs12));
        var leafRaw = Convert.ToHexString(leaf.RawData);
        var extras = new X509Certificate2Collection();
        var seen = new HashSet<string>(StringComparer.Ordinal) { leafRaw };
        foreach (var cert in all)
        {
            var raw = Convert.ToHexString(cert.RawData);
            if (!seen.Add(raw))
                continue;
            extras.Add(cert);
        }

        return new ServerIdentity(leafWithKey, extras);
    }

    static void WritePem(string path, string label, byte[] data)
    {
        var pem = new string(PemEncoding.Write(label, data));
        var temp = path + ".tmp";
        File.WriteAllText(temp, pem + "\n");
        File.Move(temp, path, true);
    }
}

public sealed record ServerIdentity(X509Certificate2 Leaf, X509Certificate2Collection Extras);

public sealed record CertificateInfo(string Host, DateTimeOffset NotAfter, IReadOnlyList<string> Names);

public sealed class CertificateException(string message) : Exception(message);
