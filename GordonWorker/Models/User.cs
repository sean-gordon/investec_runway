namespace GordonWorker.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "User"; // "Admin" or "User"
    public bool IsSystem { get; set; } = false; // Cannot be deleted
    public DateTime CreatedAt { get; set; }
}

public class RegisterModel
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? Role { get; set; }
}

public class LoginModel
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class RefreshToken
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public long? ReplacedBy { get; set; }
}
