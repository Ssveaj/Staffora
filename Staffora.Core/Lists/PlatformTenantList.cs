using Staffora.Core.Entities.Tenancy;

namespace Staffora.Core.Lists;

public static class PlatformTenantList
{
    public const string ByState = "state";

    public const string SortName = "name";
    public const string SortState = "state";
    public const string SortCreated = "created";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortState, "State"), new(SortCreated, "Created")],
        defaultSort: SortName,
        groups: [new(ByState, "State")]);

    private static readonly IReadOnlyList<ListOption> states = ListOption.ForEnum<TenantState>();

    public static string StateValue(TenantState state) => state.ToString().ToLowerInvariant();

    public static string StateLabel(TenantState state) => states.First(option => option.Value == StateValue(state)).Label;
}

public static class PlatformTenantRoleList
{
    public const string SortName = "name";
    public const string SortPermissions = "permissions";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortPermissions, "Permissions")],
        defaultSort: SortName);
}

public static class PlatformDeadLetterList
{
    public const string Direction = "direction";

    public const string Sent = "sent";
    public const string Received = "received";

    public const string SortGivenUp = "given-up";
    public const string SortKind = "kind";
    public const string SortWritten = "written";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortGivenUp, "Given up"), new(SortKind, "Kind"), new(SortWritten, "Written")],
        defaultSort: SortGivenUp,
        filters: [new ListFilterDefinition(Direction, "Direction", [new(Sent, "Sent by this tenant"), new(Received, "Meant for this tenant")])],
        defaultDescending: true);
}
