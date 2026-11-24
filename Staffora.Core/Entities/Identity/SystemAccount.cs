using System.Security.Cryptography;

namespace Staffora.Core.Entities.Identity;

public static class SystemAccount
{
    public static readonly Guid IdNamespace = new("6f1c2a9e-4b7d-4e35-8a60-2d9b7c41e8f3");

    public const string FirstName = "System";

    public const string LastName = "account";

    public static Guid IdFor(Guid tenantId)
    {
        Span<byte> name = stackalloc byte[32];
        IdNamespace.TryWriteBytes(name[..16], bigEndian: true, out _);
        tenantId.TryWriteBytes(name[16..], bigEndian: true, out _);

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(name, hash);

        var id = hash[..16];
        id[6] = (byte)((id[6] & 0x0F) | 0x50);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id, bigEndian: true);
    }

    public static string UserNameFor(Guid tenantId) => $"{UserNamePrefix}{tenantId:N}";

    public const string UserNamePrefix = "system:";

    public static IQueryable<ApplicationUser> ExceptSystemAccounts(this IQueryable<ApplicationUser> users) =>
        users.Where(user => !user.IsSystemAccount);
}
