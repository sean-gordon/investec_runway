using GordonWorker.Models;

namespace GordonWorker.Repositories;

public interface IUserRepository
{
    Task<IEnumerable<User>> GetAllUsersAsync();
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetByIdAsync(int id);
    Task<int> CreateUserAsync(string username, string passwordHash, string role = "User");
    Task UpdateUserAsync(int id, string role, string? passwordHash = null);
    Task DeleteUserAsync(int id);
}
