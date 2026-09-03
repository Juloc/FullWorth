namespace FullWorth.Backend.Modules.Users;

public sealed record CreateUserRequest(string Email, string DisplayName);

public sealed record UpdateUserRequest(string Email, string DisplayName, bool IsActive);
