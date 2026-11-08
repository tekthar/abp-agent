namespace Toda.Agents;

/// <summary>
/// Who acts on an agents service (ADR 0091): the account, and the host's own principal for it (Toda's Casl ability),
/// carried back to the host's seams. The package never reads the principal.
/// </summary>
public sealed record AgentActor(Guid Id, object? Principal = null);

/// <summary>An account as the agents' pages show it: a person, a service account or an agent's.</summary>
public sealed record AgentPerson(Guid Id, string Username, string DisplayName, bool IsActive)
{
    /// <summary><see cref="AgentPersonKinds"/>: what the account is, so pickers and history can tell an agent from a colleague.</summary>
    public string Kind { get; init; } = AgentPersonKinds.Person;

    /// <summary>The account's picture (ADR 0096): a path on the host's site it serves, or null for its initials.</summary>
    public string? Picture { get; init; }
}

public static class AgentPersonKinds
{
    public const string Person = "person";
    public const string Service = "service";
    public const string Agent = "agent";
}

/// <summary>
/// The host's accounts (ADR 0091): an agent works through an account of its own, marked as an agent's, which never
/// signs in and holds no tokens.
/// </summary>
public interface IAgentAccounts
{
    /// <summary>Makes an agent's account. Refused (with a message for the person) when the username is taken or isn't one.</summary>
    Task<AgentPerson> CreateAsync(string username, string displayName, CancellationToken cancellationToken);

    /// <summary>The accounts there are of these; missing ones are left out.</summary>
    Task<IReadOnlyDictionary<Guid, AgentPerson>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Makes an agent's account with its name in other languages as well (by language code) beside the name as written.
    /// The default makes it with the name as written, then gives it the others through <see cref="RenameAsync"/>.
    /// </summary>
    async Task<AgentPerson> CreateAsync(string username, string displayName, IReadOnlyDictionary<string, string> displayNames, CancellationToken cancellationToken)
    {
        AgentPerson made = await CreateAsync(username, displayName, cancellationToken);
        if (displayNames.Count > 0)
        {
            await RenameAsync(made.Id, displayName, displayNames, cancellationToken);
        }

        return made;
    }

    /// <summary>
    /// Gives an agent's account its names, when the agent is made with names in several languages or renamed: the name
    /// as written (its English name, else its first) and its name in each other language, by language code. The default
    /// leaves the account as it was made, for a host whose accounts have one name.
    /// </summary>
    Task RenameAsync(Guid accountId, string displayName, IReadOnlyDictionary<string, string> displayNames, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Sets or clears (null) an agent's account's picture (ADR 0096): a path on the host's site that the host serves and
    /// checks. The default refuses, for a host whose accounts have no pictures.
    /// </summary>
    Task SetPictureAsync(Guid accountId, string? picture, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("This host's accounts have no pictures."));
}

/// <summary>Who may administer what (ADR 0091). The default: nobody but those the host calls site admins, and no scopes.</summary>
public interface IAgentRights
{
    /// <summary>Whether the actor administers the site: every agent, every run's transcript.</summary>
    Task<bool> IsSiteAdminAsync(AgentActor actor, CancellationToken cancellationToken);

    /// <summary>Whether the actor administers the scope: its own agents, and taking the site's on.</summary>
    Task<bool> ManagesScopeAsync(AgentActor actor, Guid scopeId, CancellationToken cancellationToken);
}

/// <summary>A scope agents work in (a project, a marketplace), by its id and the key people know it by.</summary>
public sealed record AgentScopeRef(Guid Id, string Key);

/// <summary>
/// The host's scopes (ADR 0091): agents are made for one, offered to them, and taken on by them with a role the agent's
/// account gets there for the runs the system starts.
/// </summary>
public interface IAgentScopes
{
    /// <summary>The scope by its key, when the actor may read it; null otherwise.</summary>
    Task<AgentScopeRef?> FindAsync(AgentActor actor, string key, CancellationToken cancellationToken);

    /// <summary>The scopes of these ids there are, by id.</summary>
    Task<IReadOnlyDictionary<Guid, AgentScopeRef>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The scopes of these keys there are, by key as given; missing ones are left out.</summary>
    Task<IReadOnlyDictionary<string, AgentScopeRef>> FindManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken);

    /// <summary>Why an agent can't be given this role in the scope (it doesn't exist, or administers the scope); null when it can.</summary>
    Task<string?> CantGiveAsync(Guid scopeId, string roleKey, CancellationToken cancellationToken);

    /// <summary>Gives the account the role in the scope.</summary>
    Task GrantAsync(Guid scopeId, string roleKey, Guid accountId, Guid by, CancellationToken cancellationToken);

    /// <summary>Takes back every role the account has in the scope.</summary>
    Task RevokeAsync(Guid scopeId, Guid accountId, CancellationToken cancellationToken);
}

/// <summary>An entry in the host's audit log: what happened, to what, and the details.</summary>
public interface IAgentAudit
{
    Task WriteAsync(string type, string targetType, Guid? targetId, string targetName, object? details, CancellationToken cancellationToken);
}

/// <summary>The site's state that holds agents: safe mode, where nothing runs on its own.</summary>
public interface IAgentSite
{
    bool SafeMode { get; }

    /// <summary>
    /// Why agents can't be turned on now, in words for the admin (ADR 0086 section 7: Toda wants the site's monthly AI
    /// ceiling first); null when they can. The default lets them.
    /// </summary>
    Task<string?> WhyNotOnAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
