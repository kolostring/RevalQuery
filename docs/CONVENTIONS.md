# Conventions

1. `RevalQuery.Core` has no UI-framework dependency; Blazor-specific code lives in `RevalQuery.Blazor`, which references Core and never the reverse.
2. Core depends only on `*.Abstractions` packages at the lowest supported version (currently `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.0), so consumers on any target framework can resolve it. (inferred: confirm)
3. Every project, tests included, targets every supported .NET from net8.0 up, and CI runs the tests on each one, not just compiles them. Today that is net8.0, net9.0 and net10.0; net11.0 joins at its GA release, not before, because a stable package must not depend on a prerelease `Microsoft.AspNetCore.Components`.
4. `Nullable` and `ImplicitUsings` are enabled in every project, examples included.
5. Every public member carries an XML doc comment; the build generates the documentation file, so a missing one is a CS1591 warning. Code outside the doc comments goes uncommented.
6. Each library has its own test project: `RevalQuery.Core` is tested in `RevalQuery.Core.Tests`, `RevalQuery.Blazor` in `RevalQuery.Blazor.Tests`. A test belongs to the project of the code it exercises, so `RevalQuery.Core.Tests` never references `RevalQuery.Blazor`. Both use xUnit; Blazor tests use bUnit against small `.razor` fixture components where a renderer is needed, and Core tests reach Core's internals through `InternalsVisibleTo`.
7. A defect found in review is fixed in the same commit as a regression test that fails without the fix, in a file named after the reviewed commit (`ReviewOf<sha>Tests.cs`). (inferred: confirm)
8. Domain terms come from `CONTEXT.md`. Type prefixes follow the `reval-prefix-for-types-that-span-queries-and-mutations` ADR: `Reval…` spans queries and mutations, `Query…`/`Mutation…` covers one kind.
9. Versions come from MinVer and `v*` git tags, and are never hand-edited. Publishing a GitHub release packs and pushes to NuGet.
10. Commits follow Conventional Commits (`feat:`, `fix:`, `chore:`, `!` for breaking changes). Examples use `ProjectReference` to the in-repo projects, never a published package.
