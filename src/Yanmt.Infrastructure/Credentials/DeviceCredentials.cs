using Yanmt.Core.Credentials;

namespace Yanmt.Infrastructure.Credentials;

// Decrypted credentials, used only inside the collectors. They're classes rather than records so
// the compiler doesn't generate a ToString that would print the secrets into a log line.

public sealed class SshCredential(string username, string password)
{
    public string Username { get; } = username;
    public string Password { get; } = password;

    public override string ToString() => $"SshCredential(user={Username}, password=***)";
}

public sealed class SnmpV3Credential(string username, SnmpAuthProtocol auth, string authPassword, SnmpPrivProtocol priv, string privPassword)
{
    public string Username { get; } = username;
    public SnmpAuthProtocol Auth { get; } = auth;
    public string AuthPassword { get; } = authPassword;
    public SnmpPrivProtocol Priv { get; } = priv;
    public string PrivPassword { get; } = privPassword;

    public override string ToString() =>
        $"SnmpV3Credential(user={Username}, auth={CredentialRules.Describe(Auth)}, priv={CredentialRules.Describe(Priv)}, passwords=***)";
}
