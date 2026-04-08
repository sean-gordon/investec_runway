using Dapper;
using GordonWorker.Models;
using Npgsql;

namespace GordonWorker.Repositories;

public class RefreshTokenRepository(IConfiguration configuration) : IRefreshTokenRepository
{
    private string ConnectionString => configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("DefaultConnection not found");

    public async Task<long> CreateAsync(int userId, string tokenHash, DateTime expiresAt, string? userAgent, string? ip)
    {
        using var db = new NpgsqlConnection(ConnectionString);
        return await db.QuerySingleAsync<long>(@"
            INSERT INTO refresh_tokens (user_id, token_hash, expires_at, user_agent, ip)
            VALUES (@UserId, @TokenHash, @ExpiresAt, @UserAgent, @Ip)
            RETURNING id;",
            new { UserId = userId, TokenHash = tokenHash, ExpiresAt = expiresAt, UserAgent = userAgent, Ip = ip });
    }

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash)
    {
        using var db = new NpgsqlConnection(ConnectionString);
        return await db.QuerySingleOrDefaultAsync<RefreshToken>(@"
            SELECT id           AS Id,
                   user_id      AS UserId,
                   token_hash   AS TokenHash,
                   issued_at    AS IssuedAt,
                   expires_at   AS ExpiresAt,
                   revoked_at   AS RevokedAt,
                   replaced_by  AS ReplacedBy
            FROM refresh_tokens
            WHERE token_hash = @TokenHash;",
            new { TokenHash = tokenHash });
    }

    public async Task RevokeAsync(long id, long? replacedById)
    {
        using var db = new NpgsqlConnection(ConnectionString);
        await db.ExecuteAsync(@"
            UPDATE refresh_tokens
            SET revoked_at  = NOW(),
                replaced_by = @ReplacedBy
            WHERE id = @Id AND revoked_at IS NULL;",
            new { Id = id, ReplacedBy = replacedById });
    }

    public async Task RevokeAllForUserAsync(int userId)
    {
        using var db = new NpgsqlConnection(ConnectionString);
        await db.ExecuteAsync(@"
            UPDATE refresh_tokens
            SET revoked_at = NOW()
            WHERE user_id = @UserId AND revoked_at IS NULL;",
            new { UserId = userId });
    }
}
