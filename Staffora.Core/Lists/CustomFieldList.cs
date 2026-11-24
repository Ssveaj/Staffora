using Staffora.Core.Entities.EntityAttributes;

namespace Staffora.Core.Lists;

public static class CustomFieldList
{
    public const string Entity = "entity";

    public const string Scope = "scope";

    public const string StateFilter = "state";

    public const string Draft = "draft";
    public const string Active = "active";
    public const string Inactive = "inactive";

    public const string User = "user";
    public const string Unit = "unit";
    public const string Job = "job";
    public const string Own = "own";
    public const string Global = "global";

    public const string ByEntity = "entity";

    public const string ByTenant = "tenant";

    public const string GlobalGroup = "global";

    public const string SortName = "name";
    public const string SortType = "type";

    private static readonly ListFilterDefinition entityFilter = new(Entity, "Applies to", [new(User, "Users"), new(Unit, "Organization units"), new(Job, "Jobs")]);

    private static readonly ListFilterDefinition stateFilter = new(StateFilter, "State", [new(Draft, "Draft"), new(Active, "Active"), new(Inactive, "Inactive")]);

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortType, "Type")],
        defaultSort: SortName,
        filters:
        [
            entityFilter,
            new ListFilterDefinition(Scope, "Scope", [new(Own, "Own"), new(Global, "Global")]),
        ],
        groups: [new(ByEntity, "Applies to")],
        defaultGroup: ByEntity);

    public static ListDefinition PlatformDefinition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortType, "Type")],
        defaultSort: SortName,
        filters:
        [
            entityFilter,
            new ListFilterDefinition(Scope, "Scope", [new(Global, "Global"), new(Own, "For a tenant")]),
            stateFilter,
        ],
        groups: [new(ByTenant, "Tenant"), new(ByEntity, "Applies to")],
        defaultGroup: ByTenant);

    public static ListDefinition ConsoleDefinition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortType, "Type")],
        defaultSort: SortName,
        filters:
        [
            entityFilter,
            new ListFilterDefinition(Scope, "Scope", [new(Own, "Own"), new(Global, "Global")]),
            stateFilter,
        ],
        groups: [new(ByEntity, "Applies to")],
        defaultGroup: ByEntity);

    public static ListDefinition For(bool platform) => platform ? PlatformDefinition : Definition;

    public static string ValueOf(CustomFieldTarget target) => target switch
    {
        CustomFieldTarget.User => User,
        CustomFieldTarget.Job => Job,
        _ => Unit,
    };
}
