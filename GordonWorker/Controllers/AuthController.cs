using BCrypt.Net;
using Dapper;
using GordonWorker.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace GordonWorker.Controllers;

[ApiController]
[EnableRateLimiting("auth")]
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

        if (!IsPasswordStrong(model.Password))
            return BadRequest(
                "Password must be at least 12 characters and include upper-case, lower-case, a digit, and a symbol.");

        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            var existingUser = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM users WHERE username = @Username", new { model.Username });

            // Return the same generic response whether or not the username exists so the
            // endpoint can't be used to enumerate accounts. Log the duplicate for ops.
            if (existingUser != null)
            {
                _logger.LogInformation("Registration attempt for existing username from {IP}.", HttpContext.Connection.RemoteIpAddress);
                return Ok(new { Message = "Registration request received." });
            }

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(model.Password);

            await connection.ExecuteAsync(
                "INSERT INTO users (username, password_hash) VALUES (@Username, @PasswordHash)",
                new { model.Username, PasswordHash = passwordHash });

            _logger.LogInformation("New user registered from {IP}.", HttpContext.Connection.RemoteIpAddress);
            return Ok(new { Message = "Registration request received." });
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
            {
                _logger.LogWarning("Failed login for username '{User}' from {IP}.", model.Username, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized("Invalid username or password.");
            }

            _logger.LogInformation("Successful login for user {UserId} from {IP}.", user.Id, HttpContext.Connection.RemoteIpAddress);
            var token = GenerateJwtToken(user);
            return Ok(new { Token = token, Username = user.Username });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed.");
            return StatusCode(500, "Internal server error.");
        }
    }

    private static bool IsPasswordStrong(string password)
    {
        if (password.Length < 12) return false;
        if (!Regex.IsMatch(password, "[A-Z]")) return false;
        if (!Regex.IsMatch(password, "[a-z]")) return false;
        if (!Regex.IsMatch(password, "[0-9]")) return false;
        if (!Regex.IsMatch(password, "[^A-Za-z0-9]")) return false;
        return true;
    }

    private string GenerateJwtToken(User user)
    {
        var jwtSettings = _configuration.GetSection("Jwt");
        var secret = Environment.GetEnvironmentVariable("JWT_SECRET") 
                     ?? jwtSettings["Secret"] 
                     ?? throw new InvalidOperationException("CRITICAL: JWT Secret is not configured in Environment Variables.");
                     
        var key = Encoding.UTF8.GetBytes(secret);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role)
            }),
            // Shorter session: an XSS-stolen token is now useful for hours, not a week.
            Expires = DateTime.UtcNow.AddHours(12),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
            Issuer = jwtSettings["Issuer"],
            Audience = jwtSettings["Audience"]
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
