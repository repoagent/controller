using System.Text.RegularExpressions;

namespace DiscussionBridge;

/// <summary>A configured text trigger, separate from the authenticated App actor.</summary>
public sealed class MentionTrigger
{
    readonly Regex pattern;
    public string Name { get; }

    public MentionTrigger(string agentLogin, string? mentionName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentLogin);
        if (mentionName is not null && !Regex.IsMatch(mentionName, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\z"))
            throw new ArgumentException("--mention-name must be 1–39 letters, digits or hyphens, starting and ending with a letter or digit; omit the @");
        Name = mentionName ?? agentLogin;
        var names = new[] { agentLogin, Name }.Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape);
        pattern = new Regex(@"(?<![\w!#$%&'*+/=?^`{|}~.@-])@(?:" + string.Join('|', names) + @")(?![\w\[-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public bool Matches(string text) => pattern.IsMatch(text);
}
