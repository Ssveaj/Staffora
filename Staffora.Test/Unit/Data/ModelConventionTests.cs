using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Staffora.Core.Entities;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data.Conventions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Data;

public partial class ModelConventionTests
{
    private static readonly (Type Dependent, Type Principal)[] ownedRows =
    [
        (typeof(TenantRolePermission), typeof(TenantRole)),
        (typeof(OrganizationUnitAttribute), typeof(OrganizationUnit)),
        (typeof(ApplicationUserAttribute), typeof(ApplicationUser)),

        (typeof(Staffora.Core.Entities.Scheduling.WorkPatternSegment), typeof(Staffora.Core.Entities.Scheduling.WorkPattern)),

        (typeof(Staffora.Core.Entities.Jobs.JobTimeSlot), typeof(Staffora.Core.Entities.Jobs.Job)),
        (typeof(Staffora.Core.Entities.Jobs.JobSlotQualification), typeof(Staffora.Core.Entities.Jobs.JobSlot)),

        (typeof(Staffora.Core.Entities.Catalogue.ServiceQualification), typeof(Staffora.Core.Entities.Catalogue.Service)),

        (typeof(Staffora.Core.Entities.Portal.OrderLine), typeof(Staffora.Core.Entities.Portal.Order)),

        (typeof(Staffora.Core.Entities.EntityAttributes.JobAttributes.JobAttribute), typeof(Staffora.Core.Entities.Jobs.Job)),

        (typeof(IdentityUserClaim<Guid>), typeof(ApplicationUser)),
        (typeof(IdentityUserLogin<Guid>), typeof(ApplicationUser)),
        (typeof(IdentityUserToken<Guid>), typeof(ApplicationUser)),
        (typeof(IdentityUserRole<Guid>), typeof(ApplicationUser)),
        (typeof(IdentityUserRole<Guid>), typeof(ApplicationRole)),
        (typeof(IdentityRoleClaim<Guid>), typeof(ApplicationRole)),
    ];

    private static readonly (Type Entity, string Property)[] unboundedStrings =
    [
        (typeof(ErrorLog), nameof(ErrorLog.ExceptionDetail)),
        (typeof(ApplicationUserAttributeDefinition), nameof(ApplicationUserAttributeDefinition.Options)),
        (typeof(OrganizationUnitAttributeDefinition), nameof(OrganizationUnitAttributeDefinition.Options)),
        (typeof(Staffora.Core.Entities.EntityAttributes.JobAttributes.JobAttributeDefinition), nameof(OrganizationUnitAttributeDefinition.Options)),
        (typeof(ApplicationUser), nameof(ApplicationUser.PasswordHash)),
        (typeof(ApplicationUser), nameof(ApplicationUser.SecurityStamp)),
        (typeof(ApplicationUser), nameof(ApplicationUser.ConcurrencyStamp)),
        (typeof(ApplicationRole), nameof(ApplicationRole.ConcurrencyStamp)),
        (typeof(IdentityUserClaim<Guid>), nameof(IdentityUserClaim<>.ClaimType)),
        (typeof(IdentityUserClaim<Guid>), nameof(IdentityUserClaim<>.ClaimValue)),
        (typeof(IdentityRoleClaim<Guid>), nameof(IdentityRoleClaim<>.ClaimType)),
        (typeof(IdentityRoleClaim<Guid>), nameof(IdentityRoleClaim<>.ClaimValue)),
        (typeof(IdentityUserLogin<Guid>), nameof(IdentityUserLogin<>.ProviderDisplayName)),
        (typeof(IdentityUserToken<Guid>), nameof(IdentityUserToken<>.Value)),
    ];

    private static readonly string[] auditForeignKeys =
        [nameof(ITenantEntity.CreatedById), nameof(ITenantEntity.ModifiedById), nameof(ITenantEntity.ImpersonatedById)];

    [Fact]
    public void Every_relationship_is_NoAction_or_on_the_owned_row_list()
    {
        var foreignKeys = ModelUnderTest.Model.GetEntityTypes().SelectMany(entityType => entityType.GetDeclaredForeignKeys()).ToList();

        var offending = foreignKeys
            .Where(fk => fk.DeleteBehavior != DeleteBehavior.NoAction)
            .Where(fk => !ownedRows.Contains((fk.DeclaringEntityType.ClrType, fk.PrincipalEntityType.ClrType)))
            .Select(fk => $"{fk.DeclaringEntityType.DisplayName()}.{string.Join(",", fk.Properties.Select(p => p.Name))} -> {fk.PrincipalEntityType.DisplayName()}: {fk.DeleteBehavior}");
        var stale = ownedRows
            .Where(pair => !foreignKeys.Any(fk => fk.DeclaringEntityType.ClrType == pair.Dependent && fk.PrincipalEntityType.ClrType == pair.Principal))
            .Select(pair => $"{pair.Dependent.Name} -> {pair.Principal.Name} is listed but no such relationship exists");

        Assert.Empty(offending.Concat(stale));
    }

    [Fact]
    public void Every_tenant_row_has_a_TenantId_index()
    {
        var tenantRows = ModelUnderTest.TenantRows.ToList();

        var withoutIndex = tenantRows
            .Where(entityType => !entityType.GetIndexes().Any(index => index.Properties[0].Name == nameof(ITenantEntity.TenantId)))
            .Select(entityType => entityType.DisplayName());

        Assert.Contains(tenantRows, entityType => entityType.ClrType == typeof(OrganizationUnitUserRole));
        Assert.Empty(withoutIndex);
    }

    [Fact]
    public void Every_tenant_row_has_NoAction_audit_foreign_keys_to_users()
    {
        var missing = ModelUnderTest.TenantRows
            .SelectMany(entityType => auditForeignKeys.Select(column => (EntityType: entityType, Column: column)))
            .Where(pair => !pair.EntityType.GetForeignKeys().Any(fk =>
                fk.Properties.Count == 1
                && fk.Properties[0].Name == pair.Column
                && fk.PrincipalEntityType.ClrType == typeof(ApplicationUser)
                && fk.DeleteBehavior == DeleteBehavior.NoAction))
            .Select(pair => $"{pair.EntityType.DisplayName()}.{pair.Column}");

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_tenant_row_has_the_audit_columns()
    {
        string[] columns = [nameof(ITenantEntity.Created), nameof(ITenantEntity.CreatedById), nameof(ITenantEntity.Modified), nameof(ITenantEntity.ModifiedById), nameof(ITenantEntity.ImpersonatedById)];

        var missing = ModelUnderTest.TenantRows
            .SelectMany(entityType => columns.Where(column => entityType.FindProperty(column) is null).Select(column => $"{entityType.DisplayName()}.{column}"));

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_string_has_a_maximum_length_or_is_on_the_allow_list()
    {
        var strings = ModelUnderTest.Model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetDeclaredProperties())
            .Where(property => property.ClrType == typeof(string) || property.GetTypeMapping().Converter?.ProviderClrType == typeof(string))
            .ToList();

        var unbounded = strings
            .Where(property => IsUnbounded(property.GetColumnType()))
            .Where(property => !unboundedStrings.Contains((property.DeclaringType.ClrType, property.Name)))
            .Select(property => $"{property.DeclaringType.DisplayName()}.{property.Name} ({property.GetColumnType()})");
        var stale = unboundedStrings
            .Where(entry => !strings.Any(property => property.DeclaringType.ClrType == entry.Entity && property.Name == entry.Property && IsUnbounded(property.GetColumnType())))
            .Select(entry => $"{entry.Entity.Name}.{entry.Property} is allow-listed but is bounded or gone");

        Assert.Empty(unbounded.Concat(stale));
    }

    [Fact]
    public void Every_custom_field_definition_has_a_tenant_foreign_key_a_unique_name_per_scope_and_the_audit_columns()
    {
        var definitions = ModelUnderTest.Model.GetEntityTypes()
            .Where(entityType => typeof(Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition).IsAssignableFrom(entityType.ClrType))
            .ToList();
        Assert.Equal([typeof(ApplicationUserAttributeDefinition), typeof(Staffora.Core.Entities.EntityAttributes.JobAttributes.JobAttributeDefinition), typeof(OrganizationUnitAttributeDefinition)], definitions.Select(entityType => entityType.ClrType).OrderBy(type => type.Name));

        foreach (var definition in definitions)
        {
            var name = definition.DisplayName();
            Assert.Contains(definition.GetForeignKeys(), fk => fk.Properties is [{ Name: nameof(ITenantEntity.TenantId) }] && fk.PrincipalEntityType.ClrType == typeof(Tenant) && fk.DeleteBehavior == DeleteBehavior.NoAction);
            foreach (var column in auditForeignKeys)
            {
                Assert.True(definition.GetForeignKeys().Any(fk => fk.Properties is [{ } property] && property.Name == column && fk.PrincipalEntityType.ClrType == typeof(ApplicationUser) && fk.DeleteBehavior == DeleteBehavior.NoAction), $"{name}.{column}");
            }

            var unique = Assert.Single(definition.GetIndexes(), index => index.IsUnique);
            Assert.Equal([nameof(ITenantEntity.TenantId), "Name"], unique.Properties.Select(property => property.Name));
            Assert.Null(unique.GetFilter());
            Assert.Equal(PropertySaveBehavior.Throw, definition.FindProperty(nameof(ITenantEntity.TenantId))!.GetAfterSaveBehavior());
            Assert.All([nameof(ITenantEntity.Created), nameof(ITenantEntity.CreatedById), nameof(ITenantEntity.Modified), nameof(ITenantEntity.ModifiedById), nameof(ITenantEntity.ImpersonatedById)],
                column => Assert.NotNull(definition.FindProperty(column)));
        }
    }

    [Fact]
    public void Every_persisted_enum_declares_explicit_values()
    {
        var persistedEnums = ModelUnderTest.Model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetDeclaredProperties())
            .Select(property => Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType)
            .Where(type => type.IsEnum)
            .Distinct()
            .ToList();
        var declarations = EnumDeclarations();

        var offending = persistedEnums
            .SelectMany(type => declarations.TryGetValue(type.Name, out var body)
                ? MembersWithoutExplicitValue(body).Select(member => $"{type.Name}.{member}")
                : [$"{type.Name}: declaration not found under Staffora.Core or Staffora.Data"]);

        Assert.Contains(typeof(TenantState), persistedEnums);
        Assert.Contains(typeof(RolePermission), persistedEnums);
        Assert.Empty(offending);
    }

    [Fact]
    public void The_enum_check_detects_a_member_without_an_explicit_value()
    {
        const string body = """
            [Display(Name = "First, with a comma")]
            First = 0,
            // Second = 1 was removed
            Third,
            Fourth = 3
            """;

        Assert.Equal(["Third"], MembersWithoutExplicitValue(body));
    }

    [Fact]
    public void Every_DateTime_column_reads_back_as_UTC_through_the_converter()
    {
        var instant = new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc);
        DateTime[] written = [instant, DateTime.SpecifyKind(instant, DateTimeKind.Unspecified), instant.ToLocalTime()];
        var dateTimes = ModelUnderTest.Model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetDeclaredProperties())
            .Where(property => (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTime))
            .ToList();

        var offending = new List<string>();
        foreach (var property in dateTimes)
        {
            var converter = property.GetTypeMapping().Converter;
            if (converter is not UtcDateTimeConverter)
            {
                offending.Add($"{property.DeclaringType.DisplayName()}.{property.Name} has no UTC converter");
                continue;
            }

            foreach (var value in written)
            {
                var stored = (DateTime)converter.ConvertToProvider(value)!;
                var read = (DateTime)converter.ConvertFromProvider(DateTime.SpecifyKind(stored, DateTimeKind.Unspecified))!;

                if (read.Kind != DateTimeKind.Utc || read != instant)
                {
                    offending.Add($"{property.DeclaringType.DisplayName()}.{property.Name}: wrote {value:O} ({value.Kind}), read {read:O} ({read.Kind})");
                }
            }
        }

        Assert.Contains(dateTimes, property => property.ClrType == typeof(DateTime?));
        Assert.Empty(offending);
    }

    [Fact]
    public void Guid_keys_are_generated_by_EF()
    {
        var keys = ModelUnderTest.Model.GetEntityTypes()
            .Select(entityType => entityType.FindPrimaryKey())
            .Where(key => key is { Properties.Count: 1 } && key.Properties[0].ClrType == typeof(Guid) && !key.Properties[0].IsForeignKey())
            .Select(key => key!.Properties[0])
            .ToList();

        var notGenerated = keys
            .Where(property => property.ValueGenerated != ValueGenerated.OnAdd)
            .Select(property => $"{property.DeclaringType.DisplayName()}.{property.Name}: {property.ValueGenerated}");

        Assert.Contains(keys, property => property.DeclaringType.ClrType == typeof(Tenant));
        Assert.Empty(notGenerated);
    }

    private static bool IsUnbounded(string? columnType) =>
        columnType is null || columnType.Contains("(max)", StringComparison.OrdinalIgnoreCase) || columnType is "text" or "ntext";

    private static Dictionary<string, string> EnumDeclarations() =>
        RepoFiles.CommittedTextFiles(".cs")
            .Where(file => Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/') is var path
                && (path.StartsWith("Staffora.Core/", StringComparison.Ordinal) || path.StartsWith("Staffora.Data/", StringComparison.Ordinal)))
            .SelectMany(file => EnumDeclaration().Matches(File.ReadAllText(file)))
            .GroupBy(match => match.Groups["name"].Value)
            .ToDictionary(group => group.Key, group => group.Single().Groups["body"].Value);

    private static List<string> MembersWithoutExplicitValue(string body)
    {
        var code = Attribute().Replace(Comment().Replace(body, string.Empty), string.Empty);
        return code.Split(',')
            .Select(member => member.Trim())
            .Where(member => member.Length > 0 && !member.Contains('='))
            .ToList();
    }

    [GeneratedRegex(@"\benum\s+(?<name>\w+)\s*(?::\s*\w+\s*)?\{(?<body>[^}]*)\}")]
    private static partial Regex EnumDeclaration();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Attribute();
}
