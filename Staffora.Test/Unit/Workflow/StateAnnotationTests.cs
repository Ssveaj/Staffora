using System.Reflection;
using System.Text.RegularExpressions;
using Staffora.Core;
using Staffora.Core.Annotations;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Workflow;

public partial class StateAnnotationTests
{
    private enum SampleState
    {
        [EntryState]
        [Icon("edit_note")]
        [StateStyling("#607D8B")]
        [ButtonStyle("Primary")]
        [EntityStateCategory(StateCategory.Entry)]
        Draft = 0,

        Plain = 1,

        [DeleteState]
        [EntityStateCategory(StateCategory.Canceled)]
        Deleted = 2,
    }

    private enum ConventionalState
    {
        Create = -1,
        Open = 0,
        Created = 1,
    }

    [Fact]
    public void Every_annotation_declares_its_own_AttributeUsage_for_enum_members_only()
    {
        var annotations = typeof(IconAttribute).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(IconAttribute).Namespace && typeof(Attribute).IsAssignableFrom(type))
            .ToList();

        Assert.Equal(
            ["ButtonStyleAttribute", "DeleteStateAttribute", "EntityStateCategoryAttribute", "EntryStateAttribute", "IconAttribute", "StateStylingAttribute"],
            annotations.Select(type => type.Name).Order(StringComparer.Ordinal));
        Assert.All(annotations, type =>
        {
            Assert.True(type.IsDefined(typeof(AttributeUsageAttribute), inherit: false), $"{type.Name} declares no [AttributeUsage] of its own.");
            var usage = type.GetCustomAttribute<AttributeUsageAttribute>()!;
            Assert.Equal(AttributeTargets.Field, usage.ValidOn);
            Assert.False(usage.AllowMultiple);
        });
    }

    [Fact]
    public void The_category_values_are_Entry_Open_Done_Canceled_and_Archived()
    {
        Assert.Equal(
            [StateCategory.Entry, StateCategory.Open, StateCategory.Done, StateCategory.Canceled, StateCategory.Archived],
            Enum.GetValues<StateCategory>());
    }

    [Fact]
    public void Each_reader_returns_the_annotated_value()
    {
        Assert.Equal("edit_note", SampleState.Draft.GetIcon());
        Assert.Equal("#607D8B", SampleState.Draft.GetStateColor());
        Assert.Equal("Primary", SampleState.Draft.GetButtonStyle());
        Assert.Equal(StateCategory.Entry, SampleState.Draft.GetStateCategory());
        Assert.Equal(StateCategory.Canceled, SampleState.Deleted.GetStateCategory());
    }

    [Fact]
    public void Each_reader_falls_back_on_a_member_without_its_annotation()
    {
        Assert.Equal(CoreExtensions.FallbackIcon, SampleState.Plain.GetIcon());
        Assert.Equal(CoreExtensions.FallbackStateColor, SampleState.Plain.GetStateColor());
        Assert.Equal(CoreExtensions.FallbackButtonStyle, SampleState.Plain.GetButtonStyle());
        Assert.Equal(CoreExtensions.FallbackStateCategory, SampleState.Plain.GetStateCategory());
        Assert.False(SampleState.Plain.IsDeleteState());
        Assert.False(SampleState.Plain.IsEntryState());
    }

    [Fact]
    public void Each_reader_falls_back_on_a_value_that_names_no_member()
    {
        var undefined = (SampleState)42;

        Assert.Equal(CoreExtensions.FallbackIcon, undefined.GetIcon());
        Assert.Equal(CoreExtensions.FallbackStateCategory, undefined.GetStateCategory());
        Assert.False(undefined.IsDeleteState());
        Assert.False(undefined.IsEntryState());
    }

    [Fact]
    public void Only_a_member_marked_DeleteState_is_a_delete_state()
    {
        Assert.Equal([SampleState.Deleted], Enum.GetValues<SampleState>().Where(state => state.IsDeleteState()));
    }

    [Fact]
    public void An_entry_state_is_marked_EntryState_or_named_Create()
    {
        Assert.Equal([SampleState.Draft], Enum.GetValues<SampleState>().Where(state => state.IsEntryState()));
        Assert.Equal([ConventionalState.Create], Enum.GetValues<ConventionalState>().Where(state => state.IsEntryState()));
    }

    [Fact]
    public void IsDeleteState_is_defined_once_in_the_production_code()
    {
        string[] projects = ["Staffora.Core/", "Staffora.Data/", "Staffora.Services/", "Staffora.Blazor/"];

        var definitions = RepoFiles.CommittedTextFiles(".cs", ".razor")
            .Select(file => (Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/'), File: file))
            .Where(source => projects.Any(project => source.Path.StartsWith(project, StringComparison.Ordinal)))
            .Where(source => IsDeleteStateDefinition().IsMatch(File.ReadAllText(source.File)))
            .Select(source => source.Path);

        Assert.Equal(["Staffora.Core/CoreExtensions.cs"], definitions);
    }

    [GeneratedRegex(@"\bbool\s+IsDeleteState\s*\(")]
    private static partial Regex IsDeleteStateDefinition();
}
