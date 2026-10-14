using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CertMonitor;

public class ScanStore
{
    private readonly ConcurrentDictionary<string, ScanResult> _data = new();
    private readonly string _file;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public ScanStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data"); Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "results.json");
        try { if (File.Exists(_file)) foreach (var r in JsonSerializer.Deserialize<List<ScanResult>>(File.ReadAllText(_file)) ?? new()) _data[$"{r.Host}:{r.Port}"] = r; } catch { }
    }
    public ScanResult? Get(string key) => _data.TryGetValue(key, out var r) ? r : null;
    public void Set(ScanResult r) => _data[$"{r.Host}:{r.Port}"] = r;
    public void Prune(HashSet<string> keep) { foreach (var k in _data.Keys.Where(k => !keep.Contains(k))) _data.TryRemove(k, out _); }
    public List<ScanResult> All() => _data.Values.OrderBy(r => r.DaysLeft ?? int.MinValue).ToList();
    public void Save() { try { File.WriteAllText(_file, JsonSerializer.Serialize(All(), Json)); } catch { } }
}

public class ScanService : BackgroundService
{
    private readonly ScanOptions _opt; private readonly ScanStore _store; private readonly IWebHostEnvironment _env; private readonly ILogger<ScanService> _log;
    private readonly EmailService _emailService;
    private readonly SemaphoreSlim _running = new(1, 1); private readonly object _targetsGate = new();
    public bool IsScanning { get; private set; }
    public ScanService(IOptions<ScanOptions> opt, ScanStore store, IWebHostEnvironment env, ILogger<ScanService> log, EmailService emailService) 
    { 
        _opt = opt.Value; 
        _store = store; 
        _env = env; 
        _log = log; 
        _emailService = emailService;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        while (!ct.IsCancellationRequested) { await ScanAllAsync(ct); await Task.Delay(TimeSpan.FromHours(Math.Max(1, _opt.IntervalHours)), ct); }
    }

    private string TargetsPath => Path.IsPathRooted(_opt.TargetsFile) ? _opt.TargetsFile : Path.Combine(_env.ContentRootPath, _opt.TargetsFile);

    public List<Target> LoadTargets()
    {
        lock (_targetsGate)
        {
            var list = new List<Target>();
            if (!File.Exists(TargetsPath)) { _log.LogWarning("Hedef dosyası bulunamadı: {p}", TargetsPath); return list; }
            foreach (var line in File.ReadLines(TargetsPath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
                var c = ParseCsvLine(line); if (c.Count == 0 || string.IsNullOrWhiteSpace(c[0])) continue;
                if (c.Count > 5 && bool.TryParse(c[5], out var en) && !en) continue;
                var port = c.Count > 1 && int.TryParse(c[1], out var p) && p is >= 1 and <= 65535 ? p : 443;
                list.Add(new Target(c[0], port, c.Count > 2 && c[2] != "" ? c[2] : c[0], c.Count > 3 ? c[3] : "", c.Count > 4 ? c[4] : ""));
            }
            return list;
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if ((ch == ',' || ch == ';') && !quoted) { fields.Add(field.ToString().Trim()); field.Clear(); }
            else field.Append(ch);
        }
        fields.Add(field.ToString().Trim());
        return fields;
    }

    public void SaveTargets(IEnumerable<Target> targets)
    {
        lock (_targetsGate)
        {
            var list = targets.ToList();
            if (list.Any(t => string.IsNullOrWhiteSpace(t.Host) || t.Port is < 1 or > 65535)) throw new InvalidDataException("Hedef host veya port alanı geçersiz.");
            if (list.GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new InvalidDataException("Aynı host ve port birden fazla kez tanımlanamaz.");
            var sb = new StringBuilder("Host,Port,SniName,Environment,Owner,Enabled\r\n");
            foreach (var t in list) sb.AppendLine(string.Join(',', new[] { t.Host.Trim(), t.Port.ToString(), t.SniName.Trim(), t.Environment.Trim(), t.Owner.Trim(), "true" }.Select(EscapeCsv)));
            var temp = TargetsPath + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            try
            {
                if (File.Exists(TargetsPath)) File.Replace(temp, TargetsPath, TargetsPath + ".bak");
                else File.Move(temp, TargetsPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    private static string EscapeCsv(string value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";

    public async Task ScanAllAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct)) return; IsScanning = true;
        try
        {
            var targets = LoadTargets(); _log.LogInformation("{n} hedef taranıyor", targets.Count); var scanner = new CertScanner(_opt); using var gate = new SemaphoreSlim(Math.Max(1, _opt.MaxParallel));
            await Task.WhenAll(targets.Select(async t => { await gate.WaitAsync(ct); try { var res = await scanner.ScanAsync(t, ct); var old = _store.Get(t.Key); if (old?.Thumbprint != null && res.Thumbprint != null && old.Thumbprint != res.Thumbprint) res.Renewed = true; _store.Set(res); } finally { gate.Release(); } }));
            _store.Prune(targets.Select(t => t.Key).ToHashSet()); _store.Save();
            
            // E-posta bildirimlerini gönder
            await _emailService.SendExpiryNotificationsAsync(_store.All());
        }
        finally { IsScanning = false; _running.Release(); }
    }
}
