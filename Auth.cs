using System.Security.Cryptography;
using System.Text.Json;

namespace CertMonitor;

public sealed class AuthOptions
{
    public string AdminUser { get; set; } = "admin";
    public string AdminPassword { get; set; } = "ChangeMe123!";
    public int SessionHours { get; set; } = 8;
}

public sealed class AppUser
{
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public sealed class AuthService
{
    private readonly string _file;
    private readonly AuthOptions _options;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AuthService(IWebHostEnvironment env, Microsoft.Extensions.Options.IOptions<AuthOptions> options)
    {
        _file = Path.Combine(env.ContentRootPath, "App_Data", "users.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        _options = options.Value;
        EnsureAdmin();
    }

    public bool Validate(string userName, string password)
    {
        var user = Load().FirstOrDefault(u => string.Equals(u.UserName, userName.Trim(), StringComparison.OrdinalIgnoreCase));
        return user != null && Verify(password, user.PasswordHash);
    }

    private void EnsureAdmin()
    {
        lock (_gate)
        {
            var users = Load();
            if (users.Count == 0)
            {
                users.Add(new AppUser { UserName = _options.AdminUser, PasswordHash = Hash(_options.AdminPassword) });
                File.WriteAllText(_file, JsonSerializer.Serialize(users, Json));
            }
        }
    }

    private List<AppUser> Load()
    {
        try { return File.Exists(_file) ? JsonSerializer.Deserialize<List<AppUser>>(File.ReadAllText(_file)) ?? new() : new(); }
        catch { return new(); }
    }

    private static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(key)}";
    }

    private static bool Verify(string password, string encoded)
    {
        try
        {
            var p = encoded.Split(':');
            if (p.Length != 3) return false;
            var salt = Convert.FromBase64String(p[1]);
            var expected = Convert.FromBase64String(p[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}

public record LoginRequest(string UserName, string Password);
public record TestEmailRequest(string TestAddress);
