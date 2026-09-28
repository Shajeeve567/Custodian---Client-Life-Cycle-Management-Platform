using System.Text;
using Microsoft.Extensions.Hosting;

namespace Custodian.Shared.Auth;

/// <summary>
/// Startup check for Jwt:SigningKey, shared by every service (Identity signs with it, the others
/// validate with it).
///
/// The development key is committed in each service's appsettings.json and the repository is
/// public, so anyone could mint a valid Owner/Staff token for any tenant with it. It is accepted
/// only in the Development and Testing environments; everywhere else the real key must come from
/// the environment (Jwt__SigningKey in Azure App Service settings). A missing, placeholder or
/// too-short key fails startup in every environment, instead of failing later on the first token.
/// </summary>
public static class JwtSigningKeyGuard
{
    /// <summary>The key committed in appsettings.json. Public knowledge: never valid outside dev/test.</summary>
    public const string CommittedDevelopmentKey =
        "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";

    /// <summary>HMAC-SHA256 needs at least a 256-bit key.</summary>
    public const int MinimumKeyBytes = 32;

    public const string TestingEnvironment = "Testing";

    public static void Validate(string? signingKey, IHostEnvironment environment) =>
        Validate(signingKey, environment.EnvironmentName);

    public static void Validate(string? signingKey, string environmentName)
    {
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. Set the Jwt__SigningKey environment variable.");
        }

        if (signingKey.Contains("FILL_IN_HERE", StringComparison.OrdinalIgnoreCase)
            || signingKey.StartsWith('<'))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is still the .env.example placeholder. Generate a real key (e.g. `openssl rand -base64 64`).");
        }

        if (Encoding.UTF8.GetByteCount(signingKey) < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey must be at least {MinimumKeyBytes} bytes for HMAC-SHA256.");
        }

        if (signingKey == CommittedDevelopmentKey && !IsDevelopmentOrTesting(environmentName))
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey is the public development key from appsettings.json, which is not allowed in the " +
                $"'{environmentName}' environment. Set Jwt__SigningKey to a secret value in the app settings.");
        }
    }

    private static bool IsDevelopmentOrTesting(string environmentName) =>
        string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
        || string.Equals(environmentName, TestingEnvironment, StringComparison.OrdinalIgnoreCase);
}
