using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;

namespace Staffora.Services.Services;

public sealed class UserPasswordIssuer : IUserPasswordIssuer
{
    private readonly Dictionary<Guid, string> issued = [];
    private readonly IPasswordHasher<ApplicationUser> hasher;

    public UserPasswordIssuer(IPasswordHasher<ApplicationUser> hasher)
    {
        this.hasher = hasher;
    }

    public void IssueNewPassword(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var password = UserPasswords.Generate();
        user.PasswordHash = this.hasher.HashPassword(user, password);
        user.SecurityStamp = UserPasswords.NewSecurityStamp();
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;

        user.TwoFactorEnabled = false;
        this.issued[user.Id] = password;
    }

    public async Task RemoveOtherSignInMeansAsync(DbContext context, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tokens = context.Set<IdentityUserToken<Guid>>();
        tokens.RemoveRange(await tokens.Where(token => token.UserId == userId).ToListAsync(cancellationToken));
    }

    public string? TakeIssued(Guid userId) => this.issued.Remove(userId, out var password) ? password : null;
}

public static class UserPasswords
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!#$%*+-=?@";

    public static string Generate()
    {
        var all = Upper + Lower + Digits + Symbols;
        var characters = new List<char> { Pick(Upper), Pick(Lower), Pick(Digits), Pick(Symbols) };
        while (characters.Count < 16)
        {
            characters.Add(Pick(all));
        }

        for (var i = characters.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (characters[i], characters[j]) = (characters[j], characters[i]);
        }

        return new string([.. characters]);
    }

    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N").ToUpperInvariant();

    private static char Pick(string from) => from[RandomNumberGenerator.GetInt32(from.Length)];
}
