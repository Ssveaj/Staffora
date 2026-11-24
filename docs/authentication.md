# Authentication and sessions

How people sign in, how a session is carried on the auth cookie and checked again, and how sign-in is hardened. What a signed-in user may do is in [authorization.md](authorization.md); what each tenant state means for access is in [Tenant states and access](authorization.md#tenant-states-and-access). Session state follows [ADR 0014](adr/0014-session-on-the-auth-cookie-audited-impersonation.md); there are no self-service account pages ([ADR 0034](adr/0034-no-self-service-account-pages.md)).

## Signing in

- **Unique names and addresses.** User names and email addresses are unique across every tenant, because sign-in finds the user by either before any tenant is known. Identity's validator requires it (`RequireUniqueEmail`) and a unique index on `NormalizedEmail` holds it (a system account has no address).
- **One answer for a taken name.** `UserService` answers a name or address another user holds, in any tenant, with one message ("That user name or email address cannot be used."), so it confirms nothing about another tenant. Accepted risk: since names are global, the message still tells an administrator that a name is held somewhere on the platform, though not where or by whom.
- **Trimming.** `UserService` trims user names and addresses on create and update before Identity's rules see them. Should two users share an address anyway, the lookup by address finds nobody rather than failing, and the sign-in page decides where to go from the user the sign-in found, never from what was typed.
- **Who is refused.** `ApplicationSignInManager.CanSignInAsync` refuses a deactivated user (any state but Active), a system account, and a user whose tenant does not admit them (`TenantStates.AdmitsUser`: an Active tenant admits its users, a Setup tenant only its active system owners, a Suspended, Pending deletion or Archived tenant nobody). It judges the state the database holds, so a copy Identity loaded just before a deactivation signs nobody in. A Setup tenant's active system owners are read in the same query as the tenant's state ([System owners](authorization.md#system-owners)).
- **Refusal after the password.** A refused user is answered as any account until the password is right (`ApplicationSignInManager.CheckPasswordSignInAsync`): a wrong password costs a password verification and counts towards the lockout, a locked account goes to the lockout page, and only the right password is told why, never with a cookie: "Your organization's account is not active, so nobody in it can sign in." or "Your account is not active, so you cannot sign in." (`SignInRefusal`). Identity's own check would refuse before the password and tell anyone who typed an address that its account or tenant is shut. A system account is refused without a reason.
- **One place issues the cookie.** Every sign-in path ends in `ApplicationSignInManager.SignInWithClaimsAsync`, which throws for anyone `CanSignInAsync` refuses, so no path issues a cookie for a refused user.
- **A password is the one way in.** There is no passkey, two-factor or external sign-in. The sign-in manager answers every user as having no second factor (`ApplicationSignInManager.IsTwoFactorEnabledAsync`), so a row that still has two-factor sign-in on signs in by its password alone.
- **Cookies of refused users.** The same manager fails the security-stamp check of such a user's cookie, which every request runs ([Every request](#every-request)); the session gate refuses them and any cookie whose security stamp is no longer the user's when a circuit starts. A cookie issued before a refusal stops working at its next request or circuit, and an open circuit is signed out at its next revalidation, at most five minutes later.
- **Stamps.** Deactivating a user and giving them a new password (the user workflow's Deactivate and New password steps) rotate the security stamp, so a cookie issued before stays refused after a reactivation. Nobody deactivates their own account, and a system account is never moved. Every write of a user row renews its concurrency stamp (`ApplicationDbContext`), so a copy Identity read before a step is refused with Identity's concurrency failure instead of writing the hash, the stamp or the lockout back over the step's. A failed sign-in on such a copy is still counted, since the count is one statement on the stored row ([Lockout](#lockout)).
- **Lockout and rate limit.** Five wrong passwords in a row lock the account for fifteen minutes, and sign-in posts are rate limited ([Sign-in hardening and security headers](#sign-in-hardening-and-security-headers)).

## First platform owner

In Development the seeder creates the platform tenant and a platform owner with credentials written in the source. No other environment ever gets them: outside Development the seeder writes only the `Admin` role. A development database whose platform already has a platform owner gets no seeded one beside it.

A new database elsewhere is bootstrapped once from the command line:

```text
dotnet run --project Staffora.Blazor -- bootstrap-platform-owner <email>
```

Arguments after the email, such as `--environment Production`, configure the host as usual. The command (`Staffora.Data/Seed/PlatformBootstrap.cs`):

- checks the address before it touches the database;
- migrates the database, creates the platform tenant (Active from the start, never in Setup) with its system account, and creates the first platform owner with that email and the `Admin` role;
- refuses before writing a row when a platform owner exists, so a second run changes nothing; further owners are a platform owner's work;
- is serialized by a SQL Server application lock held from the check to the last write, so of runs started together exactly one creates the owner; a unique index allows one platform tenant whatever writes it.

**The initial password** is a fresh random one of 24 characters, written once to the standard output of the console that ran the command and nowhere else: not through the logger (whose providers may write files), not to any table in plain text, and not again on a second run. Whoever runs the command passes it on without storing it. No page changes it; `reset-platform-owner` issues another when it is lost.

**An interrupted run.** If a run stops after it created the owner but before the password reached the console, nobody can sign in and a second run refuses. Nobody has signed in to that database yet, so drop it (`dotnet ef database drop --force --project Staffora.Data -- --connection "<connection string>"`) and run the bootstrap again. Never do this on a database a platform owner has used: there, use `reset-platform-owner`.

A platform owner locked out by wrong passwords, with no other owner to unlock them, runs `unlock-user <email>` the same way ([Lifting a lockout](#lifting-a-lockout)).

## A platform owner who lost their password

No session changes a platform owner's credentials, lockout or access: the grant ceiling refuses a platform owner as the target of New password, Deactivate, Reactivate and Unlock from every session, the platform console included ([Rules](authorization.md#rules), "Grant ceiling"). So one platform owner never resets another; the server's operator does:

```text
dotnet run --project Staffora.Blazor -- reset-platform-owner <email>
```

- **Checks.** `PlatformOwnerResetCommand` checks the address before it builds the host and migrates nothing. It refuses, writing nothing, an address nobody signs in with and any user who is not a platform owner (the `Admin` role **and** the platform tenant: a tenant's user, an `Admin` of another tenant, a platform-tenant member without `Admin`).
- **The reset.** It is the user workflow's account recovery reset (the New password step's `UserPasswordIssuer`): a fresh random password of 16 characters, a new security stamp (every earlier cookie is refused at its next request), the lockout and failed count lifted, two-factor sign-in switched off and the owner's Identity tokens removed, so the new password is the one way in.
- **History.** It is saved in one transaction with an entry in the owner's history: the New password step, taken by the platform tenant's system account. The command builds a runtime for that account only after Identity says the address is an `Admin`'s and it has checked that it is the platform tenant's (allow-listed in `DataContextScanTests`).
- **The password** is written once to the standard output of the console, never through the logger (whose warning names the owner by id) and never stored in clear. A second run issues another and ends the sessions of the first.

**Exit codes** of `reset-platform-owner` and `unlock-user` (`Staffora.Blazor/Helper/ServerCommands.cs`):

| Code | Meaning |
|---|---|
| 0 | The command did its work |
| 1 | A refusal, or any failure (a database that does not answer, a row changed meanwhile): one line on standard error naming neither the address nor a password, no stack trace |
| 2 | The argument is not one email address |

The commands' classes act outside any session, so they are registered only when one of them runs, never in the web app's container, and no other file of the web app registers, resolves or injects them.

Verified by: `PlatformOwnerResetCommandTests`, `ServerCommandScanTests`.

## Session

Everything a session needs before its first render is a claim on the auth cookie, set only by POST endpoints that re-issue the cookie. Nothing is kept in browser storage.

### Resolution

`UserSessionService.InitializeFromClaimsAsync` resolves the session once per circuit, single-flight: concurrent callers share one run and query the database once. The session gate (`Staffora.Blazor/Components/Account/SessionGate.razor`), hosted by both layouts around everything they render, starts it; the revalidation waits for it before its first pass. In order:

1. Read the claims from the circuit's authentication state.
2. Load the signed-in user. A system account, a deactivated user, a user whose tenant does not admit them, or a cookie that does not carry the user's current security stamp opens no session, so a new password or a deactivation ends every earlier cookie at the next circuit.
3. Load the user's own tenant, whether it is the platform tenant, and whether the user holds `Admin`.
4. When an impersonation claim is present, re-authorize it (authorizer and open audit row); a refused one opens no session (`ImpersonationEnded`). See [Impersonation](authorization.md#impersonation).
5. Determine the effective user and tenant.
6. Load the effective user's Identity roles.
7. Prime the permission cache for the effective user and tenant (`PermissionService.PrimeAsync`).
8. Apply everything to the runtime in one step (`IRuntimeExecutionContext.ApplySession`, which replaces the runtime's whole snapshot), then mark the session ready (`IsReady`) and notify once (`OnChanged`).

Ready means resolved, not signed in: a session that resolves to nobody, a refused user or an ended impersonation is ready too, with no user, so the sign-in redirect and the anonymous pages render. A resolution that fails is no session (fail closed).

### One snapshot

The runtime holds one immutable `RuntimeSnapshot`, replaced as a whole by every change. Each runtime property reads it once, but two property reads may fall on either side of a session the revalidation applies on another thread. A decision that needs two or more facts therefore reads `runtime.Current` once and takes them all from it: platform reach, the permission check and its cache key, the save's tenant checks and stamping (creator and impersonator from the same session), Identity's user store, the impersonation dialog's owner check, and the navigation (`PermissionService.GetUserPermissionsAsync(RuntimeSnapshot)`).

Verified by: `SessionStateScanTests` (refuses a production statement that reads two runtime properties one by one), `RuntimeSnapshotTests`.

### Ready gate

Interactive pages render in interactive server mode **without** prerendering (`App.razor`): the server's HTML holds only a boot loader that hides once the circuit renders. The sign-in page and its system pages, excluded from interactive routing, stay static. The session gate renders only a loader until the session is ready, then the layout with its page and a hidden ready landmark (`data-session="ready"`) that browser journeys wait for, so no page, menu or service call runs with a half-resolved identity. Session changes may be raised off the circuit's dispatcher, so every subscriber (the gate, `AccountMenu`, `NavMenu`, `PermissionView`) re-renders through `InvokeAsync`.

### Permissions

The permission set is cached per (tenant, effective user) with a five-minute time-to-live on the injected clock, primed by the resolution and dropped at once when the current user edits their own roles ([Resolution](authorization.md#resolution)).

### Revalidation

Every five minutes while a circuit is connected (`IdentityRevalidatingAuthenticationStateProvider.Interval`), the circuit checks its cookie with the app's sign-in manager (security stamp; a system account, a deactivated user or a user whose tenant left Active fails it) and resolves its session again from the database (`UserSessionService.RevalidateAsync`), applying what changed in one step:

- a user who fails either check is signed out of the circuit: its session is cleared (ready, no user) and the router sends the browser to sign in;
- a platform owner who lost the `Admin` role or platform-tenant membership loses platform reach (the console's services refuse, and the permission bypass ends);
- an impersonation whose audit row was closed (ended in another tab) or whose owner lost reach ends on the circuit, which then reads and writes nothing and offers the end button; a refused target, or a platform owner whose own session is refused, also has that impersonation's row closed when the end can be recorded (a failed write is logged as a warning, the row stays open, and the session is refused all the same).

Until then an open circuit keeps what its session resolved, while the next circuit is refused at once. So no circuit acts on an ended session, or stamps a row with an impersonator after its audit row ended, for longer than one interval. A pass that fails (the database cannot be read) signs the circuit out the same way. The security-stamp refresh and `RefreshSignInAsync` carry the session claims over, so impersonation survives them. Deactivating a user, giving them a new password, and a tenant's Suspend and Schedule deletion (from Active) rotate security stamps ([Tenant states and access](authorization.md#tenant-states-and-access)).

### Every request

A request that carries the auth cookie is checked against the user row before anything else (`RequestStampCheck`, run first in the cookie's `OnValidatePrincipal`, in a scope of its own).

- **The check.** The sign-in manager must find the user with the stamp the cookie carries and still admit them (no system account, an active user of a tenant that admits them), read in one query of the user's stamp, state and system-account flag and their tenant's state (`ApplicationSignInManager.AdmitsCookieAsync`, `ApplicationUserStore.FindCookieFactsAsync`). A cookie without a stamp never stands.
- **Refusal.** A cookie that fails is rejected and signed out at that request (the response deletes it), so a static page, which opens no circuit, refuses it at once rather than at Identity's periodic check up to half an hour later.
- **Static assets** (files `MapStaticAssets` serves, whose endpoint carries a `StaticAssetDescriptor`: stylesheets, scripts, fonts, images, `/_framework`, `/_content`) hold no data and are not checked. Every other request is, an unknown address included.
- **Database outage.** When the check cannot read the database, the cookie is rejected for that request but not deleted, the error is logged at Error level (so it reaches the error log), and the request is answered 503 with the status page (`UseRequestStampOutage`).
- **Only refuses.** Identity's own validator still re-issues the cookie on its interval. Its interval is not set to zero: that re-issues the cookie on every request and reads the user into the request's Identity context, which turns a page's redirect after a form post into a 400.
- **Own stamp.** No page changes one's own account, so nothing rotates the signed-in user's own stamp. A stamp changes only by a user workflow step another person takes (New password, Deactivate) or a tenant's suspension, and every cookie issued before is refused at its next request.

### Sign-out

A form post to `/Account/Logout` with its antiforgery token (`AccountMenu`'s logout item). A cookie that carries an impersonation first has that impersonation's own audit row closed (`ImpersonationService.EndOnSignOutAsync`, by the end button's rules: only a row that is open and records that owner and target; a failure to write is logged and the sign-out goes on). Then the auth cookie is cleared, and with it every session claim.

## Sign-in hardening and security headers

The settings live in `Staffora.Blazor/Program.cs` and `Staffora.Blazor/Helper/`.

### Lockout

- The sign-in page counts every wrong password (`lockoutOnFailure: true`). There is no second factor whose codes could be guessed.
- Identity's lockout options: allowed for every new user, five failures in a row, fifteen minutes. The fifth failure sends the browser to `/Account/Lockout`, and so does any attempt while locked, the right password included. A success starts the count again.
- Every failure is counted in the database, a wrong password for a deactivated user or a user of a tenant that is not Active included. `ApplicationUserManager.AccessFailedAsync` has the user store add one to the stored count in one `UPDATE` (`ApplicationUserStore.CountFailedAccessAsync`), which at the fifth sets the lockout end from the injected clock and restarts the count. The statement does not depend on the concurrency stamp, so wrong passwords tried at once are each counted; saving the whole row under a read stamp would count only one of many concurrent tries.
- It leaves the concurrency stamp alone, so typing wrong passwords cannot make a workflow step on the user fail.
- It is one of the two bulk updates of a user (the other rotates the stamps of a tenant's users on suspension or scheduled deletion, [Tenant states and access](authorization.md#tenant-states-and-access)), allow-listed in `DataContextScanTests` and judged by `BulkUpdateGuard` like any other.
- The failure count and lockout end are saved from any session, stamped with the user's tenant's system account (`ApplicationUserStore.SignInBookkeepingColumns`, [Data access](authorization.md#data-access)).

### Lifting a lockout

The lockout is per account, so anyone who knows an address can keep that account locked by trying five wrong passwords every fifteen minutes. This is accepted: it is Identity's standard behaviour, the alternative (no lockout) lets passwords be guessed, the rate limit per client address slows it, and the lockout can always be lifted:

- by the user workflow's **Unlock** step, offered only while the user is locked out, to a holder of `UsersEdit` in the user's own tenant or from the platform console (written on the tenant's behalf). It takes no edits (`TakesNoEdits`), so it changes nothing else;
- by the **New password** step, which lifts the lockout and clears the count too, in the user's own tenant or from the platform console, where the platform owner also corrects a user's name, email address and user name through Save, every other field refused ([ADR 0030](adr/0030-platform-owner-corrects-a-users-sign-in-details.md));
- for a platform owner with nobody to unlock them, by `dotnet run --project Staffora.Blazor -- unlock-user <email>` on the server (`AccountUnlockCommand`). It clears the lockout end and failed count of that account and nothing else, migrates nothing, and, since it needs the server and its configuration, says whether the address exists. A lost platform-owner password: [A platform owner who lost their password](#a-platform-owner-who-lost-their-password).

Both steps are refused for a user of another tenant (not found) and, by the grant ceiling, for a user who holds more than the session and for a platform owner from every session ([Rules](authorization.md#rules), "Grant ceiling"). The command's lift is sign-in bookkeeping, stamped with the user's tenant's system account; a step's is stamped with the user who took it (the tenant's system account from the platform console) and recorded in the user's history.

### Which addresses exist

The sign-in page answers a wrong password and an unknown address with the same message and at the same cost: for an unknown address `ApplicationSignInManager.PasswordSignInAsync` verifies the typed password against a hash of a random password. One signal remains and is accepted, as in Identity's standard flow: a locked account answers with the lockout page, which an unknown address never does, so five wrong tries tell that the address exists. The rate limit per client address bounds how many addresses one client can probe.

### Cookies and keys

- **Auth cookie.** Set with `ConfigureApplicationCookie`: `HttpOnly`, `Secure` (always; browsers treat `localhost` as secure, so local runs over HTTP keep it), `SameSite=Lax`, an eight-hour lifetime with sliding expiration, renewed once half of it has passed.
- **Data Protection keys.** The auth and antiforgery cookies are protected by ASP.NET Core Data Protection. By default the keys live where the framework keeps them for the user the app runs as. Two instances would need a shared key store.
- **Antiforgery cookie.** Marked `Secure` as well: always outside Development, where every request is HTTPS and a plain-HTTP request fails visibly; in Development on every HTTPS request, since local runs and browser journeys use `http://localhost`.

### Pages need a signed-in user

A convention in `Program.cs` adds `[Authorize]` to every Razor component endpoint, so a page that names no policy of its own still needs a signed-in user and an anonymous request is sent to sign in. Pages open before sign-in say so with `[AllowAnonymous]`: `/Account/Login`, `/Account/Lockout`, `/Account/AccessDenied`, `/not-found`, `/status/{code}` and `/Error`.

The circuit's hub (`/_blazor`) is open, since the anonymous not-found page opens a circuit too; inside the circuit `AuthorizeRouteView` checks every page again. `UseAuthorization()` runs after the status-code middleware, so a page it re-executes is authorized like any other. Which policy each page opens by: [SuperAdmin (platform owner)](authorization.md#superadmin-platform-owner).

### Status pages

- An error status without a body (404 for an unknown address, 429 from the rate limiter, 400 from a refused form post, any other) is re-executed as `/status/{code}` with the status kept (`Pages/StatusPage.razor`): "Not found" for 404, "Too many attempts — try again in a minute" for 429, a generic message otherwise.
- It renders on the server, not in a circuit, inside `MainLayout`: with the chrome and navigation of a signed-in user, in the plain frame for an anonymous one. A re-executed pass does not wait for the session gate, so `ReExecutedPageSession` resolves the request's session before the page renders (the unhandled-exception page gets the same).
- The re-executed pass is a GET whatever the failed request was (`ReExecutedPageSession` sets the method): a post refused for its antiforgery token is not checked again, and a post without a form body that the rate limiter refuses gets its status page instead of a server error.
- A post without a valid antiforgery token is answered 400 with that page on every kind of endpoint and leaves no error-log row (`RefusedRequests`, inside the status-code re-execution).

  It catches `AntiforgeryValidationException` (from an endpoint that calls `ValidateRequestAsync` itself) and `BadHttpRequestException` (a request the framework could not read or bind) while nothing is sent; a component page's form refused by Blazor, a 400 with no body but its page content type, has the content type cleared so the page applies (in Development Blazor writes its own explanation instead). The refusal is logged as a warning, which the error log does not store; any other exception still reaches the exception handler and the error log.
- Navigating to an unknown address inside a circuit shows `/not-found` in the same layout.

### E-mail flows

There is no e-mail channel, so no e-mail sender is registered and nothing can report a message as sent. The pages that would send one are not part of the app: forgot and reset password with their confirmation pages, e-mail confirmation and resending it, and changing one's address (`/Account/Manage/Email`); their addresses answer 404, as every other Identity self-service address does. Passwords are given instead:

- a user administrator of the user's own tenant (`UsersEdit`) gives a colleague a new password through the New password step, shown once: an account recovery reset that also removes the colleague's Identity tokens and switches two-factor sign-in off, within the grant ceiling and never for a platform owner;
- the platform owner gives a tenant's user one from the console ([ADR 0030](adr/0030-platform-owner-corrects-a-users-sign-in-details.md));
- only a system owner gives their own account one (and changes their own details);
- a platform owner's own password is reset only by `reset-platform-owner`.

The e-mail channel and password reset by e-mail are planned ([roadmap.md](roadmap.md)).

### Rate limits

ASP.NET Core's rate limiter with fixed windows, refusing at once with 429 and a `Retry-After` header (`RateLimitPolicies`):

| Policy | Applies to | Limit |
|---|---|---|
| `sign-in` | The sign-in page's form posts (`[EnableRateLimiting]`); viewing the page is not limited | 10 per minute per client address |
| `impersonation` | The impersonation start and end endpoints | 20 per minute per signed-in user |

The numbers come from the `RateLimits` configuration section (`RateLimitOptions`: `SignIn` and `Impersonation`, each a `PermitLimit` and a `Window`), so a test host can set them; production runs with the defaults. The end-to-end server raises the sign-in limit for its own app process (`scripts/e2e-server.mjs`), since the journeys sign in many times a minute from one address.

### Client address behind a proxy

The sign-in limit counts per client address, so the app takes the address from `X-Forwarded-For` (and the scheme from `X-Forwarded-Proto`) only when it comes from a proxy named in the `ForwardedHeaders` configuration section: `KnownProxies` (addresses) and `KnownNetworks` (CIDR ranges), both empty by default, which trusts no proxy and ignores the headers (`ProxyHeaders`, `UseForwardedHeaders` first in the pipeline, one proxy hop).

ASP.NET Core's switch `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` would trust the headers from anyone, letting one client spread its attempts over invented addresses; the app's post-configuration puts back exactly the configured proxies, so the switch alone trusts nobody.

**Deploying behind a reverse proxy or load balancer, configure it**, for example `ForwardedHeaders__KnownProxies__0=10.0.0.4` or `ForwardedHeaders__KnownNetworks__0=10.0.0.0/24`. Without it every client shares the proxy's address and one limit, and a proxy that terminates TLS makes requests look like plain HTTP, which the antiforgery cookie refuses outside Development.

### Security headers

Every response carries `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin` and a content security policy (`SecurityHeaders`, set before the rest of the pipeline and again for a re-executed error page):

```text
default-src 'self'; script-src 'self' 'nonce-<per response>'; script-src-elem 'self' 'nonce-<per response>';
script-src-attr 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self';
connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
```

- **Scripts.** Script elements come only from the app's origin; the page's one inline script, the import map, carries the response's nonce (`App.razor`).
- **Inline event-handler attributes** are allowed (`script-src-attr 'unsafe-inline'`) because Radzen renders its own, some with a per-render id (a drop-down opens with `onclick="Radzen.togglePopup(this.parentNode, '<popup id>')"`, a numeric field filters keys with `onkeypress`, a form field marks focus with `onfocus`), which no hash can name; a strict policy would leave drop-downs, grid filters and menus dead. This allows attributes only: a script element and a `javascript:` address still need the origin or the nonce, and Blazor encodes every value it renders, so data cannot add an attribute (a `MarkupString` of user data could, and none is rendered).
- **Styles** allow `'unsafe-inline'` because Blazor and Radzen render `style` attributes.
- **Other sources.** The circuit's WebSocket goes to the same origin. Fonts are local (the icon font ships with the Radzen package), images local or `data:` (the SVGs in the stylesheets); avatars are an icon, not a remote picture.
- **Framing.** `frame-ancestors 'none'` forbids framing the app anywhere, so Blazor's own `frame-ancestors 'self'` header is switched off (`ContentSecurityFrameAncestorsPolicy = null`).
- **Development.** The policy holds in Development too, so a development tool that reaches another origin from the page (the browser refresh of `dotnet watch`, which connects to its own port) is blocked by it.

The browser journeys fail on any console error, a policy violation included (`page-health`), so the smoke specs prove the policy on the sign-in page, the platform console and a tenant page.

Verified by: `SignInHardeningTests` (lockout, rate limits, status pages and refused posts in the Test and Development environments), `TenantLifecycleSignInTests`.

## References

- `Staffora.Blazor/Program.cs`, `Staffora.Blazor/Helper/SecurityHeaders.cs`, `RateLimits.cs`, `ProxyHeaders.cs`, `ReExecutedPageSession.cs`, `ServerCommands.cs`; `Staffora.Blazor/Pages/StatusPage.razor`
- `Staffora.Services/Authorization/UserSessionService.cs`, `ApplicationSignInManager.cs`, `ApplicationClaimsPrincipalFactory.cs`, `AccountUnlockCommand.cs`
- `Staffora.Data/Seed/PlatformBootstrap.cs`, `Staffora.Data/ApplicationUserStore.cs`
- `Staffora.Core/Execution/RuntimeExecutionContext.cs`, `RuntimeSnapshot.cs`
- `Staffora.Blazor/Components/Account/SessionGate.razor`, `AccountMenu.razor`, `IdentityRevalidatingAuthenticationStateProvider.cs`, `Staffora.Blazor/Components/App.razor`
- Deviations from these rules: [known-issues.md](known-issues.md)
