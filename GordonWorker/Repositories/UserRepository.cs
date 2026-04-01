using Dapper;
using GordonWorker.Models;
using Npgsql;

namespace GordonWorker.Repositories;

public class UserRepository(IConfiguration configuration) : IUserRepository
{
    private string ConnectionString => configuration.GetConnectionString("DefaultConnection") 
        ?? throw new InvalidOperationException("DefaultConnection not found");

    public async Task<IEnumerable<User>> GetAllUsersAsync()
    {
        using var db = new NpgsqlConnection(ConnectionString);
        return await db.QueryAsync<User>(
            "SELECT id, username, role, is_system, created_at, last_weekly_report_sent FROM users ORDER BY id");
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        using var db = new NpgsqlConnection(ConnectionString);
        return await db.QuerySingleOrDefaultAsync<User>(
            "SELECT * FROM users WHERE username = @Username", new { Username = username });
    }

    public async Task<User?> GetByIdAsync(int id)
    {
        using var db = new NpgsqlConnection(ConnectionString);
        return await db.QuerySingleOrDefaultAsync<User>(
            "SELECT * FROM users WHERE id = @Id", new { Id = id });
    }

    public async Task<int> CreateUserAsync(string username, string passwordHash, string role = "User")
    {
        using var db = new NpgsqlConnection(ConnectionString);
        return await db.QuerySingleAsync<int>(
            "INSERT INTO users (username, password_hash, role) VALUES (@Username, @PasswordHash, @Role) RETURNING id",
            new { Username = username, PasswordHash = passwordHash, Role = role });
    }

    public async Task UpdateUserAsync(int id, string role, string? passwordHash = null)
    {
        using var db = new NpgsqlConnection(ConnectionString);
        var sql = "UPDATE users SET role = @Role";
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);
        parameters.Add("Role", role);

        if (!string.IsNullOrWhiteSpace(passwordHash))
        {
            sql += ", password_hash = @PasswordHash";
            parameters.Add("PasswordHash", passwordHash);
        }

        sql += " WHERE id = @Id";
        await db.ExecuteAsync(sql, parameters);
    }

    public async Task DeleteUserAsync(int id)
    {
        using var db = new NpgsqlConnection(ConnectionString);
        await db.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = id });
    }
}
