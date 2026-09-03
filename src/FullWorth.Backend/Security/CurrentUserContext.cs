namespace FullWorth.Backend.Security;

public sealed class CurrentUserContext
{
    public Guid UserId { get; private set; }
    public bool IsAuthenticated { get; private set; }

    internal void SetAuthenticated(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A current user id is required.", nameof(userId));

        UserId = userId;
        IsAuthenticated = true;
    }

    public Guid RequireUserId() => IsAuthenticated && UserId != Guid.Empty
        ? UserId
        : throw new InvalidOperationException("An authenticated Finance user context is required.");
}
