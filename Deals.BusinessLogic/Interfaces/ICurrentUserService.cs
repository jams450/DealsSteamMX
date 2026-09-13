namespace Deals.BusinessLogic.Interfaces
{
    public interface ICurrentUserService
    {
        int? GetUserId();
        int GetRequiredUserId();
        int? GetSessionVersion();
        Guid? GetSessionId();
        string GetName();
        string GetEmail();
        bool IsAdmin();
    }
}
