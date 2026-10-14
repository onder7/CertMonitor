using System.Formats.Asn1;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertMonitor;

public class CertScanner
{
    private readonly ScanOptions _opt;
    public CertScanner(ScanOptions opt) => _opt = opt;

    public async Task<ScanResult> ScanAsync(Target t, CancellationToken ct)
    {
        var r = new ScanResult { Host = t.Host, Port = t.Port, Environment = t.Environment, Owner = t.Owner, ScannedAt = DateTime.Now };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(t.Host, t.Port, cts.Token);
            
            // IP adresini al
            if (tcp.Client.RemoteEndPoint is System.Net.IPEndPoint remoteEndPoint)
            {
                var ipAddress = remoteEndPoint.Address;
                
                // IPv4-mapped IPv6 adresini IPv4'e dönüştür (::ffff:10.1.0.69 -> 10.1.0.69)
                if (ipAddress.IsIPv4MappedToIPv6)
                {
                    ipAddress = ipAddress.MapToIPv4();
                }
                
                r.IpAddress = ipAddress.ToString();
            }

            // Doğrulama hatası olsa bile sertifikayı okuyabilmek için callback her zaman true döner
            var errors = SslPolicyErrors.None;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, e) => { errors = e; return true; });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = t.SniName }, cts.Token);

            r.TlsVersion = ssl.SslProtocol.ToString();
            var cert = new X509Certificate2(ssl.RemoteCertificate!);
            Fill(r, cert, t.SniName, errors);
            r.SupportedProtocols = await ProbeProtocolsAsync(t, ct);
            if (r.SupportedProtocols.Contains("TLS 1.0") || r.SupportedProtocols.Contains("TLS 1.1"))
                r.Warnings.Add("Eski TLS (1.0/1.1) açık");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            r.Error = "Zaman aşımı";
        }
        catch (Exception ex)
        {
            r.Error = ex.InnerException?.Message ?? ex.Message;
        }

        r.Status = Classify(r);
        return r;
    }

    private void Fill(ScanResult r, X509Certificate2 cert, string sni, SslPolicyErrors errors)
    {
        r.Subject = cert.Subject;
        r.Issuer = cert.Issuer;
        r.Thumbprint = cert.Thumbprint;
        r.NotBefore = cert.NotBefore;
        r.NotAfter = cert.NotAfter;
        r.DaysLeft = (int)Math.Floor((cert.NotAfter - DateTime.Now).TotalDays);
        r.SelfSigned = cert.Subject == cert.Issuer;
        r.SignatureAlgorithm = cert.SignatureAlgorithm.FriendlyName;
        r.San = GetSans(cert);
        r.HostnameMatch = HostMatches(sni, r.San, cert);

        if (cert.GetRSAPublicKey() is { } rsa)
        {
            r.KeyInfo = $"RSA {rsa.KeySize}";
            if (rsa.KeySize < 2048) r.Warnings.Add($"Zayıf anahtar: RSA {rsa.KeySize}");
        }
        else if (cert.GetECDsaPublicKey() is { } ec) r.KeyInfo = $"ECDSA {ec.KeySize}";

        if (r.SignatureAlgorithm?.Contains("sha1", StringComparison.OrdinalIgnoreCase) == true)
            r.Warnings.Add("SHA-1 imza algoritması");
        if (r.SelfSigned) r.Warnings.Add("Self-signed sertifika");
        if (r.HostnameMatch == false) r.Warnings.Add("Hostname sertifikayla uyuşmuyor");

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(cert);
        foreach (var s in chain.ChainStatus)
            if (s.Status != X509ChainStatusFlags.NoError)
                r.ChainErrors.Add(s.Status.ToString());
    }

    private static string Classify(ScanResult r)
    {
        if (r.Error != null) return "Unreachable";
        if (r.DaysLeft < 0) return "Expired";
        bool weak = r.Warnings.Any(w => w.StartsWith("Zayıf") || w.StartsWith("SHA-1"));
        if (r.DaysLeft <= 7 || r.HostnameMatch == false || weak) return "Critical";
        if (r.ChainErrors.Any(e => e != "NotTimeValid")) return "ChainError";
        if (r.DaysLeft <= 30 || r.Warnings.Count > 0) return "Warning";
        return "OK";
    }

    private static async Task<List<string>> ProbeProtocolsAsync(Target t, CancellationToken ct)
    {
#pragma warning disable SYSLIB0039
        var map = new (string Name, SslProtocols P)[]
        {
            ("TLS 1.0", SslProtocols.Tls), ("TLS 1.1", SslProtocols.Tls11),
            ("TLS 1.2", SslProtocols.Tls12), ("TLS 1.3", SslProtocols.Tls13)
        };
#pragma warning restore SYSLIB0039
        var supported = new List<string>();
        foreach (var (name, p) in map)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(t.Host, t.Port, cts.Token);
                using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = t.SniName,
                    EnabledSslProtocols = p
                }, cts.Token);
                supported.Add(name);
            }
            catch { /* bu protokol kapalı veya istemci desteklemiyor */ }
        }
        return supported;
    }

    private static List<string> GetSans(X509Certificate2 c)
    {
        var list = new List<string>();
        var ext = c.Extensions["2.5.29.17"];
        if (ext == null) return list;
        try
        {
            var seq = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence();
            while (seq.HasData)
            {
                var tag = seq.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 2)
                    list.Add(seq.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
                else
                    seq.ReadEncodedValue();
            }
        }
        catch { }
        return list;
    }

    public static bool HostMatches(string host, List<string> san, X509Certificate2 cert)
    {
        var names = san.Count > 0 ? san : new List<string> { cert.GetNameInfo(X509NameType.DnsName, false) ?? "" };
        return names.Any(n => NameMatch(host, n));
    }

    public static bool NameMatch(string host, string pattern)
    {
        if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase)) return true;
        if (!pattern.StartsWith("*.")) return false;
        var dot = host.IndexOf('.');
        return dot > 0 && string.Equals(host[(dot + 1)..], pattern[2..], StringComparison.OrdinalIgnoreCase);
    }
}
