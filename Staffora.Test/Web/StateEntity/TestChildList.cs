using Staffora.Core.Lists;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Web.StateEntity;

public static class TestChildList
{
    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(sorts: [new(SortName, "Name")], defaultSort: SortName, pageSize: 10);
}

public sealed record TestChildRow(Guid Id, string Name, TestChildState State, bool IsPinned);
