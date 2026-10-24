namespace RepoAgentController;

/// <summary>Where the core reports controller events. Each event is a log kind, such as <c>selection-error</c>, and a message
/// that the adapter redacts and writes to stdout and the retained logs unchanged.</summary>
public interface IControllerLog
{
    void Write(string kind, string message);
}
