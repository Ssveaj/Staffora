using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Staffora.Test.Integration.Web;

internal static class DialogEditing
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public static IElement? EditButton(this IRenderedComponent<IComponent> rendered) =>
        rendered.FindAll(".state-entity-dialog-footer button[data-dialog-action='edit']").LastOrDefault();

    public static void Edit(this IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.NotNull(rendered.EditButton()), Wait);
        rendered.InvokeAsync(() => rendered.EditButton()!.Click()).GetAwaiter().GetResult();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-entity-dialog-footer button[data-dialog-action='cancel']")), Wait);
    }
}
