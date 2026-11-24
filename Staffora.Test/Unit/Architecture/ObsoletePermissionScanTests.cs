using System.Text.RegularExpressions;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities.Tenancy;
using Staffora.Services.Authorization;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class ObsoletePermissionScanTests
{
    private const string Declaration = "Staffora.Core/Entities/Tenancy/RolePermission.cs";

    private static readonly string[] projects = ["Staffora.Core", "Staffora.Data", "Staffora.Services", "Staffora.Blazor", "Staffora.E2ESetup", "Staffora.Test"];

    private static readonly RolePermission[] obsolete = [.. Enum.GetValues<RolePermission>().Where(RolePermissions.IsObsolete)];

    [Fact]
    public void The_four_Organizations_permissions_are_obsolete_and_point_to_the_unit_permissions()
    {
        Assert.Equal(["OrganizationsView", "OrganizationsCreate", "OrganizationsEdit", "OrganizationsDelete"], obsolete.Select(permission => permission.ToString()));
        Assert.Equal([10, 11, 12, 13], obsolete.Select(permission => (int)permission));
        Assert.All(obsolete, permission =>
        {
            var message = typeof(RolePermission).GetField(permission.ToString())!.GetCustomAttributes(typeof(ObsoleteAttribute), false).Cast<ObsoleteAttribute>().Single().Message;
            Assert.Contains("OrganizationUnits" + permission.ToString()["Organizations".Length..], message);
        });
    }

    [Fact]
    public void No_source_names_an_obsolete_permission_outside_its_declaration()
    {
        var offenders = Sources()
            .Where(source => source.Path != Declaration)
            .SelectMany(source => Uses(source.Text).Select(use => $"{source.Path}: {use}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scan_catches_a_check_a_list_and_a_razor_attribute()
    {
        var name = obsolete[0].ToString();
        var sample = $"""
            await permissionService.HasPermissionAsync(RolePermission.{name});
            RolePermission[] gate = [RolePermission.UsersView, RolePermission .{name}];
            <PermissionView Permission="RolePermission.{name}">
            RolePermission.OrganizationUnitsView
            """;

        Assert.Equal([$"RolePermission.{name}", $"RolePermission .{name}", $"RolePermission.{name}"], Uses(sample));
    }

    [Fact]
    public void No_role_is_offered_an_obsolete_permission()
    {
        Assert.DoesNotContain(RolePermission.None, RolePermissions.Grantable);
        Assert.Empty(obsolete.Intersect(RolePermissions.Grantable));
        Assert.Equal(Enum.GetValues<RolePermission>().Length - 1 - obsolete.Length, RolePermissions.Grantable.Count);

        Assert.Empty(obsolete.Intersect(EnumDisplayHelper.GetPermissionsByArea().SelectMany(group => group)));
        Assert.Empty(obsolete.Select(permission => permission.ToString()).Intersect(PermissionService.GetGroupedPermissions().SelectMany(group => group.Value).Select(entry => entry.Permission.ToString())));
        Assert.Empty(PermissionService.GetPermissionsByArea(PermissionArea.Organizations));
    }

    private static List<string> Uses(string text) =>
        [.. ObsoleteUse().Matches(text).Select(match => match.Value).Where(use => obsolete.Any(permission => use.EndsWith("." + permission, StringComparison.Ordinal)))];

    private static IEnumerable<(string Path, string Text)> Sources() =>
        RepoFiles.CommittedTextFiles(".cs", ".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => projects.Any(project => source.Path.StartsWith(project + "/", StringComparison.Ordinal)))
            .Select(source => (source.Path, File.ReadAllText(source.File)));

    [GeneratedRegex(@"\bRolePermission\s*\.\s*Organizations\w+\b")]
    private static partial Regex ObsoleteUse();
}
