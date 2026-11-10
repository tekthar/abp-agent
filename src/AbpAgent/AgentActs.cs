namespace AbpAgent;

/// <summary>
/// What a run is doing now, for the code under it: the runner sets it around each read, tool call and answer,
/// and has the host's <see cref="IAgentActingScope"/> put the act under the agent's name and the run's change set. The
/// cleaner reads it, so whatever a tool writes while an agent acts is cleaned.
/// </summary>
public static class AgentActs
{
    private static readonly AsyncLocal<AgentActing?> Current = new();

    /// <summary>The act under way here; null outside a run.</summary>
    public static AgentActing? Now => Current.Value;

    /// <summary>Sets the act, and the host's scope for it, until it's disposed.</summary>
    public static IDisposable Begin(AgentActing acting, IAgentActingScope host)
    {
        AgentActing? before = Current.Value;
        Current.Value = acting;
        IDisposable? scope = host.Begin(acting);
        return new End(() =>
        {
            scope?.Dispose();
            Current.Value = before;
        });
    }

    private sealed class End(Action end) : IDisposable
    {
        private int ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0)
            {
                end();
            }
        }
    }
}
