using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace RepoAgent.Auth.Development;

// Optional, agent local controller. Its enrollment is injected only into that child, which removes it before
// starting tools. The AppHost never reads an App private key or writes the controller enrollment to its source tree.
internal static class ControllerDevelopment
{
    public static void Add(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> auth)
    {
        var configuration = builder.Configuration.GetSection("Controller");
        if (configuration["Repository"] is not { Length: > 0 } repository) return;
        if (builder.Configuration.GetValue("DcpPublisher:RandomizePorts", false))
            throw new InvalidOperationException("A configured controller requires its saved, stable broker origin. Randomized ports (--isolated) are supported only in broker-only mode; use a dedicated stable-port local stack and a fresh enrollment instead of rewriting an enrollment.");
        if (string.IsNullOrWhiteSpace(builder.Configuration["SharedSigningKeyUri"]))
            throw new InvalidOperationException("A configured controller requires SharedSigningKeyUri and the authorized developer's Azure CLI sign-in.");
        var origin = Required(configuration, "BrokerOrigin");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var broker) || broker.Scheme != Uri.UriSchemeHttps ||
            broker.Host != "localhost" || broker.UserInfo.Length != 0 || broker.Query.Length != 0 || broker.Fragment.Length != 0 || broker.AbsolutePath != "/")
            throw new InvalidOperationException("Controller:BrokerOrigin must be a trusted HTTPS localhost origin of this local broker.");
        var sourceRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../../.."));
        var workspace = Absolute(configuration, "Workspace");
        var stateDirectory = OutsideSource(Absolute(configuration, "StateDirectory"), sourceRoot);
        var codexHome = OutsideSource(configuration["CodexHome"] is { Length: > 0 }
            ? Absolute(configuration, "CodexHome") : Path.Combine(stateDirectory, "codex"), sourceRoot);
        var statusPort = configuration.GetValue("StatusPort", 8081);
        if (statusPort is < 1 or > 65535 || statusPort == broker.Port)
            throw new InvalidOperationException("Controller:StatusPort must be a free port distinct from the broker port.");
        var enrollment = builder.AddParameter("controller-enrollment", secret: true);
        var arguments = new List<string>
        {
            "--repo", repository, "--app-id", Required(configuration, "AppId"),
            "--broker-url", broker.GetLeftPart(UriPartial.Authority),
            "--enrollment-environment", "REPOAGENT_LOCAL_ENROLLMENT",
            "--workspace", workspace, "--state", Path.Combine(stateDirectory, "controller.json"),
            "--status-port", statusPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (configuration.GetValue("Clone", false)) arguments.Add("--clone");
        if (configuration.GetValue("RepositorySettingsRefresh", false)) arguments.Add("--repository-settings");
        foreach (var (key, option) in new[] { ("MentionUser", "--mention-user"), ("DashboardCategory", "--dashboard-category") })
            if (configuration[key] is { Length: > 0 } value) arguments.AddRange([option, value]);
        if (configuration["CodexAuthFile"] is { Length: > 0 }) arguments.AddRange(["--codex-auth", Absolute(configuration, "CodexAuthFile")]);
        // Keep the broker's public and process origins identical, so private saved enrollment survives restarts.
        // No proxy port, discovery alias or random replacement may change its immutable brokerOrigin binding.
        auth.WithEndpoint("https", endpoint =>
        {
            endpoint.Port = broker.Port;
            endpoint.TargetPort = broker.Port;
            endpoint.IsProxied = false;
        });
        builder.AddProject<Projects.RepoAgentController>("controller", launchProfileName: null)
            .WithArgs([.. arguments])
            .WithEnvironment("REPOAGENT_LOCAL_ENROLLMENT", enrollment)
            .WithEnvironment("CODEX_HOME", codexHome)
            .WithHttpEndpoint(port: statusPort, targetPort: statusPort, name: "status", isProxied: false)
            .WithHttpHealthCheck("/status", endpointName: "status")
            .WaitFor(auth);
    }

    static string Required(IConfiguration configuration, string key) => configuration[key] is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Controller:{key} is required when Controller:Repository is configured.");

    static string Absolute(IConfiguration configuration, string key)
    {
        var path = Required(configuration, key);
        return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path)
            : throw new InvalidOperationException($"Controller:{key} must be an absolute path.");
    }

    static string OutsideSource(string path, string sourceRoot)
    {
        var relative = Path.GetRelativePath(sourceRoot, path);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathFullyQualified(relative)
            ? path : throw new InvalidOperationException("Controller state and Codex storage must be outside the repository.");
    }
}
