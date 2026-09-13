using System.Security.Claims;
using Deals.BusinessLogic.Interfaces;
using Deals.API.Security;

namespace Deals.API.Services
{
    public class CurrentUserService : ICurrentUserService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CurrentUserService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public int? GetUserId()
        {
            return TryGetIntClaim(ClaimNames.NameIdentifier, ClaimNames.Subject);
        }

        public int GetRequiredUserId()
        {
            var userId = GetUserId();
            if (!userId.HasValue)
            {
                throw new InvalidOperationException("Authenticated user id claim is missing or invalid.");
            }

            return userId.Value;
        }

        public int? GetSessionVersion()
        {
            return TryGetIntClaim(ClaimNames.SessionVersion);
        }

        public Guid? GetSessionId()
        {
            return TryGetGuidClaim(ClaimNames.SessionId);
        }

        public string GetEmail()
        {
            return GetClaimValue(ClaimNames.Name, ClaimTypes.Email, "preferred_username")
                ?? "System";
        }

        public string GetName()
        {
            return GetClaimValue(ClaimNames.Name, "name")
                ?? "System";
        }

        public bool IsAdmin()
        {
            return string.Equals(GetClaimValue(ClaimNames.Role), ClaimNames.AdminRole, StringComparison.OrdinalIgnoreCase);
        }

        private string? GetClaimValue(params string[] claimTypes)
        {
            var claims = GetClaimsIdentity();
            if (claims == null)
            {
                return null;
            }

            foreach (var claimType in claimTypes)
            {
                if (string.IsNullOrWhiteSpace(claimType))
                {
                    continue;
                }

                var value = claims.FindFirst(claimType)?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private int? TryGetIntClaim(params string[] claimTypes)
        {
            var value = GetClaimValue(claimTypes);
            return int.TryParse(value, out var parsed) ? parsed : null;
        }

        private Guid? TryGetGuidClaim(params string[] claimTypes)
        {
            var value = GetClaimValue(claimTypes);
            return Guid.TryParse(value, out var parsed) ? parsed : null;
        }

        private ClaimsIdentity? GetClaimsIdentity()
        {
            return _httpContextAccessor.HttpContext?.User?.Identity as ClaimsIdentity;
        }
    }
}
