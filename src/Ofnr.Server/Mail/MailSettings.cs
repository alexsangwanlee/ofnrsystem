using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Ofnr.Server.Mail;

/// <summary>A mail failure whose message is Korean and shown to the CEO as is.</summary>
public sealed class MailException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Passwords in <c>&lt;data&gt;/secrets/&lt;name&gt;.bin</c>: DPAPI (current user, no entropy) over UTF-8, the format scripts/setup.ps1 writes.
/// Other OS, for development only: <c>mail</c> comes from the OFNR_MAIL_PASSWORD environment variable and nothing is stored.
/// </summary>
public sealed class SecretStore(SettingsStore settings, IConfiguration config)
{
    string PathOf(string name) => Path.Combine(settings.Current.DataRoot, "secrets", name + ".bin");

    public void Save(string name, string value)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("비밀번호는 Windows에서만 저장할 수 있습니다. 개발 PC에서는 환경 변수 OFNR_MAIL_PASSWORD를 쓰세요.");
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public string? Load(string name)
    {
        if (!OperatingSystem.IsWindows()) return name == "mail" ? config["OFNR_MAIL_PASSWORD"] : null;
        var path = PathOf(name);
        if (!File.Exists(path)) return null;
        // Another Windows account or PC wrote it; the README asks to run setup.ps1 and enter the mail password again.
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>The connection string plus the <c>db</c> password setup.ps1 saved, when the string carries none (setup.ps1 keeps it out of settings.json).</summary>
    public string WithDbPassword(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(b.Password) || Load("db") is not { } password) return connectionString;
        b.Password = password;
        return b.ConnectionString;
    }
}
