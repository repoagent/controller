using System.Diagnostics;

// A native executable boundary for Windows ProcessStartInfo. Python remains the
// single scripted provider fixture; inherit its streams without buffering them.
var start = new ProcessStartInfo
{
    FileName = Environment.GetEnvironmentVariable("REVOKE_GH_PYTHON")
        ?? throw new InvalidOperationException("Missing fixture Python executable."),
    UseShellExecute = false,
};
start.ArgumentList.Add(Environment.GetEnvironmentVariable("REVOKE_GH_FIXTURE")
    ?? throw new InvalidOperationException("Missing fixture script."));
foreach (var argument in args)
{
    start.ArgumentList.Add(argument);
}
using var child = Process.Start(start)
    ?? throw new InvalidOperationException("Could not start provider fixture.");
child.WaitForExit();
return child.ExitCode;
