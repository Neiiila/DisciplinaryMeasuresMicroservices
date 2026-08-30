using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Core.Abstractions;
using Identity.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Identity.Api.Infrastructure;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string storedHash, string providedPassword);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 with a per-password salt.
/// </summary>
/// <remarks>
/// Salt and hash travel in one string as <c>iterations.salt.hash</c>, so the
/// parameters used to produce a hash are stored alongside it and the iteration
/// count can be raised later without invalidating existing passwords.
/// </remarks>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 210_000;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string storedHash, string providedPassword)
    {
        if (string.IsNullOrWhiteSpace(storedHash) || string.IsNullOrWhiteSpace(providedPassword))
        {
            return false;
        }

        var parts = storedHash.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                providedPassword, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

            // Constant-time: a length-dependent early return would leak information
            // about the stored hash through timing.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public int LifetimeMinutes { get; set; } = 480;
}

public interface IAccessTokenGenerator
{
    (string Token, DateTimeOffset ExpiresOn) Generate(User user);
}

/// <summary>
/// Issues the HMAC-SHA256 tokens every service in the platform validates.
/// </summary>
/// <remarks>
/// Identity is the only issuer. The token carries the subject, display name and
/// role and nothing else: it is sent on every request to every service, so
/// anything added here is paid for on each one.
/// </remarks>
public sealed class JwtAccessTokenGenerator(IOptions<JwtOptions> options, IClock clock) : IAccessTokenGenerator
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTimeOffset ExpiresOn) Generate(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var issuedAt = clock.UtcNow;
        var expiresOn = issuedAt.AddMinutes(_options.LifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Name.Full),
            new(ClaimTypes.Role, user.Role.ToString())
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresOn.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key)),
                SecurityAlgorithms.HmacSha256)
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresOn);
    }
}
