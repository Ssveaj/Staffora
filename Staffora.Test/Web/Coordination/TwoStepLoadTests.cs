using Staffora.Blazor.Components.Coordination;
using Staffora.Core.Models;

namespace Staffora.Test.Web.Coordination;

public class TwoStepLoadTests
{
    [Fact]
    public async Task The_first_step_shows_as_soon_as_it_comes_and_the_second_once_it_follows()
    {
        using var load = new TwoStepLoad<string, string>();
        var changes = new List<(TwoStepLoadState State, string? First, string? Second)>();
        load.Changed += () => changes.Add((load.State, load.First, load.Second));
        var bookings = new TaskCompletionSource<Result<string>>();

        var running = load.LoadAsync(_ => Task.FromResult(Result<string>.Success("staff")), (_, _) => bookings.Task);
        Assert.Equal((TwoStepLoadState.FirstShown, "staff", (string?)null), (load.State, load.First, load.Second));

        bookings.SetResult(Result<string>.Success("bookings"));
        Assert.True(await running);
        Assert.Equal([(TwoStepLoadState.FirstShown, "staff", null), (TwoStepLoadState.Ready, "staff", "bookings")], changes);
    }

    [Fact]
    public async Task A_load_is_known_to_run_while_the_earlier_board_still_shows_ready_also_once_a_newer_one_overtook_it()
    {
        using var load = new TwoStepLoad<string, string>();
        Assert.True(await load.LoadAsync(_ => Task.FromResult(Result<string>.Success("old")), (_, _) => Task.FromResult(Result<string>.Success("old bookings"))));
        Assert.False(load.IsRunning);
        var runningWhenShown = new List<(TwoStepLoadState State, bool Running)>();
        load.Changed += () => runningWhenShown.Add((load.State, load.IsRunning));

        var closing = new TaskCompletionSource<Result<string>>();
        var live = new TaskCompletionSource<Result<string>>();
        var overtaken = load.LoadAsync(_ => closing.Task, (_, _) => Task.FromResult(Result<string>.Success("closing bookings")));
        Assert.True(load.IsRunning);
        var newest = load.LoadAsync(_ => live.Task, (_, _) => Task.FromResult(Result<string>.Success("new bookings")));
        closing.SetResult(Result<string>.Success("closing"));
        Assert.False(await overtaken);
        Assert.Equal((TwoStepLoadState.Ready, "old", true), (load.State, load.First, load.IsRunning));

        live.SetResult(Result<string>.Success("new"));
        Assert.True(await newest);
        Assert.Equal([(TwoStepLoadState.FirstShown, true), (TwoStepLoadState.Ready, false)], runningWhenShown);
        Assert.False(load.IsRunning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => load.LoadAsync(_ => throw new InvalidOperationException("broken"), (_, _) => Task.FromResult(Result<string>.Success("never"))));
        Assert.False(load.IsRunning);
    }

    [Fact]
    public async Task A_slow_first_step_overtaken_by_a_newer_load_shows_nothing_and_never_reads_its_second_step()
    {
        using var load = new TwoStepLoad<string, string>();
        var slow = new TaskCompletionSource<Result<string>>();
        CancellationToken slowToken = default;
        var secondsRead = new List<string>();

        var older = load.LoadAsync(
            token =>
            {
                slowToken = token;
                return slow.Task;
            },
            (first, _) =>
            {
                secondsRead.Add(first);
                return Task.FromResult(Result<string>.Success($"bookings of {first}"));
            });
        var newer = await load.LoadAsync(
            _ => Task.FromResult(Result<string>.Success("week 11")),
            (first, _) =>
            {
                secondsRead.Add(first);
                return Task.FromResult(Result<string>.Success($"bookings of {first}"));
            });
        Assert.True(newer);
        Assert.True(slowToken.IsCancellationRequested);

        slow.SetResult(Result<string>.Success("week 10"));
        Assert.False(await older);
        Assert.Equal((TwoStepLoadState.Ready, "week 11", "bookings of week 11"), (load.State, load.First, load.Second));
        Assert.Equal(["week 11"], secondsRead);
        Assert.Equal(2, load.Loads);
    }

    [Fact]
    public async Task A_second_step_a_newer_load_overtook_shows_nothing_and_a_cancelled_step_shows_nothing_either()
    {
        using var load = new TwoStepLoad<string, string>();
        var slowBookings = new TaskCompletionSource<Result<string>>();
        var older = load.LoadAsync(_ => Task.FromResult(Result<string>.Success("week 10")), (_, _) => slowBookings.Task);
        Assert.Equal("week 10", load.First);

        Assert.True(await load.LoadAsync(_ => Task.FromResult(Result<string>.Success("week 11")), (_, _) => Task.FromResult(Result<string>.Success("bookings of week 11"))));
        slowBookings.SetResult(Result<string>.Success("bookings of week 10"));
        Assert.False(await older);
        Assert.Equal(("week 11", "bookings of week 11"), (load.First, load.Second));

        var waiting = load.LoadAsync(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Result<string>.Success("never");
        }, (_, _) => Task.FromResult(Result<string>.Success("never")));
        Assert.True(await load.LoadAsync(_ => Task.FromResult(Result<string>.Success("week 12")), (_, _) => Task.FromResult(Result<string>.Success("bookings of week 12"))));
        Assert.False(await waiting);
        Assert.Equal(("week 12", "bookings of week 12"), (load.First, load.Second));
    }

    [Fact]
    public async Task The_earlier_bookings_stay_beside_a_new_first_step_only_while_they_fit_it()
    {
        using var load = new TwoStepLoad<string, string>();
        await load.LoadAsync(_ => Task.FromResult(Result<string>.Success("week 10")), (_, _) => Task.FromResult(Result<string>.Success("week 10")));

        var again = new TaskCompletionSource<Result<string>>();
        var reload = load.LoadAsync(_ => Task.FromResult(Result<string>.Success("week 10")), (_, _) => again.Task, (first, second) => first == second);
        Assert.Equal((TwoStepLoadState.FirstShown, "week 10"), (load.State, load.Second));
        again.SetResult(Result<string>.Success("week 10"));
        await reload;

        var other = new TaskCompletionSource<Result<string>>();
        var moved = load.LoadAsync(_ => Task.FromResult(Result<string>.Success("week 11")), (_, _) => other.Task, (first, second) => first == second);
        Assert.Equal((TwoStepLoadState.FirstShown, (string?)null), (load.State, load.Second));
        other.SetResult(Result<string>.Success("week 11"));
        await moved;
    }

    [Fact]
    public async Task A_failed_step_is_shown_in_words_and_a_dropped_load_shows_nothing()
    {
        var load = new TwoStepLoad<string, string>();
        await load.LoadAsync(_ => Task.FromResult(Result<string>.Failure("No permission.")), (_, _) => throw new InvalidOperationException("never read"));
        Assert.Equal((TwoStepLoadState.Failed, "No permission.", (string?)null), (load.State, load.Error, load.First));

        await load.LoadAsync(_ => Task.FromResult(Result<string>.Success("week 10")), (_, _) => Task.FromResult(Result<string>.Failure("The bookings could not be read.")));
        Assert.Equal((TwoStepLoadState.SecondFailed, "week 10", "The bookings could not be read."), (load.State, load.First, load.SecondError));

        var late = new TaskCompletionSource<Result<string>>();
        var running = load.LoadAsync(_ => late.Task, (_, _) => Task.FromResult(Result<string>.Success("never")));
        load.Dispose();
        late.SetResult(Result<string>.Success("week 11"));
        Assert.False(await running);
        Assert.Equal("week 10", load.First);
    }
}
