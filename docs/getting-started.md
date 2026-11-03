# Getting started

This walks through `samples/Toda.Agents.SampleHost`: a help desk whose tickets live in its own table, and an agent
that triages them. Everything here is in the sample; read its files beside this page.

## 1. The module and your context

Depend on `TodaAgentsModule`, and let your EF Core context stand in for the package's, so its tables are yours: your
migrations make them, and your queries can join them.

```csharp
[DependsOn(typeof(AbpAutofacModule), typeof(AbpAspNetCoreMvcModule), typeof(TodaAgentsModule))]
public class SampleHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<SampleDbContext>(options => options.ReplaceDbContext<IAgentsDbContext>());
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
    }
}

[ConnectionStringName("Default")]
[ReplaceDbContext(typeof(IAgentsDbContext))]
public class SampleDbContext(DbContextOptions<SampleDbContext> options) : AbpDbContext<SampleDbContext>(options), IAgentsDbContext
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentScope> AgentScopes => Set<AgentScope>();
    public DbSet<AgentSettings> AgentSettings => Set<AgentSettings>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentRunStep> AgentRunSteps => Set<AgentRunStep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureAgents(); // schema "agents"
        // ... your own entities
    }
}
```

The sample makes its tables with `EnsureCreated`. A real host runs `dotnet ef migrations add` as it does for its own
changes, and the migration includes the agents' tables.

## 2. Turn agents on

Agents are off until two people agree: the operator, in the settings, and a site admin, with the switch.

```json
{ "Agents": { "AdminsMayEnable": true } }
```

```csharp
await agents.SwitchAsync(adminId, enabled: true, ct);
```

A copy of production comes up with agents off unless its settings say otherwise. See [hosting](hosting.md) for every
setting.

## 3. Who's who, and who may

Five seams have no default, because only you know the answers:

| Seam | The sample's |
|---|---|
| `IAgentAccounts` | makes an agent's account in its own `accounts` table; people come from `appsettings.json` |
| `IAgentRights` | the admins in the settings administer the site and every desk |
| `IAgentScopes` | desks are the scopes; an agent a desk takes on gets the role `helper` there |
| `IAgentTools` | `get_ticket`, `set_priority`, `add_note`, `list_desks`, `who_is_who` |
| `IAgentModels` | a scripted model, so the sample needs no provider |

Register yours in place of the package's (`context.Services.Replace(...)`). The package keeps its defaults for the rest:
the run queue, the audit (the log), safe mode (never) and acting (nothing to set). [Hosting](hosting.md) lists them.

## 4. Your items

One class per kind of item says how agents see it. The sample's tickets:

```csharp
[ExposeServices(typeof(IAgentItemKind), typeof(TicketItems))]
public sealed class TicketItems(SampleDb sampleDb, SampleDirectory directory) : IAgentItemKind, ITransientDependency
{
    public string Kind => "ticket";
    public string Noun => "ticket"; // the run's request shows the item as <ticket key="...">
    public bool IsKey(string key) => ...;                      // "DESK-12"
    public Task<AgentItemRef?> FindAsync(string key, ...)     // by key
    public Task<AgentItemRef?> GetAsync(Guid id, ...)          // by id
    public Task<AgentItemAccess> AccessAsync(AgentActor actor, AgentItemRef item, ...) // read, change, manage, internal
    public Task<string?> ReadAsync(AgentActing acting, AgentItemRef item, ...)        // the ticket as JSON, with the run's rights
    public Task<AgentBrought> BroughtAsync(Guid scopeId, AgentItemRef? item, IReadOnlyCollection<string> keys, ...) // outside? beyond?
    public Task AnswerAsync(AgentActing acting, AgentItemRef item, string text, bool internalOnly, ...)             // a note
}
```

[Items](items.md) says what each member must do.

## 5. An agent, taken on

```csharp
AgentInfo triage = await agents.CreateSiteAsync(adminId, new AgentChange(
    Key: "triage", OfferAll: true, Instructions: "Read the ticket and set its priority.",
    Tools: new() { ["set_priority"] = AgentToolSettings.Run }), ct);
await agents.TakeAsync(new AgentActor(adminId), "DESK", triage.Id, "helper", ct);
```

A tool not named in `Tools` reads without asking if it only reads, and asks first if it can change things. Until
asking is built (it's planned), "asks first" means the run is refused the write and told to say what it would have
done.

## 6. Ask it

```csharp
AgentRunInfo run = await runs.AskAsync(new AgentActor(benId), "ticket", "DESK-1", triage.Id, "Triage it", ct);
```

The run is queued and the package's worker takes it. Read it as it goes with `runs.GetAsync(actor, run.Id, ct)`:

- its state (`queued`, `running`, `done`, `failed`, `stopped`);
- each step: the model's replies and each tool call, with its outcome;
- its answer, which also went on the ticket as a note by Ben, run by the agent.

Ben sees the transcript (what the model and each tool were given and gave back), because he started the run. Cy, who
reads the ticket, sees that a run happened but not its steps.

## 7. Prove it

`tests/Toda.Agents.Host.Tests` drives the sample through its API on a database of its own. Run them:

```bash
dotnet test tests/Toda.Agents.Host.Tests
```

Then read [safety](safety.md) before you give an agent tools that write.
