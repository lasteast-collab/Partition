using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lasteast.Core.Licensing;

public enum Edition { Free, Pro, Technician }

public sealed record License(Edition Edition, string Licensee, string Id, DateTimeOffset Issued, DateTimeOffset? Expires);

public enum LicenseStatus { None, Valid, Expired, Invalid }

/// <summary>
/// Signed license files: {"license":{...},"signature":"&lt;base64 ECDSA P-256 / SHA-256&gt;"}. The signature covers the
/// UTF-8 bytes of <see cref="Canonical"/>. Only the public key ships with the app (see <see cref="LicenseKeys"/>).
///
/// What an edition changes — and what it never changes — is listed in docs/LICENSING.md. Safety, repair, recovery,
/// backup and health features are available in every edition, including Free, permanently.
/// </summary>
public static class Licenses
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Canonical(License l) =>
        $"{{\"edition\":\"{l.Edition}\",\"licensee\":{JsonSerializer.Serialize(l.Licensee)},\"id\":{JsonSerializer.Serialize(l.Id)}," +
        $"\"issued\":\"{l.Issued.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\",\"expires\":{(l.Expires is { } e ? $"\"{e.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\"" : "null")}}}";

    public static string Sign(License l, ECDsa privateKey)
    {
        var sig = privateKey.SignData(Encoding.UTF8.GetBytes(Canonical(l)), HashAlgorithmName.SHA256);
        return JsonSerializer.Serialize(new { license = l, signature = Convert.ToBase64String(sig) }, new JsonSerializerOptions(Options) { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public static (LicenseStatus Status, License? License) Verify(string json, IEnumerable<string> publicKeysSpki, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var l = root.GetProperty("license").Deserialize<License>(new JsonSerializerOptions(Options) { PropertyNameCaseInsensitive = true });
            var sig = Convert.FromBase64String(root.GetProperty("signature").GetString() ?? "");
            if (l is null || string.IsNullOrWhiteSpace(l.Licensee)) return (LicenseStatus.Invalid, null);
            var data = Encoding.UTF8.GetBytes(Canonical(l));
            foreach (var k in publicKeysSpki)
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _);
                if (!ec.VerifyData(data, sig, HashAlgorithmName.SHA256)) continue;
                return l.Expires is { } e && e < now ? (LicenseStatus.Expired, l) : (LicenseStatus.Valid, l);
            }
            return (LicenseStatus.Invalid, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or CryptographicException or InvalidOperationException)
        {
            return (LicenseStatus.Invalid, null);
        }
    }

    /// <summary>The edition in effect: Free unless a valid, unexpired license is installed.</summary>
    public static Edition Effective(LicenseStatus status, License? l) => status == LicenseStatus.Valid && l is not null ? l.Edition : Edition.Free;

    /// <summary>Report branding (technician and customer names on reports) is the only feature tied to an edition.</summary>
    public static bool AllowsReportBranding(Edition e) => e == Edition.Technician;
}

/// <summary>
/// Public keys trusted for license signatures. The "development" key below is for tests and self-built copies; its private
/// half is published in the test suite, so it proves nothing. Official builds replace it with the publisher's key
/// (tools/LicenseIssuer new-key prints the value to paste here; keep the private key out of the repository).
/// </summary>
public static class LicenseKeys
{
    public const string Development = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/B4jp/kkxcHbhdaxjhXme6FTj4gr3cGVH7kA2HQ0YnRHvvm80nZzmOurx98NEi3i/K1qllA2I5YJ7/B+oWIG+g==";
    public static IReadOnlyList<string> Trusted { get; } = new[] { Development };
}

/// <summary>Loads the installed license (AppPaths.License).</summary>
public sealed class LicenseStore
{
    private readonly string _path;
    public LicenseStore(string? path = null) => _path = path ?? AppPaths.License;

    public (LicenseStatus Status, License? License) Load()
    {
        if (!File.Exists(_path)) return (LicenseStatus.None, null);
        try { return Licenses.Verify(File.ReadAllText(_path), LicenseKeys.Trusted, DateTimeOffset.Now); }
        catch (IOException) { return (LicenseStatus.Invalid, null); }
    }

    /// <summary>Verifies and installs a license file; returns its status (only valid ones are installed).</summary>
    public (LicenseStatus Status, License? License) Install(string sourceFile)
    {
        var json = File.ReadAllText(sourceFile);
        var r = Licenses.Verify(json, LicenseKeys.Trusted, DateTimeOffset.Now);
        if (r.Status == LicenseStatus.Valid)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, json);
        }
        return r;
    }
}
