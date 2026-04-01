using BCrypt.Net;
using GordonWorker.Models;
using GordonWorker.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GordonWorker.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IUserRepository userRepository, ILogger<UsersController> logger)
    {
        _userRepository = userRepository;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var users = await _userRepository.GetAllUsersAsync();
        // Return only non-sensitive data
        return Ok(users.Select(u => new { u.Id, u.Username, u.Role, u.IsSystem, u.CreatedAt }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Username and Password are required.");

        try
        {
            var existing = await _userRepository.GetByUsernameAsync(request.Username);
            if (existing != null) return BadRequest("Username taken.");

            var hash = BCrypt.Net.BCrypt.HashPassword(request.Password);
            var role = request.Role == "Admin" ? "Admin" : "User";

            await _userRepository.CreateUserAsync(request.Username, hash, role);
            return Ok(new { Message = "User created." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create user failed.");
            return StatusCode(500, "Internal server error.");
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserRequest request)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(id);
            if (user == null) return NotFound();

            if (user.IsSystem && request.Role != "Admin")
            {
                return BadRequest("Cannot demote System Admin.");
            }

            string? hash = null;
            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                hash = BCrypt.Net.BCrypt.HashPassword(request.Password);
            }

            var role = request.Role == "Admin" ? "Admin" : "User";
            await _userRepository.UpdateUserAsync(id, role, hash);
            
            return Ok(new { Message = "User updated." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update user failed.");
            return StatusCode(500, "Internal server error.");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(id);
            if (user == null) return NotFound();
            if (user.IsSystem) return BadRequest("Cannot delete System Admin user.");

            await _userRepository.DeleteUserAsync(id);
            return Ok(new { Message = "User deleted." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete user failed.");
            return StatusCode(500, "Internal server error.");
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
