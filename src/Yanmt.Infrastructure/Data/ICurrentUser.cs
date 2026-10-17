namespace Yanmt.Infrastructure.Data;

/// <summary>Who is making a change, for the audit log. The web host supplies the signed-in Windows user.</summary>
public interface ICurrentUser
{
    ValueTask<string> GetNameAsync(CancellationToken cancellationToken = default);
}

/// <summary>Used by background services and design-time tooling.</summary>
public sealed class SystemUser : ICurrentUser
{
    public const string Name = "system";

    public ValueTask<string> GetNameAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Name);
}
