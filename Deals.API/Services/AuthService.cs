using Deals.API.Interfaces;
using Deals.API.Models.Auth;
using Deals.API.Security;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace Deals.API.Services;

public class AuthService : IAuthService
{
    private readonly IJwtService _jwtService;
    private readonly IUserService _userService;
    private readonly AppDbContext _context;
    private readonly int _maxFailedAttempts;
    private readonly int _lockMinutes;
    private readonly int _refreshDays;

    public AuthService(
        IJwtService jwtService,
        IUserService userService,
        AppDbContext context,
        IOptions<AuthOptions> authOptions)
    {
        _jwtService = jwtService;
        _userService = userService;
        _context = context;
        var options = authOptions.Value;
        _maxFailedAttempts = Math.Max(1, options.MaxFailedAttempts);
        _lockMinutes = Math.Max(1, options.LockMinutes);
        _refreshDays = Math.Max(1, options.RefreshDays);
    }

    public async Task<LoginResponse?> AuthenticateAsync(LoginRequest request, string? ipAddress = null, string? userAgent = null)
    {
        var user = await AuthenticateRegularUserAsync(request);
        if (user == null)
        {
            return null;
        }

        return await BuildUserLoginResponseAsync(user, ipAddress, userAgent);
    }

    public async Task<LoginResponse?> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var session = await GetValidRefreshSessionAsync(refreshToken);
        if (session == null)
        {
            return null;
        }

        var user = session.User;
        if (!IsUserEligibleForRefresh(user))
        {
            return null;
        }

        var newRefreshToken = GenerateRefreshToken();
        var newRefreshExpiration = DateTime.UtcNow.AddDays(_refreshDays);
        var newSession = BuildNewSession(user.UserId, newRefreshToken, newRefreshExpiration, ipAddress, userAgent);

        if (!await RotateRefreshTokenAtomicAsync(session, newSession, HashToken(refreshToken)))
        {
            return null;
        }

        var accessToken = _jwtService.GenerateToken(user.UserId, user.Email, user.Admin, user.SessionVersion, newSession.SessionId);
        var accessExpiration = _jwtService.GetTokenExpiration();

        return new LoginResponse(accessToken, accessExpiration, user.Email, newRefreshToken, newRefreshExpiration);
    }

    public async Task<bool> RevokeRefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        var hash = HashToken(refreshToken);
        var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == hash);
        if (session == null || session.RevokedAt.HasValue)
        {
            return false;
        }

        session.RevokedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    private async Task<User?> AuthenticateRegularUserAsync(LoginRequest request)
    {
        var existing = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Username);
        if (existing != null && existing.LockedUntil.HasValue && existing.LockedUntil.Value > DateTime.UtcNow)
        {
            return null;
        }

        var user = await _userService.ValidateCredentialsAsync(request.Username, request.Password);
        if (user == null)
        {
            await RegisterFailedLoginAttemptAsync(existing);
            return null;
        }

        await ClearLockStateIfNeededAsync(user);
        return user;
    }

    private async Task<LoginResponse> BuildUserLoginResponseAsync(User user, string? ipAddress, string? userAgent)
    {
        var refreshToken = GenerateRefreshToken();
        var refreshExpiration = DateTime.UtcNow.AddDays(_refreshDays);

        var session = BuildNewSession(user.UserId, refreshToken, refreshExpiration, ipAddress, userAgent);
        _context.UserSessions.Add(session);
        await _context.SaveChangesAsync();

        var token = _jwtService.GenerateToken(user.UserId, user.Email, user.Admin, user.SessionVersion, session.SessionId);
        var expiration = _jwtService.GetTokenExpiration();

        return new LoginResponse(token, expiration, user.Email, refreshToken, refreshExpiration);
    }

    private UserSession BuildNewSession(int userId, string refreshToken, DateTime expiresAt, string? ipAddress = null, string? userAgent = null)
    {
        return new UserSession
        {
            UserId = userId,
            RefreshTokenHash = HashToken(refreshToken),
            ExpiresAt = expiresAt,
            Ip = Truncate(ipAddress, 64),
            UserAgent = Truncate(userAgent, 512),
            Created = DateTime.UtcNow,
            ReplacedBySessionId = null
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private async Task RegisterFailedLoginAttemptAsync(User? existing)
    {
        if (existing == null)
        {
            return;
        }

        existing.FailedLoginCount += 1;
        if (existing.FailedLoginCount >= _maxFailedAttempts)
        {
            existing.LockedUntil = DateTime.UtcNow.AddMinutes(_lockMinutes);
            existing.FailedLoginCount = 0;
        }

        await _context.SaveChangesAsync();
    }

    private async Task ClearLockStateIfNeededAsync(User user)
    {
        if (user.FailedLoginCount == 0 && user.LockedUntil == null)
        {
            return;
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await _context.SaveChangesAsync();
    }

    private async Task<UserSession?> GetValidRefreshSessionAsync(string refreshToken)
    {
        var hash = HashToken(refreshToken);
        var session = await _context.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash);

        if (session == null || session.RevokedAt.HasValue || session.ExpiresAt <= DateTime.UtcNow)
        {
            return null;
        }

        return session;
    }

    private static bool IsUserEligibleForRefresh(User user)
    {
        return user.Active && (!user.LockedUntil.HasValue || user.LockedUntil.Value <= DateTime.UtcNow);
    }

    private async Task<bool> RotateRefreshTokenAtomicAsync(UserSession currentSession, UserSession newSession, string refreshTokenHash)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var now = DateTime.UtcNow;

        var revoked = await _context.UserSessions
            .Where(s => s.SessionId == currentSession.SessionId
                && s.RefreshTokenHash == refreshTokenHash
                && s.RevokedAt == null
                && s.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.ReplacedBySessionId, newSession.SessionId));
        if (revoked != 1)
        {
            await transaction.RollbackAsync();
            return false;
        }

        _context.UserSessions.Add(newSession);
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    private static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }
}
