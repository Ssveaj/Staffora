using System.Text.RegularExpressions;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Services.Workflow;
using Staffora.Test.Unit.Architecture.ServiceScanSamples;

namespace Staffora.Test.Unit.Architecture;

public partial class ServicePassThroughScanTests
{
    private static readonly string[] checks = [.. ServicePermissionScanTests.PermissionChecks, "HasPlatformReach()"];

    private static readonly Dictionary<(string Service, string Method), string> allowList = new()
    {
        [("TenantSettingsEnsureService", "ForgetCachedSettings")] = "I-29 (kept): names the settings catalogue's cache entry, so the tenant creates (TenantService, CustomerService's portal) drop it after their commit without knowing the cache's keys; it reads and grants nothing",
    };

    [Fact]
    public void Every_pass_through_is_allow_listed_with_the_value_it_adds()
    {
        var offenders = ServiceSources.AllServices()
            .SelectMany(PassThroughs)
            .Where(hit => !allowList.ContainsKey((hit.Service, hit.Method)))
            .Select(hit => $"{hit.Service}.{hit.Method} ({hit.Kind} to {hit.Target})")
            .ToList();

        Assert.True(offenders.Count == 0, $"Public service methods that only hand the call on: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Every_allow_list_entry_is_a_pass_through_and_names_its_value()
    {
        var current = ServiceSources.AllServices()
            .SelectMany(PassThroughs)
            .Select(hit => (hit.Service, hit.Method))
            .ToHashSet();

        var stale = allowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !current.Contains(entry.Key))
            .Select(entry => $"{entry.Key.Service}.{entry.Key.Method}")
            .ToList();

        Assert.True(stale.Count == 0, $"Allow-list entries that are no pass-through, or name no value: {string.Join(", ", stale)}");
    }

    [Fact]
    public void The_scan_catches_a_forward_and_a_double_gate_and_passes_a_read_gate_and_real_work()
    {
        const string sample = """
            public sealed class SampleService
            {
                public Task<Result<StateHistoryPage>> ForwardAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
                    this.history.GetHistoryAsync<JobSlot>(id, query, cancellationToken);

                public async Task<Result<ListPage<AbsenceRow>>> ForwardAfterAGuardAsync(Guid? userId, ListQuery query, CancellationToken cancellationToken)
                {
                    // permissionService.HasPermissionAsync in a comment is no check.
                    if (userId is not { } id)
                        return Result<ListPage<AbsenceRow>>.Failure("No user.");

                    return await absences.QueryAbsencesOfUserAsync(id, query, cancellationToken).ConfigureAwait(false);
                }

                public async Task<Result<ListPage<AbsenceRow>>> DoubleGateAsync(Guid userId, ListQuery query, CancellationToken cancellationToken)
                {
                    if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewResourceAbsence, cancellationToken))
                    {
                        return Result<ListPage<AbsenceRow>>.Failure(NoView);
                    }

                    return await this.absences.QueryAbsencesOfUserAsync(userId, query, cancellationToken);
                }

                public async Task<Result<ListPage<SlotAssignmentRow>>> DoubleGateThroughAHelperAsync(Guid slotId, ListQuery query, CancellationToken cancellationToken) =>
                    await this.MayReadAsync(cancellationToken)
                        ? await this.assignments.QuerySlotAssignmentsAsync(slotId, query, cancellationToken)
                        : Result<ListPage<SlotAssignmentRow>>.Failure(NoView);

                public async Task<Result<StateHistoryPage>> ReadGateAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
                    await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken)
                        ? await this.history.GetHistoryAsync<JobSlot>(id, query, cancellationToken)
                        : Result<StateHistoryPage>.Failure(NoView);

                public async Task<Result<StateHistoryPage>> RealWorkAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken)
                {
                    var page = await this.history.GetHistoryAsync<JobSlot>(id, query, cancellationToken);
                    return page.IsSuccess ? page : Result<StateHistoryPage>.Failure("Not read.");
                }

                public Task<bool> MayEditAsync(CancellationToken cancellationToken) =>
                    this.permissionService.HasPermissionAsync(RolePermission.UsersEdit, cancellationToken);

                public Task<Result<StateHistoryPage>> ThroughAnotherObjectAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
                    this.factory.Create().GetHistoryAsync<JobSlot>(id, query, cancellationToken);

                private Task<bool> MayReadAsync(CancellationToken cancellationToken) =>
                    this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken);
            }
            """;
        var injected = new Dictionary<string, Type>
        {
            ["history"] = typeof(StateHistoryService),
            ["absences"] = typeof(ResourceAbsenceService),
            ["assignments"] = typeof(SlotAssignmentService),
            ["permissionService"] = typeof(PermissionService),
        };

        Assert.Equal(
            [("SampleService", "ForwardAsync", "forward"), ("SampleService", "ForwardAfterAGuardAsync", "forward"), ("SampleService", "DoubleGateAsync", "double gate"), ("SampleService", "DoubleGateThroughAHelperAsync", "double gate")],
            PassThroughs("SampleService", sample, ["ForwardAsync", "ForwardAfterAGuardAsync", "DoubleGateAsync", "DoubleGateThroughAHelperAsync", "ReadGateAsync", "RealWorkAsync", "MayEditAsync", "ThroughAnotherObjectAsync"], injected)
                .Select(hit => (hit.Service, hit.Method, hit.Kind)));
    }

    [Fact]
    public void The_scan_catches_a_forward_in_a_second_partial_file()
    {
        Assert.Equal(
            [(nameof(PartialSampleService), nameof(PartialSampleService.ForwardInTheSecondPartAsync), "forward", nameof(StateHistoryService))],
            PassThroughs(typeof(PartialSampleService)));
    }

    private static List<(string Service, string Method, string Kind, string Target)> PassThroughs(Type service) =>
        PassThroughs(service.Name, ServiceSources.Of(service), ServiceSources.PublicMethodNames(service), ServiceSources.InjectedServices(service));

    private static List<(string Service, string Method, string Kind, string Target)> PassThroughs(string service, string source, IEnumerable<string> methods, IReadOnlyDictionary<string, Type> injected)
    {
        var code = ServiceSources.Code(source);
        var checking = ServicePermissionScanTests.CheckingMethods(code, checks, ["dbFactory.", "userManager."]);
        bool Checks(string text) =>
            checks.Any(check => text.Contains(check, StringComparison.Ordinal))
            || checking.Any(name => Regex.IsMatch(text, $@"(?<![\w.])(?:this\.)?{Regex.Escape(name)}\s*(?:<[^<>()]*>)?\s*\("));

        var hits = new List<(string, string, string, string)>();
        foreach (var method in methods)
        {
            foreach (var body in ServiceSources.PublicBodiesOf(code, method))
            {
                if (ForwardOf(body, injected, Checks) is not { } forward || injected[forward.Receiver] == typeof(PermissionService))
                {
                    continue;
                }

                var target = injected[forward.Receiver];
                if (!forward.Gated)
                {
                    hits.Add((service, method, "forward", target.Name));
                    break;
                }

                if (ServicePermissionScanTests.HoldsItsOwnCheck(target, forward.Method))
                {
                    hits.Add((service, method, "double gate", target.Name));
                    break;
                }
            }
        }

        return hits;
    }

    private sealed record Forward(string Receiver, string Method, bool Gated);

    private static Forward? ForwardOf(string body, IReadOnlyDictionary<string, Type> injected, Func<string, bool> checks)
    {
        var statements = Statements(body);
        if (statements.Count == 0)
        {
            return null;
        }

        var gated = false;
        foreach (var statement in statements[..^1])
        {
            if (GuardCondition(statement, injected) is not { } condition)
            {
                return null;
            }

            gated |= checks(condition);
        }

        var expression = Unwrap(statements[^1]);
        if (Conditional(expression) is { } conditional)
        {
            var whenTrue = CallOf(Unwrap(conditional.WhenTrue), injected);
            var whenFalse = CallOf(Unwrap(conditional.WhenFalse), injected);
            var (call, other) = whenTrue is not null ? (whenTrue, conditional.WhenFalse) : (whenFalse, conditional.WhenTrue);
            return call is null || (whenTrue is not null && whenFalse is not null) || !OnlyFails(Unwrap(other))
                ? null
                : new Forward(call.Value.Receiver, call.Value.Method, gated || checks(conditional.Condition));
        }

        return CallOf(expression, injected) is { } single ? new Forward(single.Receiver, single.Method, gated) : null;
    }

    private static List<string> Statements(string body)
    {
        var text = body.TrimStart();
        if (text.StartsWith("=>", StringComparison.Ordinal))
        {
            return [text[2..].Trim()];
        }

        if (!text.StartsWith('{'))
        {
            return [];
        }

        var statements = new List<string>();
        var start = 1;
        var depth = 0;
        for (var index = 1; index < text.Length; index++)
        {
            var character = text[index];
            if (character is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (character is ')' or ']' or '}')
            {
                depth--;
                if (character == '}' && depth == 0 && ControlStatement().IsMatch(text[start..index]) && !ContinuedStatement().IsMatch(text[(index + 1)..]))
                {
                    statements.Add(text[start..(index + 1)].Trim());
                    start = index + 1;
                }

                continue;
            }

            if (character == ';' && depth == 0)
            {
                statements.Add(text[start..index].Trim());
                start = index + 1;
            }
        }

        if (text[start..].Trim() is { Length: > 0 } rest)
        {
            statements.Add(rest);
        }

        return [.. statements.Where(statement => statement.Length > 0)];
    }

    private static string? GuardCondition(string statement, IReadOnlyDictionary<string, Type> injected)
    {
        if (ArgumentCheck().IsMatch(statement))
        {
            return string.Empty;
        }

        var opening = IfStatement().Match(statement);
        if (!opening.Success)
        {
            return null;
        }

        var close = Closing(statement, opening.Index + opening.Length - 1);
        if (close < 0)
        {
            return null;
        }

        var branch = statement[(close + 1)..].Trim();
        if (branch.StartsWith('{') && branch.EndsWith('}'))
        {
            branch = branch[1..^1].Trim().TrimEnd(';').Trim();
        }

        var leaves = (branch.StartsWith("return", StringComparison.Ordinal) || branch.StartsWith("throw", StringComparison.Ordinal)) && !branch.Contains(';', StringComparison.Ordinal);
        var callsAService = injected.Keys.Any(name => Regex.IsMatch(branch, $@"(?<![\w.])(?:this\.)?{Regex.Escape(name)}\s*\."));
        return leaves && !callsAService ? statement[(opening.Index + opening.Length)..close] : null;
    }

    private static string Unwrap(string expression)
    {
        var text = expression.Trim();
        for (var changed = true; changed;)
        {
            var before = text;
            text = LeadingKeyword().Replace(text, string.Empty, 1).Trim();
            text = TrailingConfigureAwait().Replace(text, string.Empty, 1).Trim();
            if (text.StartsWith('(') && Closing(text, 0) == text.Length - 1)
            {
                text = text[1..^1].Trim();
            }

            changed = text != before;
        }

        return text;
    }

    private static (string Condition, string WhenTrue, string WhenFalse)? Conditional(string expression)
    {
        var depth = 0;
        var question = -1;
        var nested = 0;
        for (var index = 0; index < expression.Length; index++)
        {
            var character = expression[index];
            depth += character switch { '(' or '[' or '{' => 1, ')' or ']' or '}' => -1, _ => 0 };
            if (depth != 0 || index == 0 || index + 1 >= expression.Length || !char.IsWhiteSpace(expression[index - 1]) || !char.IsWhiteSpace(expression[index + 1]))
            {
                continue;
            }

            if (character == '?')
            {
                if (question < 0)
                {
                    question = index;
                }
                else
                {
                    nested++;
                }
            }
            else if (character == ':' && question >= 0)
            {
                if (nested > 0)
                {
                    nested--;
                    continue;
                }

                return (expression[..question].Trim(), expression[(question + 1)..index].Trim(), expression[(index + 1)..].Trim());
            }
        }

        return null;
    }

    private static (string Receiver, string Method)? CallOf(string expression, IReadOnlyDictionary<string, Type> injected)
    {
        var call = ServiceCall().Match(expression);
        if (!call.Success || !injected.ContainsKey(call.Groups["receiver"].Value))
        {
            return null;
        }

        var close = Closing(expression, call.Index + call.Length - 1);
        return close == expression.Length - 1 ? (call.Groups["receiver"].Value, call.Groups["method"].Value) : null;
    }

    private static bool OnlyFails(string branch) => FailingBranch().IsMatch(branch);

    private static int Closing(string text, int open)
    {
        for (int index = open, depth = 0; index < text.Length; index++)
        {
            depth += text[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"^\s*(?:if|else|for|foreach|while|do|try|catch|finally|using|lock|switch|checked|unchecked|unsafe|fixed)\b")]
    private static partial Regex ControlStatement();

    [GeneratedRegex(@"^\s*(?:else|catch|finally|while)\b")]
    private static partial Regex ContinuedStatement();

    [GeneratedRegex(@"^if\s*\(")]
    private static partial Regex IfStatement();

    [GeneratedRegex(@"^(?:\w+\.)*ThrowIf\w*\s*\(.*\)$", RegexOptions.Singleline)]
    private static partial Regex ArgumentCheck();

    [GeneratedRegex(@"^(?:return|await)\b")]
    private static partial Regex LeadingKeyword();

    [GeneratedRegex(@"\.ConfigureAwait\s*\(\s*(?:false|true)\s*\)$")]
    private static partial Regex TrailingConfigureAwait();

    [GeneratedRegex(@"^(?:this\.)?(?<receiver>\w+)\s*\.\s*(?<method>\w+)\s*(?:<[^()]*?>)?\s*\(")]
    private static partial Regex ServiceCall();

    [GeneratedRegex(@"^(?:throw\b|default\b|null$|[\w.<>?,\[\]\s]*\b(?:Failure|Success|FromResult)\s*(?:<[^()]*?>)?\s*\()")]
    private static partial Regex FailingBranch();
}
