using Deals.BusinessLogic.Models.Users;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Interfaces
{
    public interface IUserService
    {
        Task<User?> GetByIdAsync(int id);
        Task<User?> GetByEmailAsync(string email);
        Task<IEnumerable<User>> GetAllAsync();
        Task<User> CreateAsync(User user);
        Task<User?> UpdateAsync(int id, User user);
        Task<bool> ChangePasswordAsync(int id, string newPassword);
        Task<bool> DeleteAsync(int id);
        Task<bool> UpdateActiveStatusAsync(int id, bool active);
        Task<bool> UpdateAdminStatusAsync(int id, bool isAdmin);
        Task<CurrentUserProfile?> GetCurrentProfileAsync(int userId);
        Task<CurrentUserProfile?> UpdateCurrentProfileAsync(int userId, string name, string email);
        Task<ChangeOwnPasswordResult> ChangeOwnPasswordAsync(int userId, string currentPassword, string newPassword);
        Task<IReadOnlyList<CurrentUserSession>> GetSessionsAsync(int userId);
        Task<bool> RevokeSessionAsync(int userId, Guid sessionId);
        Task<bool> RevokeOtherSessionsAsync(int userId, Guid currentSessionId);
        Task<bool> RevokeAllSessionsAsync(int userId);
        Task<User?> ValidateCredentialsAsync(string email, string password);
    }
}
