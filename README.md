# AbpAgent

AI agents as teammates, for ABP applications on .NET 10 and PostgreSQL. An agent has an account of its own and works in
runs: someone asks it to do something with an item (a ticket, a record, a listing), and it reads, uses your tools with
that person's rights, and answers, under its own name. Every change it makes is in one change set you can undo, and
every step it takes is written down. AbpAgent is independent of any one product: a host gives accounts, rights, scopes, items, tools
and a model through the seams, so any product can give its people agents that work on its own things.

> **Preview.** 0.x versions may change their API in any minor version (SemVer). Pin an exact version.

> **Not a Volosoft project.** [ABP Framework](https://abp.io) is made and owned by [Volosoft](https://volosoft.com).
> AbpAgent is an independent enhancement that works on ABP: this repository and its packages don't belong to
> Volosoft, and aren't affiliated with or endorsed by them.

## What it does

- **Agents with accounts of their own:** made for the site (offered to every scope or to named ones) or for one scope
  (a project, a desk), each with instructions, a model, a person who answers for it, and settings for each tool: it
  runs, it asks first, or it's off. Pausing an agent stops its runs.
- **Runs that outlive a restart:** a run takes a turn at a time, keeps its conversation, and moves between servers.
  Each step is written as started before it happens and finished after, so a write whose result isn't known is never
  repeated: the agent is told to check first.
- **No more rights than the person who asked:** a run uses the asker's rights, under the agent's name, so history can
  say "Analyst, run by Sara". A tool that only reads runs read-only, so a tool marked wrongly still can't write.
- **Asking first:** a write the agent's settings don't let run makes the run ask the person who started it (or the
  agent's responsible person) and wait: allow, allow for this run, always allow, or refuse. People see the standing
  permissions they gave and take them back, and each step says what let it run.
- **Safety rules that follow what a run read:** once a run has read outside content (a customer's words, another
  system's) or something beyond its scope, every write asks, and its answer stays where only the right people see it.
- **Triggers:** a person asks; the host starts runs for an assignment, a mention, a rule, a schedule or a workflow step,
  by a person or by the system with the agent's own role (or rights the host lends it, a rule's), once per cause and
  one run per item at a time.
- **No loops, no floods:** an agent's own acts never start it, a run carries the host's chain of automation so the
  host's loop rules hold for it, and a day's caps bound the runs each agent, and each sender from outside the team,
  may start.
- **A cleaner for what agents write:** Markdown is parsed, and images, links and HTML that could carry data out when
  shown become text.
- **Transcripts let go:** what the model and each tool were given and gave back goes after the site's period, or the
  agent's own; who, when and each step's outcome stay.
- **A run queue of its own:** a worker on each of your servers takes runs with a lease. A host with a job system of its
  own replaces it.

The package keeps seven tables in the `agents` schema of your database, mapped in your own EF Core context.

## Packages

On [nuget.org](https://www.nuget.org/packages?q=AbpAgent):

| Package | What it holds |
|---|---|
| `AbpAgent.Contracts` | The contracts a host implements: accounts, rights, scopes, item kinds (`IAgentItemKind`), tools (`IAgentTools`), the model (`IAgentModels`), acting, the run queue, the audit. Nothing but .NET. |
| `AbpAgent` | The ABP module: agents and runs, the runner, the cleaner, transcripts, the run queue, and the EF Core model you map. |

## Use it in a host

`samples/AbpAgent.SampleHost` is a complete host: a help desk's tickets in their own table, worked on by agents with
a scripted model. [Getting started](docs/getting-started.md) walks through it. In short:

```csharp
// 1. Depend on the module.
[DependsOn(typeof(AbpAgentModule), typeof(AbpAspNetCoreMvcModule))]
public class MyModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 2. Your context stands in for the package's.
        context.Services.AddAbpDbContext<MyDbContext>(o => o.ReplaceDbContext<IAgentsDbContext>());
        // 3. What only you know: who's who, who may administer, your scopes, your tools, your model.
        context.Services.Replace(ServiceDescriptor.Transient<IAgentAccounts, MyAccounts>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentRights, MyRights>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentScopes, MyScopes>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentTools, MyTools>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentModels, MyModel>());
    }
}

// 4. Your context maps the agents' tables beside yours.
[ReplaceDbContext(typeof(IAgentsDbContext))]
public class MyDbContext(DbContextOptions<MyDbContext> options) : AbpDbContext<MyDbContext>(options), IAgentsDbContext
{
    public DbSet<Agent> Agents => Set<Agent>();
    // ... the other four DbSets of IAgentsDbContext
    protected override void OnModelCreating(ModelBuilder b) { base.OnModelCreating(b); b.ConfigureAgents(); }
}

// 5. Tell the package about your items: find, read, who may, what keys bring in, the answer.
[ExposeServices(typeof(IAgentItemKind))]
public class TicketItems : IAgentItemKind, ITransientDependency { /* see docs/items.md */ }

// 6. Ask an agent.
AgentRunInfo run = await runs.AskAsync(new AgentActor(me), "ticket", "DESK-12", agentId, "Triage it", ct);
```

## Guides

- [Getting started](docs/getting-started.md): a host from nothing to an agent that triages a ticket, step by step.
- [Concepts](docs/concepts.md): agents, scopes, runs, steps, actors and acting, change sets, transcripts.
- [Items](docs/items.md): `IAgentItemKind`, reading an item for a run, what keys bring in, the answer.
- [Tools and models](docs/tools-and-models.md): `IAgentTools` and `IAgentModels`, the conversation's shape, limits.
- [Safety](docs/safety.md): the rules a run follows, and what the host must do for them to hold.
- [Hosting](docs/hosting.md): every seam with its default, settings, the database and migrations, error codes and
  HTTP, the run queue and several servers.
- [Troubleshooting](docs/troubleshooting.md): the messages you may meet, and what to do.

Design records are in `docs/adr`: [0001](docs/adr/0001-independent-of-any-host.md) is why the package stands alone and
how that's kept.

## Build and test

You need the .NET 10 SDK. The host tests also need PostgreSQL: `localhost:5432` with user and password `postgres` (the
official image's defaults), or the server `ABPAGENT_TEST_POSTGRES` names.

```bash
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:17
dotnet build AbpAgent.slnx -c Release
dotnet test AbpAgent.slnx -c Release
dotnet pack AbpAgent.slnx -c Release -o artifacts
```

To try the sample, run it and call its API with an `X-Account` header (the sample has no sign-in):

```bash
dotnet run --project samples/AbpAgent.SampleHost --urls http://localhost:5320
```

## Releasing

The version is `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`, and `CHANGELOG.md` needs an entry for
it. Pushing the tag `v<version>`, such as `v0.2.0`, runs the checks. GitHub Actions then pushes the packages
to nuget.org and makes a GitHub release.

Publishing uses nuget.org's [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing),
so no API key is stored anywhere: nuget.org gives the release job a key that lasts an hour. Set it up once:

1. **On nuget.org**, signed in as the account (or organization) that will own the packages: your name, **Trusted
   Publishing**, add a policy with owner `tekthar`, repository `abp-agent`, workflow file `ci.yml` (the file name only)
   and environment `release`. Let it publish new packages, for packages matching `AbpAgent*`.
2. **On GitHub**, in the repository's **Settings**:
   - **Environments:** add `release`. Limit it to tags `v*` and, if you like, require a reviewer, so a tag waits for
     a person before anything is pushed.
   - **Secrets and variables, Actions, Variables:** add `NUGET_USER`, the nuget.org profile name (not the email) of
     the account in step 1.
3. **Release:** push the tag, `git tag v<version> && git push origin v<version>`.

## Contributing

Issues and pull requests are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md), and [SECURITY.md](SECURITY.md) for
vulnerabilities. CI runs on GitHub Actions for every pull request.

## License

Apache License 2.0: see [LICENSE](LICENSE) and [NOTICE](NOTICE).
