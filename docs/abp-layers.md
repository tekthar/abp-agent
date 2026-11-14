# For ABP developers: where ABP's layers are

AbpAgent is an ABP module, but it isn't split into ABP's standard module layers. This page maps one onto the other, so
you can find things where you'd look for them.

| ABP's layer (`abp new -t module`) | In AbpAgent |
|---|---|
| `*.Domain.Shared` (constants, enums, error codes) | `AbpAgent.Contracts`: the records AbpAgent and its host pass each other (`AgentActor`, `AgentTool`, `AgentActing`, `AgentModelRequest`…); `AgentErrorCodes`, `AgentRunErrorCodes`, `AgentChatEventTypes` and `AgentAnswers` are in `AbpAgent` |
| `*.Domain` (entities, domain services) | `AbpAgent`: `Agents.cs` and `Runs.cs` (agents, runs and their steps, questions, standing permissions, memories, chats and their events), `AgentRunner`, `AgentRunLine` |
| `*.Application.Contracts` (DTOs, service interfaces) | `AbpAgent.Contracts` (the host's seams: `IAgentAccounts`, `IAgentRights`, `IAgentScopes`, `IAgentItemKind`, `IAgentTools`, `IAgentModels`, `IAgentEmbeddings`, `IAgentRunQueue`…) and the records beside the services in `AbpAgent` (`AgentInfo`, `AgentChange`, `AgentRunInfo`, `AgentChatInfo`, `AgentMemoryInfo`…) |
| `*.Application` (application services) | `AbpAgent`: `AgentService`, `AgentRunService`, `AgentChatService`, `AgentMemoryService`, `AgentPermissionService` |
| `*.EntityFrameworkCore` | `AbpAgent`: `IAgentsDbContext` and `modelBuilder.ConfigureAgents()` (`AgentsModel.cs`), schema `agents`; the host's own context and migrations hold the tables |
| `*.HttpApi` | `AbpAgent.HttpApi`: the agents' API's controllers, under a prefix the host sets ([docs/http-api.md](http-api.md)) |
| `*.HttpApi.Client` | Not yet: the one client is `@abpjs/agent`, generated from the OpenAPI document |
| `*.Web` / `*.Blazor` | `@abpjs/agent` (React), to be released with the packages |
| An integration package | `AbpAgent.Graphiti`: agents' memory as a temporal knowledge graph, on graphiti-net |

## What works the ABP way

- **Modules:** `AbpAgentModule`, `AbpAgentGraphitiModule`, `AbpAgentHttpApiModule`, each with `[DependsOn]`.
- **The database:** replace `IAgentsDbContext` with your own context (`ReplaceDbContext<IAgentsDbContext>()`), call
  `modelBuilder.ConfigureAgents()` in its `OnModelCreating`, and add a migration as for your own tables.
  `AbpAgent.Graphiti` makes its own tables at start, in schema `agents_graph`.
- **Options:** `AgentsOptions` from the `Agents` section of configuration, `AgentGraphOptions` from `Agents:Graph`,
  `AgentHttpApiOptions` from `Agents:HttpApi`.
- **Replacing a part:** every seam is a service you replace in `ConfigureServices`
  (`context.Services.Replace(ServiceDescriptor.Transient<IAgentRights, MyRights>())`).
- **Errors:** `UserFriendlyException` with `AbpAgent:*` codes (`AgentErrorCodes`); `AbpAgent.HttpApi` maps them to 403
  and 409 in `AbpExceptionHttpStatusCodeOptions`.
- **Background work:** hosted services: `AgentRunWorker` (the run queue, unless the host gives its own
  `IAgentRunQueue`), `AgentTranscripts` (old transcripts), `AgentQuestionExpiry` and `AgentGraphWorker` (reading notes
  into the graph).

## Where it differs, and why

- **Services, not application services:** `AgentService` and the others are plain services that take the caller
  (`AgentActor`) as a parameter, so a run, a worker and a person go through the same checks; there are no
  `IAgentAppService` interfaces and no auto API controllers.
- **Rights are `IAgentRights` and each item kind's access, not ABP permissions:** who administers an agent is per scope
  (a project's admins, the site's), and whether a run may read or change something is the item's own rule
  (`IAgentItemKind.AccessAsync`), which ABP's permission definitions can't say. Your implementation can read ABP
  permissions underneath.
- **One main package:** domain, services and EF Core are in `AbpAgent`, so a host adds one module, not five. A split into
  ABP's layers is a decision for before 1.0.
- **Units of work:** the services open their own (`AgentsDb.RunAsync`), since a run's steps commit as they go and a
  worker has no request around it.
- **Texts:** messages are in English in code; there are no localization resources yet.
- **Settings:** the site's switch for agents is a row in schema `agents` (`AgentSettings`), set through
  `AgentService.SwitchAsync`, not ABP's setting management; the rest is configuration (`AgentsOptions`).

Possible later additions: ABP permission definitions a host may map onto `IAgentRights`, and `AbpAgent.HttpApi.Client`.
