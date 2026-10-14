namespace CertMonitor;

public class ScanOptions
{
    public int IntervalHours { get; set; } = 6;
    public int MaxParallel { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 10;
    public string TargetsFile { get; set; } = "targets.csv";
    public int WarningDays { get; set; } = 30;
    public int CriticalDays { get; set; } = 7;
}

public record Target(string Host, int Port, string SniName, string Environment, string Owner, bool Enabled = true)
{
    public string Key => $"{Host}:{Port}";
}

public class ScanResult
{
    public string Host { get; set; } = "";
    public string? IpAddress { get; set; }
    public int Port { get; set; }
    public string Environment { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Status { get; set; } = "Unreachable";   // OK, Warning, Critical, Expired, ChainError, Unreachable
    public string? Error { get; set; }
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public string? Thumbprint { get; set; }
    public DateTime? NotBefore { get; set; }
    public DateTime? NotAfter { get; set; }
    public int? DaysLeft { get; set; }
    public List<string> San { get; set; } = new();
    public bool? HostnameMatch { get; set; }
    public string? SignatureAlgorithm { get; set; }
    public string? KeyInfo { get; set; }
    public bool SelfSigned { get; set; }
    public List<string> ChainErrors { get; set; } = new();
    public string? TlsVersion { get; set; }
    public List<string> SupportedProtocols { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool Renewed { get; set; }
    public DateTime ScannedAt { get; set; } = DateTime.Now;
}
