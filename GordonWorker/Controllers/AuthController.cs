using GordonWorker.Models;
using GordonWorker.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace GordonWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AuthController> _logger;

    // Brute-force lockout policy: track failed attempts per (IP + username) pair in a sliding
    // 15-minute window. After 5 failures we refuse to even check the password for 15 minutes.
    // This slows credential-stuffing attacks from 6000/hour (the global 100/min limiter) down
    // to 20/hour per target, while still letting legitimate users recover quickly.
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);

    // Token lifetimes for the rotating-refresh-cookie flow.
    // Access tokens are deliberately short so a stolen JWT is only useful for ~15 minutes.
    // Refresh tokens live for 30 days but are rotated on every use, so theft is detectable
    // (the original holder gets a 401 the next time they try to refresh).
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);
    private const string RefreshCookieName = "gfe_rt";

    public AuthController(
        IUserRepository userRepository,
        IRefreshTokenRepository refreshTokenRepository,
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<AuthController> logger)
    {
        _userRepository = userRepository;
        _refreshTokenRepository = refreshTokenRepository;
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

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(model.Password))
            return BadRequest("Username and Password are required.");

        // Security check for role: only admins can create other admins
        var role = model.Role ?? "User";
        if (role == "Admin" && !User.IsInRole("Admin"))
        {
            return Forbid("Only admins can create other admin users.");
        }

        try
        {
            var existingUser = await _userRepository.GetByUsernameAsync(model.Username);
            if (existingUser != null)
                return BadRequest("Username already exists.");

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(model.Password);
            await _userRepository.CreateUserAsync(model.Username, passwordHash, role);

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
            await IssueRefreshCookieAsync(user.Id);
            return Ok(new { Token = token, Username = user.Username });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for {Username}.", model.Username);
            return StatusCode(500, "Internal server error.");
        }
    }

    /// <summary>
    /// Exchanges a valid refresh cookie for a new access token, rotating the refresh token in
    /// the process. Detection of a re-used (already-revoked) token revokes the entire chain for
    /// that user — that's the canonical sign of a stolen refresh token.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (!Request.Cookies.TryGetValue(RefreshCookieName, out var rawToken) || string.IsNullOrWhiteSpace(rawToken))
        {
            return Unauthorized();
        }

        var hash = HashRefreshToken(rawToken);
        var stored = await _refreshTokenRepository.GetByHashAsync(hash);

        if (stored == null)
        {
            // Unknown token — could be tampered or just expired off the table. Clear the cookie
            // so the browser doesn't keep retrying with the same garbage.
            ClearRefreshCookie();
            return Unauthorized();
        }

        if (stored.RevokedAt != null)
        {
            // Revoked-token reuse: someone is presenting a token we already rotated. Either the
            // legitimate user is on a stale tab OR the token was stolen. Safest move is to nuke
            // every active token for this user and force re-login.
            _logger.LogWarning("Refresh token reuse detected for user {UserId} (token id {TokenId}); revoking all active tokens.",
                stored.UserId, stored.Id);
            await _refreshTokenRepository.RevokeAllForUserAsync(stored.UserId);
            ClearRefreshCookie();
            return Unauthorized();
        }

        if (stored.ExpiresAt <= DateTime.UtcNow)
        {
            ClearRefreshCookie();
            return Unauthorized();
        }

        var user = await _userRepository.GetByIdAsync(stored.UserId);
        if (user == null)
        {
            ClearRefreshCookie();
            return Unauthorized();
        }

        // Rotate: insert the new row first, then mark the old as replaced_by the new.
        var newRawToken = GenerateOpaqueToken();
        var newHash = HashRefreshToken(newRawToken);
        var newId = await _refreshTokenRepository.CreateAsync(
            user.Id,
            newHash,
            DateTime.UtcNow.Add(RefreshTokenLifetime),
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString());
        await _refreshTokenRepository.RevokeAsync(stored.Id, newId);

        WriteRefreshCookie(newRawToken);

        var accessToken = GenerateJwtToken(user);
        return Ok(new { Token = accessToken, Username = user.Username });
    }

    /// <summary>
    /// Revokes the current refresh token (if any) and clears the cookie.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (Request.Cookies.TryGetValue(RefreshCookieName, out var rawToken) && !string.IsNullOrWhiteSpace(rawToken))
        {
            var hash = HashRefreshToken(rawToken);
            var stored = await _refreshTokenRepository.GetByHashAsync(hash);
            if (stored != null && stored.RevokedAt == null)
            {
                await _refreshTokenRepository.RevokeAsync(stored.Id, replacedById: null);
            }
        }
        ClearRefreshCookie();
        return NoContent();
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
            Expires = DateTime.UtcNow.Add(AccessTokenLifetime),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
            Issuer = jwtSettings["Issuer"],
            Audience = jwtSettings["Audience"]
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private async Task IssueRefreshCookieAsync(int userId)
    {
        var rawToken = GenerateOpaqueToken();
        var hash = HashRefreshToken(rawToken);
        await _refreshTokenRepository.CreateAsync(
            userId,
            hash,
            DateTime.UtcNow.Add(RefreshTokenLifetime),
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString());
        WriteRefreshCookie(rawToken);
    }

    private void WriteRefreshCookie(string rawToken)
    {
        // Path is scoped to /api/auth so the cookie is only ever sent to refresh/logout — every
        // other API call carries the JWT in Authorization instead. SameSite=Strict prevents the
        // cookie from being attached to any cross-site request, which is the practical CSRF
        // mitigation for this endpoint set.
        Response.Cookies.Append(RefreshCookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            Expires = DateTimeOffset.UtcNow.Add(RefreshTokenLifetime)
        });
    }

    private void ClearRefreshCookie()
    {
        Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth"
        });
    }

    private static string GenerateOpaqueToken()
    {
        // 32 bytes = 256 bits of entropy, base64url-encoded so it survives a Set-Cookie header
        // without any escaping shenanigans.
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashRefreshToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes);
    }
}
