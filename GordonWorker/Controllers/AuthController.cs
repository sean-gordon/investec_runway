using BCrypt.Net;
using Dapper;
using GordonWorker.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace GordonWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IConfiguration configuration, ILogger<AuthController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(model.Password))
            return BadRequest("Username and Password are required.");

        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            var existingUser = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM users WHERE username = @Username", new { model.Username });

            if (existingUser != null)
                return BadRequest("Username already exists.");

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(model.Password);

            var userId = await connection.QuerySingleAsync<int>(
                "INSERT INTO users (username, password_hash) VALUES (@Username, @PasswordHash) RETURNING id",
                new { model.Username, PasswordHash = passwordHash });

            return Ok(new { Message = "Registration successful." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed.");
            return StatusCode(500, "Internal server error.");
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginModel model)
    {
        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            var user = await connection.QuerySingleOrDefaultAsync<User>(
                "SELECT * FROM users WHERE username = @Username", new { model.Username });

            if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
                return Unauthorized("Invalid username or password.");

            var token = GenerateJwtToken(user);
            return Ok(new { Token = token, Username = user.Username });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed.");
            return StatusCode(500, "Internal server error.");
        }
    }

    private string GenerateJwtToken(User user)
    {
        var jwtSettings = _configuration.GetSection("Jwt");
        var key = Encoding.ASCII.GetBytes(jwtSettings["Secret"] ?? "SUPER_SECRET_FALLBACK_KEY_CHANGE_ME_NOW");

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role)
            }),
            Expires = DateTime.UtcNow.AddDays(7),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
            Issuer = jwtSettings["Issuer"],
            Audience = jwtSettings["Audience"]
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
