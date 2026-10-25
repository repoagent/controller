namespace RepoAgentController;

/// <summary>One redacted controller event, written to stdout and to any observer of this controller lifetime.</summary>
public readonly record struct LogEntry(DateTimeOffset At, string Kind, string Message)
{
    public string Line => $"{At:O} {Kind} {Message}";
}
