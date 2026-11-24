using Microsoft.AspNetCore.Components;

namespace Staffora.Blazor.Components.Account
{
    internal sealed class IdentityRedirectManager
    {
        private readonly NavigationManager navigationManager;

        public IdentityRedirectManager(NavigationManager navigationManager)
        {
            this.navigationManager = navigationManager;
        }

        public void RedirectTo(string? uri)
        {
            uri ??= "";

            if (!Uri.IsWellFormedUriString(uri, UriKind.Relative))
            {
                uri = this.navigationManager.ToBaseRelativePath(uri);
            }

            this.navigationManager.NavigateTo(uri);
        }
    }
}
