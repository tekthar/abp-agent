# Hosting

## The seams

Each seam is an interface in `AbpAgent.Contracts`. Register yours in your module with
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
| `Chain` | your own chain of automation, in your words (a rules engine's cause chain, one deeper). The run keeps it and hands it back on every act as `AgentActing.Chain`, so put it around what the run does, and your loop rules hold for agents' writes as for your rules' |
| `CausedInChangeSet` | the change set of the act that caused it, when you know it: an act of the agent's own run is in that run's change set, so it never starts the agent again (two agents that mention each other, an agent that mentions itself) |
| `ActorId` | whose rights a run no person started uses, when not the agent's own role: a rule's actor, a workflow's account. The run still counts as the system's for asking first: its questions go to the agent's responsible person, and no one's standing permissions apply |
| `Sender` | who outside the team started it (a customer's mention): one sender's runs are capped per day |

**What a step needs before it ends.** Register `IAgentExitChecks` and the package asks it before a run with a `Cause`
ends by an exit: the run's id, cause and item, the exit (null for the step's usual one) and when the run started. Answer
with what's still missing, in words the agent can act on ("the field Risk filled in (update_record)"); nothing missing,
an empty list. A `finish_step` whose exit isn't ready is refused with what's missing, and a final answer is answered with
it, at most `AgentRunner.MaxReminders` (2) times; after that the run ends as it is, and your step decides (it may leave
by "failed"). Without a registration nothing is checked. The sample host's `SampleExitChecks` wants a ticket's priority
high before "solved".

Every agent's instructions also say where its work goes: in each place the request names (a field, a comment, the
description, a document, an item under this one), with its tools, then a short note of what it did; with no place named,
its answer is the work, posted on the item.

**Limits and memory.** Runs stop at their limits (the agent's own, else `AgentsOptions`), wait their turn past the
runs-at-once caps, and a person has only so many runs going. Your `IAgentSite.WhyNotOnAsync` can refuse turning agents
on until something is set up (the site's monthly AI cost limit, say). Agents keep short memories about items:
answer `IAgentItemKind.NeighboursAsync` with the items linked to one, as the actor reads, and `recall` looks near
them too. Your host maps `agents.agent_memories` (`IAgentsDbContext.AgentMemories`); on erasing a person, call
`AgentMemoryService.ForgetPersonAsync`.

**Graph memory.** With the `AbpAgent.Graphiti` package (depend on `AbpAgentGraphitiModule`), each note an agent keeps
is read after its run into a graph of the things it names and the facts between them (graphiti-net, on your PostgreSQL
with the pgvector extension), one graph per agent per scope; recall gives the matching facts beside the notes, each with
the notes it came from, and only when the run's person may read every one of them now. You give `IAgentEmbeddings` (your
AI gateway's embeddings, with its aliases and limits) beside `IAgentModels`; the graph's model calls go through
`IAgentModels` as the agent's account, their purpose `agent:{key}:memory:{prompt}` (a demo model can answer them by
rule, as the sample host's does). The graph's tables are made at start in schema `agents_graph`; without pgvector, or
on a read-only replica, the graph stays off and agents keep their notes. Settings: `Agents:Graph` (`Enabled`,
`EmbeddingAlias` "embed", `Dimensions` 1024, which is part of the tables, `SmallModelAlias`, `TextSearchConfig`).
A note forgotten, deleted with its chat or agent, or erased with its person leaves recall at once and the graph within
a minute. The graph is made from the notes, so one lost can be made again: clear `GraphedAt` on the notes.
To let an agent's admins browse it, serve `AgentMemoryService.GraphAsync`: in one scope, the things (by words in their
names or facts), and around one thing its facts with the notes they came from, under the same rule; it calls no model.

**The HTTP API.** With `AbpAgent.HttpApi` (depend on `AbpAgentHttpApiModule`), the services above are served under one
prefix (`Agents:HttpApi:RoutePrefix`, `api/agent` by default), so every host has the same agents' API and the same
screens can use it. You say who is calling through `IAgentHttpCaller` (the default is ABP's signed-in user; give your
own to carry your rights object in `AgentActor.Principal`). What's yours stays in your own routes: outside connections,
AI limits, pictures, undo. The routes: [docs/http-api.md](http-api.md).

**Chats.** `AgentChatService` gives a person their own chats with an agent: in a scope they may read, or
about an item there. Each message is a run with the trigger `chat` and their rights, so every rule above holds; it gets
the chat so far as the model's own turns (the newest 20 exchanges within 60,000 characters) and starts marked as the
chat's earlier runs were. Its reply stays in the chat: the package doesn't post it on the item, hides it from everyone
but the person (and your site admins, who read transcripts), and leaves chat runs off the item's run list until they
change something there. `PostAsync` posts one reply, once, through your item kind's `AnswerAsync`; `DeleteAsync` and
the static `ForgetAsync` (for erasing a person) leave runs who, when and each step's outcome. Your host maps
`agents.agent_chats` (`IAgentsDbContext.AgentChats`) and gives the routes; the sample host's `agent-chats` routes and
its two chat tests show the whole of it. You'll want to skip your own "run finished" event for `AgentTriggers.Chat`, so
no rule acts on what someone asked in private.

**Live chats.** A chat's person sees the reply as it's written and each step as it happens. Three parts:

- **Your model streams**, if it can: `IAgentModels.StreamAsync` gives the Messages API's events (`content_block_delta`
  with `text_delta`, and so on), then `AgentModelEvent.Finished` with the whole reply, its model, tokens and cost, as
  `CompleteAsync` would answer. Without it the package calls `CompleteAsync`, and the text arrives at the turn's end. A
  stream that fails partway ends with the Messages API's `error` event, or throws; the run then fails with the error and
  keeps no answer. Any other exception from your model fails the run too, with "its call failed".
- **You serve its events**: `AgentChatService.EventsAsync(actor, chatId, after, ct)` checks the chat is the actor's
  (not found otherwise) and gives its events in order while a run of it goes: `turn` (a model turn began), `text` (a
  Markdown block of the reply, cleaned before it's sent, to append to its turn's text), `step` (a tool call started,
  `outcome` null, and finished, with the step's `id` and never its input or output), `question` (the run asks its
  person), `run` (it ended: `state`, `error`, and `code`, the run's `ErrorCode`), and `ping`
  when nothing has come for a while. Serve it as server-sent events with each event's `Id` as its id, and take
  `Last-Event-ID` as `after`: a reader that drops comes back and gets what it missed. The stream ends once the run's
  end is given, or after `AgentsOptions.ChatEventsMost`. The sample host's `agent-chats/{id}/events` route is the whole
  of it. The reply the run keeps is cleaned whole and replaces what was streamed.
- **A chat's run starts here**: with room, the server that took the message works it at once
  (`AgentsOptions.ChatRunsStartHere`), holding the run's lease, rather than leave it behind other work in your queue. If
  that server stops, the lease runs out and the run goes back to your queue (or the package's worker) from its last step.

A reader on the server that works the run is woken as each event is written; one on another server looks every
`ChatEventsPoll` (half a second).

A run doesn't start, and says why, when agents are off, the agent is paused or its scope hasn't taken it on, whoever
started it (or whose rights it would use) can't read the item, the cause has a run already, the agent's own act caused
it, or a day's cap is reached. Past a cap, `IAgentEvents.RunsCappedAsync`
is called every time; tell the agent's responsible person once a day, say.

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
| `ProductName` | this system | what the agent's persona says it works in: set your product's name |
| `ScopeNoun` | scope | what messages call a scope ("project", "desk") |
| `OperatorSettingName` | Agents:AdminsMayEnable | the setting the operator is told to set, if you bind another section |

To keep your own section's name, bind it yourself:

```csharp
context.Services.Configure<AgentsOptions>(configuration.GetSection("MyApp:Agents"));
context.Services.Configure<AgentsOptions>(o => o.OperatorSettingName = "MyApp:Agents:AdminsMayEnable");
```

## The database

`ConfigureAgents()` maps these tables in schema `agents`:

| Table | What |
|---|---|
| `agents` | agents |
| `agent_scopes` | scopes that took an agent on, with its role there |
| `agent_settings` | the site's switch, one row |
| `agent_runs` | runs, with their conversation (jsonb) |
| `agent_run_steps` | each run's steps |
| `agent_questions` | runs' questions, their answers and who gave them |
| `agent_permissions` | people's standing permissions: an agent may run a tool in a scope in their runs without asking |
| `agent_chats` | people's chats with agents |
| `agent_memories` | what agents keep about items |
| `agent_chat_events` | what a chat's person sees happen while its run goes; kept a day at most, and only the last run's |

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
| `AbpAgent:Forbidden` (`AgentErrorCodes.Forbidden`) | 403 | the actor may not administer the agent or scope, or stop the run |
| `AbpAgent:Conflict` (`AgentErrorCodes.Conflict`) | 409 | undoing a run that's still going; answering a question answered already |

Map them with `AbpExceptionHttpStatusCodeOptions`, as the sample does. Other refusals are `UserFriendlyException` with
a message for the person; something not found is `EntityNotFoundException`.

## The run queue, and several servers

The default queue is the package's worker (`AgentRunWorker`), on every server. It takes the oldest run that's waiting
(queued, or running with its lease run out), leases it for two minutes, works its turns, and renews the lease after each
one. A server that stops mid-run leaves its lease to run out, and another server takes the run over; the step that was
started is then unknown, as [concepts](concepts.md) explains.

A host with a job system replaces `IAgentRunQueue`: queue a job in `EnqueueAsync` (return its id, which is kept on the
run), and have the job call `AgentRunner.TurnAsync` until it says the run is over. A host with its own jobs runs agents this
way. The package's worker stands aside when the queue isn't its own.
