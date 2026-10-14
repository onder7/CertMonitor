using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CertMonitor;

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "CertMonitor";
    public string ToAddresses { get; set; } = "";
    public bool NotifyOnWarning { get; set; } = true;
    public bool NotifyOnCritical { get; set; } = true;
    public bool NotifyOnExpired { get; set; } = true;
    public int NotifyDaysBefore { get; set; } = 30;
}

public sealed class EmailService
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailService> _logger;
    private readonly string _configFile;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public EmailService(IOptions<EmailOptions> options, ILogger<EmailService> logger, IWebHostEnvironment env)
    {
        _options = options.Value;
        _logger = logger;
        _configFile = Path.Combine(env.ContentRootPath, "App_Data", "email-config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_configFile)!);
        LoadConfig();
    }

    private void LoadConfig()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_configFile))
                {
                    var json = File.ReadAllText(_configFile);
                    var saved = JsonSerializer.Deserialize<EmailOptions>(json);
                    if (saved != null)
                    {
                        _options.Enabled = saved.Enabled;
                        _options.SmtpHost = saved.SmtpHost;
                        _options.SmtpPort = saved.SmtpPort;
                        _options.UseSsl = saved.UseSsl;
                        _options.Username = saved.Username;
                        _options.Password = saved.Password;
                        _options.FromAddress = saved.FromAddress;
                        _options.FromName = saved.FromName;
                        _options.ToAddresses = saved.ToAddresses;
                        _options.NotifyOnWarning = saved.NotifyOnWarning;
                        _options.NotifyOnCritical = saved.NotifyOnCritical;
                        _options.NotifyOnExpired = saved.NotifyOnExpired;
                        _options.NotifyDaysBefore = saved.NotifyDaysBefore;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "E-posta ayarları yüklenemedi");
            }
        }
    }

    public void SaveConfig(EmailOptions options)
    {
        lock (_gate)
        {
            try
            {
                // Mevcut ayarları güncelle
                _options.Enabled = options.Enabled;
                _options.SmtpHost = options.SmtpHost;
                _options.SmtpPort = options.SmtpPort;
                _options.UseSsl = options.UseSsl;
                _options.Username = options.Username;
                if (!string.IsNullOrWhiteSpace(options.Password) && options.Password != "********")
                    _options.Password = options.Password;
                _options.FromAddress = options.FromAddress;
                _options.FromName = options.FromName;
                _options.ToAddresses = options.ToAddresses;
                _options.NotifyOnWarning = options.NotifyOnWarning;
                _options.NotifyOnCritical = options.NotifyOnCritical;
                _options.NotifyOnExpired = options.NotifyOnExpired;
                _options.NotifyDaysBefore = options.NotifyDaysBefore;

                // Dosyaya kaydet
                File.WriteAllText(_configFile, JsonSerializer.Serialize(_options, Json));
                _logger.LogInformation("E-posta ayarları kaydedildi");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "E-posta ayarları kaydedilemedi");
                throw;
            }
        }
    }

    public EmailOptions GetConfig() => _options;

    public async Task<(bool Success, string Message)> SendTestEmailAsync(string testToAddress)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.SmtpHost))
                return (false, "SMTP sunucu adresi belirtilmemiş.");

            if (string.IsNullOrWhiteSpace(_options.FromAddress))
                return (false, "Gönderen e-posta adresi belirtilmemiş.");

            if (string.IsNullOrWhiteSpace(testToAddress))
                return (false, "Alıcı e-posta adresi belirtilmemiş.");

            using var message = new MailMessage
            {
                From = new MailAddress(_options.FromAddress, _options.FromName),
                Subject = "CertMonitor Test E-postası",
                Body = $"Bu bir test e-postasıdır.\n\nGönderim zamanı: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nSMTP Sunucu: {_options.SmtpHost}:{_options.SmtpPort}",
                IsBodyHtml = false
            };

            message.To.Add(testToAddress);

            using var smtp = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
            {
                EnableSsl = _options.UseSsl,
                Credentials = string.IsNullOrWhiteSpace(_options.Username)
                    ? null
                    : new NetworkCredential(_options.Username, _options.Password)
            };

            await smtp.SendMailAsync(message);
            _logger.LogInformation("Test e-postası gönderildi: {to}", testToAddress);
            return (true, "Test e-postası başarıyla gönderildi.");
        }
        catch (SmtpException ex)
        {
            _logger.LogError(ex, "SMTP hatası: {msg}", ex.Message);
            return (false, $"SMTP hatası: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "E-posta gönderim hatası: {msg}", ex.Message);
            return (false, $"Hata: {ex.Message}");
        }
    }

    public async Task SendExpiryNotificationsAsync(List<ScanResult> results)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("E-posta bildirimleri devre dışı.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ToAddresses))
        {
            _logger.LogWarning("Alıcı e-posta adresi belirtilmemiş.");
            return;
        }

        var toNotify = results
            .Where(r => r.DaysLeft.HasValue && r.DaysLeft.Value <= _options.NotifyDaysBefore)
            .Where(r => (r.Status == "Expired" && _options.NotifyOnExpired) ||
                       (r.Status == "Critical" && _options.NotifyOnCritical) ||
                       (r.Status == "Warning" && _options.NotifyOnWarning))
            .OrderBy(r => r.DaysLeft)
            .ToList();

        if (!toNotify.Any())
        {
            _logger.LogDebug("Bildirilecek sertifika bulunamadı.");
            return;
        }

        try
        {
            var subject = $"SSL Sertifika Uyarısı - {toNotify.Count} sertifika dikkat gerektiriyor";
            var body = BuildEmailBody(toNotify);

            using var message = new MailMessage
            {
                From = new MailAddress(_options.FromAddress, _options.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };

            foreach (var addr in _options.ToAddresses.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = addr.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    message.To.Add(trimmed);
            }

            if (!message.To.Any())
            {
                _logger.LogWarning("Geçerli alıcı adresi bulunamadı.");
                return;
            }

            using var smtp = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
            {
                EnableSsl = _options.UseSsl,
                Credentials = string.IsNullOrWhiteSpace(_options.Username)
                    ? null
                    : new NetworkCredential(_options.Username, _options.Password)
            };

            await smtp.SendMailAsync(message);
            _logger.LogInformation("Süre sonu bildirimi gönderildi: {count} sertifika, {to}", toNotify.Count, string.Join(", ", message.To));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bildirim e-postası gönderilemedi: {msg}", ex.Message);
        }
    }

private string BuildEmailBody(List<ScanResult> results)
{
    var sb = new StringBuilder();
    sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><style>");
    sb.AppendLine("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;margin:0;padding:24px;background-color:#f3f4f6;color:#0e1d35}");
    sb.AppendLine(".container{max-width:800px;margin:0 auto;background:#ffffff;padding:32px;border-radius:16px;border:1px solid #e5e7eb;box-shadow:0 10px 25px -5px rgba(0,0,0,0.05)}");
    sb.AppendLine(".header-accent{width:40px;height:4px;background:#d9232e;border-radius:2px;margin-bottom:20px}");
    sb.AppendLine("h1{color:#0e1d35;font-size:22px;margin:0 0 8px;font-weight:700}");
    sb.AppendLine("p{color:#6b7280;margin:0 0 16px;font-size:14px;line-height:1.5}");
    sb.AppendLine("strong{color:#0e1d35}");
    sb.AppendLine("table{border-collapse:collapse;width:100%;margin-top:24px;font-size:13px}");
    sb.AppendLine("th,td{padding:12px 14px;text-align:left;border:1px solid #e5e7eb}");
    sb.AppendLine("th{background:#f8fafc;font-weight:600;color:#374151}");
    sb.AppendLine(".expired{background:#fef2f2;color:#b91c1c}");
    sb.AppendLine(".critical{background:#fffbeb;color:#b45309}");
    sb.AppendLine(".warning{background:#fefce8;color:#a16207}");
    sb.AppendLine(".footer{margin-top:32px;padding-top:20px;border-top:1px solid #e5e7eb;color:#6b7280;font-size:13px}");
    sb.AppendLine(".footer a{color:#0e1d35;font-weight:600;text-decoration:none}");
    sb.AppendLine(".footer a:hover{color:#d9232e;text-decoration:underline}");
    sb.AppendLine("</style></head><body>");

    sb.AppendLine("<div class='container'>");
    sb.AppendLine("<div class='header-accent'></div>");
    sb.AppendLine("<h1>SSL Sertifika Uyarısı</h1>");
    sb.AppendLine($"<p><strong>{results.Count} sertifika</strong> dikkat gerektiriyor.</p>");
    sb.AppendLine($"<p>Tarama zamanı: <strong>{DateTime.Now:yyyy-MM-dd HH:mm:ss}</strong></p>");

    sb.AppendLine("<table>");
    sb.AppendLine("<thead><tr><th>Host</th><th>Port</th><th>Ortam</th><th>Durum</th><th>Kalan Gün</th><th>Bitiş Tarihi</th><th>Sahip</th></tr></thead>");
    sb.AppendLine("<tbody>");

    foreach (var r in results)
    {
        var rowClass = r.Status switch
        {
            "Expired" => "expired",
            "Critical" => "critical",
            "Warning" => "warning",
            _ => ""
        };

        sb.AppendLine($"<tr class='{rowClass}'>");
        sb.AppendLine($"<td><strong>{r.Host}</strong></td>");
        sb.AppendLine($"<td>{r.Port}</td>");
        sb.AppendLine($"<td>{r.Environment}</td>");
        sb.AppendLine($"<td><strong>{r.Status}</strong></td>");
        sb.AppendLine($"<td><strong>{(r.DaysLeft.HasValue ? r.DaysLeft.Value.ToString() : "-")}</strong></td>");
        sb.AppendLine($"<td>{(r.NotAfter.HasValue ? r.NotAfter.Value.ToString("yyyy-MM-dd") : "-")}</td>");
        sb.AppendLine($"<td>{r.Owner}</td>");
        sb.AppendLine("</tr>");
    }

    sb.AppendLine("</tbody></table>");

    sb.AppendLine("<div class='footer'>");
    sb.AppendLine("<p>Bu e-posta CertMonitor tarafından otomatik olarak gönderilmiştir. Detaylar için dashboard'u kontrol edin.</p>");
    sb.AppendLine("</div>");

    sb.AppendLine("</div>");
    sb.AppendLine("</body></html>");
    return sb.ToString();
}
}
