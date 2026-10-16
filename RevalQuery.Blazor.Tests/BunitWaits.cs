using System.Runtime.CompilerServices;
using Bunit;

namespace RevalQuery.Blazor.Tests;

internal static class BunitWaits
{
    // bUnit's own 1 s ceiling would decide pass/fail on a slow agent; the hang guard never does.
    [ModuleInitializer]
    internal static void TimeOutOnlyOnTheHangGuard() => TestContextBase.DefaultWaitTimeout = HangGuard;
}
