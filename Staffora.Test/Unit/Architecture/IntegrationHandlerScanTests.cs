using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Integration;
using Staffora.Services.Portal;
using Staffora.Services.Workflow;

namespace Staffora.Test.Unit.Architecture;

public class IntegrationHandlerScanTests
{
    private static readonly Assembly[] productAssemblies =
    [
        typeof(Order).Assembly,
        typeof(ApplicationDbContext).Assembly,
        typeof(WorkflowService).Assembly,
        typeof(Staffora.Blazor.Helper.DialogServiceExtensions).Assembly,
    ];

    [Fact]
    public void Every_handler_writes_only_inside_its_delivery()
    {
        var handlers = productAssemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IIntegrationHandler).IsAssignableFrom(type))
            .ToList();
        Assert.Contains(typeof(JobStateSyncHandler), handlers);
        Assert.Contains(typeof(OrderCancelHandler), handlers);
        Assert.Empty(handlers.SelectMany(Offences));
    }

    [Fact]
    public void The_scan_catches_a_public_step_a_context_a_transaction_and_a_lease()
    {
        Assert.Equal(
            ["BeginSystemScope", "BeginTransactionAsync", "CommitTransactionAsync", "CreateDbContext", "TransitionEntityAsync"],
            Offences(typeof(OutsideTheDeliverySample)).Select(offence => offence[(offence.LastIndexOf(' ') + 1)..]).Distinct().Order(StringComparer.Ordinal));
    }

    private static IEnumerable<string> Offences(Type handler)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        IEnumerable<MethodBase> roots = [.. handler.GetMethods(all), .. handler.GetConstructors(all)];
        foreach (var called in IlScan.ReachAll(roots.Where(method => method.GetMethodBody() is not null), referenced => referenced.DeclaringType == handler && !referenced.IsPublic ? [referenced] : []))
        {
            var offends = called.DeclaringType == typeof(WorkflowService) && called.IsPublic
                || (called.DeclaringType is { IsGenericType: true } factory && factory.GetGenericTypeDefinition() == typeof(IApplicationDbContextFactory<>))
                || called.DeclaringType == typeof(DatabaseFacade) && called.Name.Contains("Transaction", StringComparison.Ordinal)
                || called.DeclaringType == typeof(ApplicationDbContext) && called.Name == nameof(ApplicationDbContext.BeginSystemScope)
                || called.Name == "BeginPlatformScope";
            if (offends)
            {
                yield return $"{handler.Name} calls {called.Name}";
            }
        }
    }

    private sealed class OutsideTheDeliverySample : IIntegrationHandler
    {
        private readonly IApplicationDbContextFactory<ApplicationDbContext> factory;
        private readonly WorkflowService workflows;

        public OutsideTheDeliverySample(IApplicationDbContextFactory<ApplicationDbContext> factory, WorkflowService workflows)
        {
            this.factory = factory;
            this.workflows = workflows;
        }

        public async Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken)
        {
            await using var own = this.factory.CreateDbContext(null!);
            await using var transaction = await own.Database.BeginTransactionAsync(cancellationToken);
            using (context.BeginSystemScope("a sample"))
            {
                await this.workflows.TransitionEntityAsync<OrderState, Order>(Guid.Empty, "a step", cancellationToken: cancellationToken);
            }

            await own.Database.CommitTransactionAsync(cancellationToken);
        }
    }
}
