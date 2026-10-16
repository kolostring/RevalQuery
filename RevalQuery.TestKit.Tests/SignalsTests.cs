using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Mutation;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.TestKit.Tests;

public class SignalsTests
{
    private sealed class FakeState : IObservableQueryState
    {
        private Action? _changed;

        public bool Value { get; set; }
        public int Checks { get; set; }
        public int Handlers => _changed?.GetInvocationList().Length ?? 0;
        public bool ChangeWhileSubscribing { get; init; }

        public event Action? OnChanged
        {
            add
            {
                if (ChangeWhileSubscribing)
                {
                    Value = true;
                    _changed?.Invoke();
                }

                _changed += value;
            }
            remove => _changed -= value;
        }

        public void Raise() => _changed?.Invoke();

        public void Subscribe(IQueryObserver observer) => throw new NotSupportedException();

        public void Unsubscribe(IQueryObserver observer) => throw new NotSupportedException();
    }

    private static bool Holds(FakeState state)
    {
        state.Checks++;
        return state.Value;
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Condition_That_Already_Holds_Completes_The_Wait()
    {
        var state = new FakeState { Value = true };

        await Signals.UntilAsync(state, Holds, TestContext.Current.CancellationToken);

        Assert.Equal(0, state.Handlers);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task The_Wait_Rechecks_On_Every_Change_Until_The_Condition_Holds()
    {
        var state = new FakeState();
        var wait = Signals.UntilAsync(state, Holds, TestContext.Current.CancellationToken);

        state.Raise();
        Assert.False(wait.IsCompleted);
        Assert.Equal(2, state.Checks);

        state.Value = true;
        state.Raise();

        await wait;
        Assert.Equal(3, state.Checks);
        Assert.Equal(0, state.Handlers);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Change_Raised_Between_Subscribing_And_Checking_Is_Not_Missed()
    {
        var state = new FakeState { ChangeWhileSubscribing = true };

        await Signals.UntilAsync(state, Holds, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task The_Wait_Never_Resumes_Inside_The_Change_Notification()
    {
        var state = new FakeState();
        var wait = Signals.UntilAsync(state, Holds, TestContext.Current.CancellationToken);
        var resumedInline = wait.ContinueWith(_ => Inline.Inside, TaskContinuationOptions.ExecuteSynchronously);

        Inline.Run(() =>
        {
            state.Value = true;
            state.Raise();
        });

        Assert.False(await resumedInline.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Condition_That_Throws_Fails_The_Wait()
    {
        var state = new FakeState();
        var wait = Signals.UntilAsync(state, s => s.Value ? throw new InvalidOperationException("boom") : false, TestContext.Current.CancellationToken);

        state.Value = true;
        state.Raise();

        await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Equal(0, state.Handlers);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Wait_Whose_Token_Is_Cancelled_Fails_Rather_Than_Passing()
    {
        var state = new FakeState();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Signals.UntilAsync(state, Holds, cancelled.Token));
        Assert.Equal(0, state.Handlers);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Query_Wait_Ends_When_The_Query_Reaches_The_Condition()
    {
        var handler = new GatedHandler<string>();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), new ManualEviction());
        client.Subscribe(QueryOptions.Create("a", handler.Handle).Build(), () => { });
        var state = client.FindQuery<string>("a")!;
        await handler.Started(0).WaitAsync(TestContext.Current.CancellationToken);

        var resolved = Signals.UntilAsync(state, s => s.IsResolved, TestContext.Current.CancellationToken);
        Assert.False(resolved.IsCompleted);
        handler.Release(0, "data");

        await resolved;
        Assert.Equal("data", state.Data);
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task A_Mutation_Wait_Ends_When_The_Mutation_Reaches_The_Condition()
    {
        var gate = new Gate();
        using var client = new RevalClient(new ServiceCollection().BuildServiceProvider(), new RevalQueryOptions(), new ManualEviction());
        using var observer = client.CreateMutation(MutationOptions.Create<string, string>(async _ =>
        {
            await gate.Task;
            return "done";
        }).Build(), () => { });
        IMutationState<string> state = observer.State;
        var execution = observer.State.ExecuteAsync("params", TestContext.Current.CancellationToken);

        var resolved = Signals.UntilAsync(state, s => s.IsResolved, TestContext.Current.CancellationToken);
        Assert.False(resolved.IsCompleted);
        gate.Open();

        await resolved;
        Assert.Equal("done", state.Data);
        await execution.WaitAsync(TestContext.Current.CancellationToken);
    }
}
