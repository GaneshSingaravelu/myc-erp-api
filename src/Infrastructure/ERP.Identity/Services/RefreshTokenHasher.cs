using System.Security.Cryptography;
using System.Text;
using ERP.Identity.Abstractions;
using ERP.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace ERP.Identity.Services;

/// <summary>
/// R2-DEV-03A — HMAC-SHA-256 refresh-token digests keyed by <see cref="RefreshTokenSettings.HmacKey"/>.
/// The key is validated on construction (fail closed) and never logged. Singleton.
/// </summary>
public sealed class RefreshTokenHasher : IRefreshTokenHasher
{
    private readonly byte[] _key;

    public RefreshTokenHasher(IOptions<RefreshTokenSettings> settings)
    {
        RefreshTokenSettings.Validate(settings.Value);
        _key = Encoding.UTF8.GetBytes(settings.Value.HmacKey);
    }

    public string Hash(string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        var digest = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(refreshToken));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
