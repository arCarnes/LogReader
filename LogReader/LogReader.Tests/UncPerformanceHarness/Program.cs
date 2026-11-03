using System.Reflection;
using LogReader.Tests.UncPerformance;

try
{
    if (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release" ||
        args.Length != 3 || args[0] != "ui-sample")
        throw new ArgumentException("Use a Release build: ui-sample <private-manifest> <new-local-report>.");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    await UiWorkflowMeasurements.RunAsync(args[1], args[2], cancellation.Token);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("UI measurement failed: " + ex.GetType().Name + " at " + UiWorkflowMeasurements.Stage);
    Console.Error.WriteLine("HResult: " + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
    foreach (var frame in new System.Diagnostics.StackTrace(ex, false).GetFrames().Take(24))
    {
        var method = frame.GetMethod();
        Console.Error.WriteLine(method?.DeclaringType?.FullName + "." + method?.Name);
    }
    return 1;
}
