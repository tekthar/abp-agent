# Hosting

## The seams

Each seam is an interface in `Toda.Agents.Contracts`. Register yours in your module with
`context.Services.Replace(ServiceDescriptor.Transient<ISeam, Yours>())`; the package's module adds its defaults first.

| Seam | What it answers | Default |
|---|---|---|
| `IAgentAccounts` | make an agent's account (no sign-in, no tokens); who people are: name, username, kind (person, service, agent), active; optionally, the account's name in other languages and renaming it when the agent is renamed | none: you must give one (the names by language and the rename have defaults: one name, kept as made) |
| `IAgentRights` | is the actor a site admin; does the actor administer a scope | none |
| `IAgentScopes` | a scope by key (when the actor may read it), by id, by keys; why a role can't be given; grant and take back an agent's role | none |
| `IAgentItemKind` (one per kind) | see [items](items.md) | none |
| `IAgentTools` | see [tools and models](tools-and-models.md) | none |
| `IAgentModels` | one reply to a conversation | none |
| `IAgentActingScope` | put an act under the agent's name in your stores, for its duration | nothing to set |
| `IAgentRunQueue` | work a run's turns in the background | the package's worker |
| `IAgentEvents` | a run ended: tell your rules, move a workflow step on; a run asked someone: tell them; a day's cap stopped a run: tell the agent's responsible person | nobody is told |
| `IAgentAudit` | write an entry in your audit log | the log |
| `IAgentSite` | is the site in safe mode | never |

## The services

| Service | What for |
|---|---|
| `AgentService` | agents: list, get, make (site or scope), change, pause, take on and off, the switch |
| `AgentRunService` | runs: ask, start for a trigger (`StartAsync`), get (as the reader may see it), an item's runs, stop, ready to undo; questions: a person's open ones, answer, expire |
| `AgentPermissionService` | standing permissions: a person's own (`MineAsync`), an agent's (`ForAgentAsync`, for whoever administers it), and taking one back (`RemoveAsync`, the person or the agent's administrators) |
| `AgentRunner` | one turn of a run (`TurnAsync`), for your own queue; `FailAsync` when your queue gives up on one |

## Starting runs

Your host notices what starts a run (an assignment, a mention, a rule, a schedule, a workflow step) and calls
`AgentRunService.StartAsync` with an `AgentStart`. Its result has the run's id, or why none started, in words you may
show. Beside the agent, the item, the trigger, the prompt and who started it (`StartedBy`, null for the system):

| Member | What to give |
|---|---|
| `Cause` | an id for what caused it (your event's id): one cause starts one run, however often the event comes |
| `Exits` | for a step of a process (a workflow's), the exits the agent may choose: it then has its own tool `finish_step`, and `AgentRunFinished.Exit` says which it chose |
| `Chain` | your own chain of automation, in your words (Toda's is its rules' cause chain, one deeper). The run keeps it and hands it back on every act as `AgentActing.Chain`, so put it around what the run does, and your loop rules hold for agents' writes as for your rules' |
| `CausedInChangeSet` | the change set of the act that caused it, when you know it: an act of the agent's own run is in that run's change set, so it never starts the agent again (two agents that mention each other, an agent that mentions itself) |
| `ActorId` | whose rights a run no person started uses, when not the agent's own role: a rule's actor, a workflow's account. The run still counts as the system's for asking first: its questions go to the agent's responsible person, and no one's standing permissions apply |
| `Sender` | who outside the team started it (a customer's mention): one sender's runs are capped per day |

**What a step needs before it ends.** Register `IAgentExitChecks` and the package asks it before a run with a `Cause`
ends by an exit: the run's id, cause and item, the exit (null for the step's usual one) and when the run started. Answer
with what's still missing, in words the agent can act on ("the field Risk filled in (update_record)"); nothing missing,
an empty list. A `finish_step` whose exit isn't ready is refused with what's missing, and a final answer is answered with
it, at most `AgentRunner.MaxReminders` (2) times; after that the run ends as it is, and your step decides (Toda's leaves
by "failed"). Without a registration nothing is checked. The sample host's `SampleExitChecks` wants a ticket's priority
high before "solved".

Every agent's instructions also say where its work goes: in each place the request names (a field, a comment, the
description, a document, an item under this one), with its tools, then a short note of what it did; with no place named,
its answer is the work, posted on the item.

A run doesn't start, and says why, when agents are off, the agent is paused or its scope hasn't taken it on, whoever
started it (or whose rights it would use) can't read the item, the cause has a run already, the agent's own act caused
it, or a day's cap is reached. Past a cap, `IAgentEvents.RunsCappedAsync`
is called every time; tell the agent's responsible person once a day, as Toda does.

Services take an `AgentActor`. The ones only a site admin may call (`SwitchAsync`, `ListAsync`, `CreateSiteAsync`)
take the account's id: check it's a site admin before you call them, as the sample's controller does.

## Settings

The `Agents` section (`AgentsOptions`):

| Setting | Default | What |
|---|---|---|
| `AdminsMayEnable` | false | the operator's gate: a site admin may turn agents on only when it's true |
| `TranscriptDays` | 30 | how long transcripts are kept; an agent may say otherwise |
| `MaxTurns`, `MaxToolCalls`, `MaxTime` | 30, 60, 15 minutes | a run's limits |
| `QuestionTimeout` | 1 day | how long a run waits for an answer before it's refused |
| `MaxRunsPerAgentPerDay` | 200 | the runs one agent may start in a day, from every trigger |
| `MaxRunsPerSenderPerDay` | 20 | the runs one sender from outside the team may start in a day |
| `ProductName` | Toda | what the agent's persona says it works in |
| `ScopeNoun` | scope | what messages call a scope ("project", "desk") |
| `OperatorSettingName` | Agents:AdminsMayEnable | the setting the operator is told to set, if you bind another section |

To keep your own section's name, bind it yourself:

```csharp
context.Services.Configure<AgentsOptions>(configuration.GetSection("MyApp:Agents"));
context.Services.Configure<AgentsOptions>(o => o.OperatorSettingName = "MyApp:Agents:AdminsMayEnable");
```

## The database

`ConfigureAgents()` maps seven tables in schema `agents`:

| Table | What |
|---|---|
| `agents` | agents |
| `agent_scopes` | scopes that took an agent on, with its role there |
| `agent_settings` | the site's switch, one row |
| `agent_runs` | runs, with their conversation (jsonb) |
| `agent_run_steps` | each run's steps |
| `agent_questions` | runs' questions, their answers and who gave them |
| `agent_permissions` | people's standing permissions: an agent may run a tool in a scope in their runs without asking |

They need PostgreSQL: the run queue takes runs with `FOR UPDATE SKIP LOCKED`. Add them to your migrations with
`dotnet ef migrations add`, as you would your own change. A new version of the package that changes them says so in
the CHANGELOG. Since your context maps them, a test that your model has no change your migrations lack catches a
package update you haven't migrated for, with no database:

```csharp
Assert.False(db.Database.HasPendingModelChanges()); // EF Core 9 and later
```

Transcripts are personal data: what people asked, and what the model and tools read and wrote. List `agent_runs` and
`agent_run_steps` in your record of processing, and include them when someone asks for their data (runs they started:
`AgentRun.StartedBy`; runs their words started from outside the team: `AgentRun.Sender`). When you merge two accounts,
move `ResponsibleId`, `CreatedBy`, `UpdatedBy`, `PausedBy`, `TakenBy`, `StartedBy`, `ActorId`, `Sender`, a question's
`AskedOf` and `AnsweredBy`, and a permission's `PersonId` (dropping one the target has already); an agent's own
`AccountId` stays.

## Error codes and HTTP

The package throws ABP's exceptions. Two codes have their own status:

| Code | Status | When |
|---|---|---|
| `Toda.Agents:Forbidden` (`AgentErrorCodes.Forbidden`) | 403 | the actor may not administer the agent or scope, or stop the run |
| `Toda.Agents:Conflict` (`AgentErrorCodes.Conflict`) | 409 | undoing a run that's still going; answering a question answered already |

Map them with `AbpExceptionHttpStatusCodeOptions`, as the sample does. Other refusals are `UserFriendlyException` with
a message for the person; something not found is `EntityNotFoundException`.

## The run queue, and several servers

The default queue is the package's worker (`AgentRunWorker`), on every server. It takes the oldest run that's waiting
(queued, or running with its lease run out), leases it for two minutes, works its turns, and renews the lease after each
one. A server that stops mid-run leaves its lease to run out, and another server takes the run over; the step that was
started is then unknown, as [concepts](concepts.md) explains.

A host with a job system replaces `IAgentRunQueue`: queue a job in `EnqueueAsync` (return its id, which is kept on the
run), and have the job call `AgentRunner.TurnAsync` until it says the run is over. Toda runs agents as its jobs this
way. The package's worker stands aside when the queue isn't its own.
