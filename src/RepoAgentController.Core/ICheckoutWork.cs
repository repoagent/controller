namespace RepoAgentController;

/// <summary>Where the managed checkout records the agent turns it admits, and whether it holds new turns for a refresh.</summary>
public interface ICheckoutWork
{
    /// <summary>The checkout's admitted agent turns and admission, or null when it records none or they cannot be read.</summary>
    CheckoutWork? Read();
}

/// <summary>The agent sessions with a turn the checkout admitted, and whether new turns are held for a refresh;
/// null when that is not recorded as true or false.</summary>
public sealed record CheckoutWork(IReadOnlySet<string> Sessions, bool? AdmissionBlocked);
