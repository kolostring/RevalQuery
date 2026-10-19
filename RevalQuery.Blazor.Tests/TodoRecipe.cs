using Microsoft.Extensions.DependencyInjection;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query.Options;
using RevalQuery.TestKit;

namespace RevalQuery.Blazor.Tests;

// The README's optimistic recipe, defined once in a static factory: no injected client, no
// component field.
public static class TodoRecipe
{
    public const string Key = "todos";

    public static QueryOptionsBuilder<ValueTuple<string>, List<string>> Todos() =>
        QueryOptions.Create<List<string>>(Key, static ctx =>
            ctx.ServiceProvider.GetRequiredService<GatedHandler<List<string>>>().Handle(ctx));

    // whileUpdating, for a test, runs inside every attempt of the optimistic updater, where
    // the test can land another write on the key.
    public static MutationOptionsBuilder<string, string, List<string>> AddTodo(Action<int>? whileUpdating = null) =>
        MutationOptions.Create<string, string>(static ctx =>
                ctx.ServiceProvider.GetRequiredService<GatedHandler<string>>().Handle(ctx))
            .OnMutate(async ctx =>
            {
                await ctx.Client.CancelAsync(Key);

                List<string> previous = [];
                var attempts = 0;
                ctx.Client.SetQueryData(Todos().Build(), current =>
                {
                    whileUpdating?.Invoke(++attempts);
                    previous = current ?? [];
                    return [.. previous, ctx.Params];
                });
                return previous;
            })
            .OnException(static ctx =>
            {
                // default when OnMutate threw before it had changed anything.
                if (ctx.OnMutateResult is not null) ctx.Client.SetQueryData(Todos().Build(), ctx.OnMutateResult);
                return Task.CompletedTask;
            })
            .OnSettled(static ctx =>
            {
                ctx.Client.Invalidate(Key);
                return Task.CompletedTask;
            });
}
