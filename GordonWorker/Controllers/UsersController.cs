using BCrypt.Net;
using Dapper;
using GordonWorker.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace GordonWorker.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IConfiguration configuration, ILogger<UsersController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        var users = await connection.QueryAsync<User>("SELECT id, username, role, is_system, created_at FROM users ORDER BY id");
        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Username and Password are required.");

        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            var existing = await connection.QuerySingleOrDefaultAsync<int?>("SELECT id FROM users WHERE username = @Username", new { request.Username });
            if (existing != null) return BadRequest("Username taken.");

            var hash = BCrypt.Net.BCrypt.HashPassword(request.Password);
            var role = request.Role == "Admin" ? "Admin" : "User";

            await connection.ExecuteAsync(
                "INSERT INTO users (username, password_hash, role) VALUES (@Username, @Hash, @Role)",
                new { request.Username, Hash = hash, Role = role });

            return Ok(new { Message = "User created." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create user failed.");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserRequest request)
    {
        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            var user = await connection.QuerySingleOrDefaultAsync<User>("SELECT * FROM users WHERE id = @Id", new { Id = id });
            if (user == null) return NotFound();

            // Only allow changing password if provided
            var passSql = "";
            object passParam = new { };
            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                passSql = ", password_hash = @Hash";
                passParam = new { Hash = BCrypt.Net.BCrypt.HashPassword(request.Password) };
            }

            // Only allow changing role if not system admin or if it's not downgrading the system admin (though system admin shouldn't be touched usually)
            // Ideally system admin role is locked.
            if (user.IsSystem && request.Role != "Admin")
            {
                return BadRequest("Cannot demote System Admin.");
            }

            var sql = $"UPDATE users SET role = @Role {passSql} WHERE id = @Id";
            
            var parameters = new DynamicParameters();
            parameters.Add("Id", id);
            parameters.Add("Role", request.Role == "Admin" ? "Admin" : "User");
            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                parameters.Add("Hash", BCrypt.Net.BCrypt.HashPassword(request.Password));
            }

            await connection.ExecuteAsync(sql, parameters);
            return Ok(new { Message = "User updated." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update user failed.");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            var user = await connection.QuerySingleOrDefaultAsync<User>("SELECT * FROM users WHERE id = @Id", new { Id = id });
            
            if (user == null) return NotFound();
            if (user.IsSystem) return BadRequest("Cannot delete System Admin user.");

            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            
            // Cascade delete handled by DB constraints for settings/transactions, but good to be explicit or rely on ON DELETE CASCADE
            // Our Init script has ON DELETE CASCADE, so just deleting user is enough.
            await connection.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = id }, transaction);
            
            transaction.Commit();
            return Ok(new { Message = "User deleted." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete user failed.");
            return StatusCode(500, ex.Message);
        }
    }

    public class CreateUserRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string Role { get; set; } = "User";
    }

    public class UpdateUserRequest
    {
        public string? Password { get; set; }
        public string Role { get; set; } = "User";
    }
}
