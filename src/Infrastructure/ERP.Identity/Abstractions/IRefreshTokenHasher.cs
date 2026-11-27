namespace ERP.Identity.Abstractions;

/// <summary>
/// R2-DEV-03A — Computes the keyed digest under which a refresh token is stored and looked up.
/// The plaintext refresh token is never persisted.
/// </summary>
public interface IRefreshTokenHasher
{
    /// <summary>Lowercase hex HMAC-SHA-256 of <paramref name="refreshToken"/> (64 characters).</summary>
    string Hash(string refreshToken);
}
