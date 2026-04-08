using GordonWorker.Models;

namespace GordonWorker.Repositories;

public interface IRefreshTokenRepository
{
    /// <summary>
    /// Persists a hashed refresh token row and returns the new id.
    /// </summary>
    Task<long> CreateAsync(int userId, string tokenHash, DateTime expiresAt, string? userAgent, string? ip);

    /// <summary>
    /// Looks up a refresh token by its SHA-256 hex hash. Returns null if not found.
    /// Caller is responsible for checking revoked_at / expires_at.
    /// </summary>
    Task<RefreshToken?> GetByHashAsync(string tokenHash);

    /// <summary>
    /// Marks a token as revoked. If <paramref name="replacedById"/> is provided, the
    /// rotation chain is recorded so we can audit reuse-after-rotation later.
    /// </summary>
    Task RevokeAsync(long id, long? replacedById);

    /// <summary>
    /// Revokes every active refresh token for a user. Used on password change or
    /// when reuse of an already-revoked token is detected (likely theft).
    /// </summary>
    Task RevokeAllForUserAsync(int userId);
}
