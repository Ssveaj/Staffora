using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Staffora.Test.Integration.Infrastructure;

public static class RenderedClicks
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public static void ClickOnce<TComponent>(IRenderedComponent<TComponent> rendered, Func<IElement> find)
        where TComponent : IComponent =>
        rendered.WaitForAssertion(() =>
        {
            try
            {
                find().Click();
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException exception)
            {
                Assert.Fail(exception.Message);
            }
        }, Wait);
}
