using GordonWorker.Models;
using GordonWorker.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace GordonWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AuthController> _logger;

    // Brute-force lockout policy: track failed attempts per (IP + username) pair in a sliding
    // 15-minute window. After 5 failures we refuse to even check the password for 15 minutes.
    // This slows credential-stuffing attacks from 6000/hour (the global 100/min limiter) down
    // to 20/hour per target, while still letting legitimate users recover quickly.
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);

    public AuthController(IUserRepository userRepository, IConfiguration configuration, IMemoryCache cache, ILogger<AuthController> logger)
    {
        _userRepository = userRepository;
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
    }

    private string GetLockoutKey(string username)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"login_fail:{ip}:{username.ToLowerInvariant()}";
    }

    private bool IsLockedOut(string username)
    {
        return _cache.TryGetValue<int>(GetLockoutKey(username), out var count) && count >= MaxFailedAttempts;
    }

    private void RegisterFailedAttempt(string username)
    {
        var key = GetLockoutKey(username);
        var count = _cache.TryGetValue<int>(key, out var existing) ? existing + 1 : 1;
        _cache.Set(key, count, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = LockoutWindow
        });
    }

    private void ClearFailedAttempts(string username)
    {
        _cache.Remove(GetLockoutKey(username));
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(model.Password))
            return BadRequest("Username and Password are required.");

        try
        {
            var existingUser = await _userRepository.GetByUsernameAsync(model.Username);
            if (existingUser != null)
                return BadRequest("Username already exists.");

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(model.Password);
            await _userRepository.CreateUserAsync(model.Username, passwordHash);

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
        if (string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(model.Password))
            return BadRequest("Username and password are required.");

        // Check lockout BEFORE touching the database, so a locked-out attacker can't even
        // force bcrypt work (which is our most expensive per-request operation).
        if (IsLockedOut(model.Username))
        {
            _logger.LogWarning("Login blocked for locked-out account {Username} from {IP}",
                model.Username, HttpContext.Connection.RemoteIpAddress);
            Response.Headers["Retry-After"] = ((int)LockoutWindow.TotalSeconds).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests,
                "Too many failed login attempts. Try again in 15 minutes.");
        }

        try
        {
            var user = await _userRepository.GetByUsernameAsync(model.Username);

            if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
            {
                RegisterFailedAttempt(model.Username);
                return Unauthorized("Invalid username or password.");
            }

            // Success — reset the counter so future typos don't accumulate forever.
            ClearFailedAttempts(model.Username);

            var token = GenerateJwtToken(user);
            return Ok(new { Token = token, Username = user.Username });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for {Username}.", model.Username);
            return StatusCode(500, "Internal server error.");
        }
    }

    private string GenerateJwtToken(User user)
    {
        var jwtSettings = _configuration.GetSection("Jwt");
        var secret = Environment.GetEnvironmentVariable("JWT_SECRET") 
                     ?? jwtSettings["Secret"] 
                     ?? throw new InvalidOperationException("CRITICAL: JWT Secret is not configured in Environment Variables.");
                     
        var key = Encoding.ASCII.GetBytes(secret);

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
