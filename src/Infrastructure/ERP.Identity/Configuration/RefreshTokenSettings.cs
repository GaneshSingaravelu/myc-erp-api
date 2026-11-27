namespace ERP.Identity.Configuration;

/// <summary>
/// R2-DEV-03A — Refresh-token storage and reuse-detection settings.
///
/// KEY MANAGEMENT
/// ==============
/// <see cref="HmacKey"/> keys the HMAC-SHA-256 digest under which refresh tokens are stored
/// (current token on the session, consumed tokens in history). The plaintext refresh token
/// is never persisted.
///
/// - Provisioned exactly like <c>JwtSettings:SecretKey</c>: <c>appsettings.json</c> holds only
///   a placeholder; the real value comes from an environment variable
///   (<c>RefreshTokenSettings__HmacKey</c>), user-secrets, or a secret store.
/// - Never generated at runtime. A missing, placeholder or short key FAILS STARTUP, so the
///   digest is stable across restarts and every instance must be given the same key.
/// - Rotation: changing the key makes every stored digest unmatchable, so all sessions must
///   log in again (same effect as the 03A cutover). Procedure: provision the new key on all
///   instances, then restart them together. There is no dual-key verification.
/// </summary>
public class RefreshTokenSettings
{
    public const string SectionName = "RefreshTokenSettings";

    /// <summary>Minimum key length in characters (UTF-8 bytes) — 256 bits, as for the JWT key.</summary>
    public const int MinimumKeyLength = 32;

    public string HmacKey { get; set; } = string.Empty;

    /// <summary>
    /// Ambiguity window after a consumption in which presenting the same (consumed) token pair
    /// again is treated as a duplicate (rejected, nothing revoked). After the window it is
    /// treated as reuse and revokes that session family. Owner-approved default: 10 seconds.
    /// </summary>
    public int DuplicateWindowSeconds { get; set; } = 10;

    /// <summary>Throws when the settings cannot be used safely. Called at startup (fail closed).</summary>
    public static void Validate(RefreshTokenSettings? settings)
    {
        if (settings is null)
            throw new InvalidOperationException($"{SectionName} section is missing from configuration.");

        if (string.IsNullOrWhiteSpace(settings.HmacKey)
            || System.Text.Encoding.UTF8.GetByteCount(settings.HmacKey) < MinimumKeyLength
            || settings.HmacKey.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{SectionName}:HmacKey is missing, a placeholder, or too short. " +
                $"It must be at least {MinimumKeyLength} characters, identical on every instance. " +
                $"Set it via environment variable ({SectionName}__HmacKey), user-secrets or a secret store.");
        }

        if (settings.DuplicateWindowSeconds is < 1 or > 300)
            throw new InvalidOperationException($"{SectionName}:DuplicateWindowSeconds must be between 1 and 300.");
    }
}
