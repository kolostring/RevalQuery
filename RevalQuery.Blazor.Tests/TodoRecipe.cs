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

    public static MutationOptionsBuilder<string, string, List<string>> AddTodo() =>
        MutationOptions.Create<string, string>(static ctx =>
                ctx.ServiceProvider.GetRequiredService<GatedHandler<string>>().Handle(ctx))
            .OnMutate(static async ctx =>
            {
                await ctx.Client.CancelAsync(Key);

                List<string> previous = [];
                ctx.Client.SetQueryData(Todos().Build(), current =>
                {
                    previous = current ?? [];
                    return [.. previous, ctx.Params];
                });
                return previous;
            })
            .OnException(static ctx =>
            {
                ctx.Client.SetQueryData(Todos().Build(), ctx.OnMutateResult);
                return Task.CompletedTask;
            })
            .OnSettled(static ctx =>
            {
                ctx.Client.Invalidate(Key);
                return Task.CompletedTask;
            });
}
