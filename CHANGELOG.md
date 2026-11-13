# Changelog

All notable changes to AbpAgent are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

Browsing an agent's graph. Nothing for a host to migrate.

### Added

- `AgentMemoryService.GraphAsync`: an agent's graph in one scope for its admins, to browse with no model calls. It gives
  the things (those whose names or facts have the words, when given; most connected first, at most 200), and around one
  thing its facts, the things they reach, and the notes they came from (`AgentGraphPage`, `AgentGraphView`,
  `AgentGraphThing`, `AgentGraphLink`, `AgentGraphNote`), and how many of the scope's notes aren't in the graph yet. It
  follows recall's rule: a fact shows only when the reader may read every note it came from; a thing, when a note they may
  read names it, with its summary only when they may read every note that names it; facts left out are counted.
- `IAgentMemoryGraph.LookAsync` and `IAgentMemoryGraph.Why`, with defaults, so another graph implementation still builds;
  `AgentGraph` (AbpAgent.Graphiti) reads its tables for them.
- `AgentActing.ChatId`: the chat a run answers in, for a tool that keeps something between a chat's turns.

## [0.4.0] - 2026-11-13

Graph memory. What a host changes: a migration for four new `agent_memories` columns (`GraphedAt`, `GraphEpisode`,
`GraphFailures`, `GraphLeasedUntil`); to have the graph, depend on `AbpAgentGraphitiModule` (package `AbpAgent.Graphiti`),
give `IAgentEmbeddings`, and run PostgreSQL with pgvector. Without it, nothing changes.

### Added

- `AbpAgent.Graphiti`: agents' memory as a temporal knowledge graph, with graphiti-net 0.1.0 on PostgreSQL and pgvector.
  Each note an agent keeps is read, after its run, into the things it names and the facts between them, in a graph per
  agent per scope (`agent-{agent}-{scope}`); recall also gives the facts that match, each with the notes it came from,
  only when the run's person may read every one of them now. The graph's tables are made at start in their own schema
  (`Agents:Graph:Schema`, `agents_graph`); without pgvector, or on a read-only replica, it stays off and agents keep their
  notes. Its model calls go through `IAgentModels` as the agent's account (purpose `agent:{key}:memory:{prompt}`), its
  embeddings through `IAgentEmbeddings` (`Agents:Graph:EmbeddingAlias`, `embed`; `Dimensions`, 1024). A forgotten,
  deleted or erased note leaves recall at once and the graph at the next sweep (strict removal). One write at a time per
  graph, across servers (a PostgreSQL advisory lock).
- `IAgentEmbeddings`, `AgentEmbedRequest` and `AgentEmbedding`: a host's embedding models; the default has none.
- `IAgentMemoryGraph`, `AgentGraphRecall` and `AgentGraphFact`: the seam recall asks, when a graph is there.
- `AgentMemory.GraphedAt`, `GraphEpisode`, `GraphFailures` and `GraphLeasedUntil`.

## [0.3.1] - 2026-11-12

### Fixed

- An application with no generic host (an ABP integrated test, a console host made with `AbpApplicationFactory`) couldn't
  make `AgentRunHere`, so nothing that queues a run could be made either: 0.3.0 took `IHostApplicationLifetime` in its
  constructor. It's optional now; with no host, a chat's run started here stops when the application's services are
  disposed.

## [0.3.0] - 2026-11-12

Live chats (A1). What a host changes: map the new table (`IAgentsDbContext.AgentChatEvents`, `agents.agent_chat_events`)
and the new column (`agent_runs.ErrorCode`) and add a migration; serve `AgentChatService.EventsAsync` as server-sent
events (see docs/hosting.md, "Live chats"); optionally stream from your model with `IAgentModels.StreamAsync`.

### Added

- `IAgentModels.StreamAsync`, with a default that calls `CompleteAsync`: the Messages API's events, then
  `AgentModelEvent.Finished` with the whole reply. The runner streams every model turn through it.
- Chats' events: `AgentChatEvent` (`agents.agent_chat_events`), `AgentChatEventTypes` (`turn`, `text`, `step`,
  `question`, `run`, `ping`) and `AgentChatService.EventsAsync`, which checks the chat is the actor's, then gives its
  events in order while a run goes, resumable after an event's id. A chat's events go when its next run starts, when it's
  deleted or its person erased (`AgentChatService.ForgetAsync`), and after a day.
- `AgentTextStream`: a reply's text a Markdown block at a time (and a plain paragraph a sentence at a time), each piece
  cleaned before it's given, and given only if what's been given with it still cleans to itself.
- A chat's run starts on the server that took its message when there's room (`AgentRunHere`,
  `AgentsOptions.ChatRunsStartHere`, on by default), holding its lease; a run whose lease ran out with no job goes back
  to the queue (`AgentRunLine.QueueWaitingAsync`).
- `AgentsOptions.ChatEventsPoll` (half a second) and `ChatEventsMost` (ten minutes).
- `AgentRun.ErrorCode` and `AgentRunInfo.ErrorCode`: what kind of error ended a run, for a host to say in its people's
  language (`AgentRunErrorCodes`: `model_failed`, `broke_off`, `limits`, `run_cost`, `tool_failures`, `held`, `server`;
  or the host model's own refusal code, or the code a stream's `error` event gave as its `type`). A chat's `run` event
  carries it as `code`; its `step` and `question` events carry the step's `id`, never its input or output.

### Changed

- Any exception from the host's model, not only `AgentModelRefusedException`, ends the run Failed with "The model
  didn't answer: its call failed (…)", and a stream that breaks off ends it with "The model's reply broke off (code)."; the
  provider's own words are only logged, and neither keeps an answer. A server stopping still leaves the run to be taken
  over.
- The package's worker renews a run's lease while it works it, not only between turns, so one long turn on a site with
  several servers isn't taken over by another midway.
- Chats' events past a day go with the transcripts' sweep too, so they never outlive a run's transcript.

### Fixed

- `AgentText.Clean` left a link uncleaned in a list item when its text was `x`, `X` or a space (`- see [x](address)`):
  Markdig read it as a task list's checkbox, where a browser reads a link. The cleaner no longer reads task lists, so
  every `[x](address)` is a link to it, and is cleaned.

## [0.2.1] - 2026-11-12

### Fixed

- Making an agent (`CreateSiteAsync`, `CreateForScopeAsync`) now keeps the `AgentChange.Memory` and
  `AgentChange.Limits` it is given. Before, only `UpdateAsync` applied them, so an agent made with `Memory = false`
  still kept memories, and its own limits waited for a change. A host that changed them right after making the
  agent can drop that call.

## [0.2.0] - 2026-11-10

The first release as AbpAgent, from its own repository, https://github.com/tekthar/abp-agent. The releases below
were published under the old ids.

### Changed

- **Renamed to AbpAgent**, so the package names no product it came from (docs/adr/0001-independent-of-any-host.md).
  What a host changes:
  - packages: `Toda.Agents.Contracts` is now `AbpAgent.Contracts`, `Toda.Agents` is now `AbpAgent`;
  - namespaces follow (`using AbpAgent;`, `using AbpAgent.Contracts;`), and `TodaAgentsModule` is now
    `AbpAgentModule`;
  - error codes: `Toda.Agents:Forbidden` is now `AbpAgent:Forbidden`, `Toda.Agents:Conflict` is now
    `AbpAgent:Conflict`. Clients that match on the code need the new one;
  - `AgentsOptions.ProductName` defaults to "this system" instead of the old product's name: set your own;
  - the host tests' server is named by `ABPAGENT_TEST_POSTGRES` (was `TODA_AGENTS_TEST_POSTGRES`).
  Nothing in the database changes: the tables stay in the `agents` schema, so a migration added after the rename is
  empty. Comments no longer cite design records the repository doesn't ship; `docs/adr/0001` is the one it does.

### Added

- A name check in CI (`scripts/check-names.sh`): a former host's name in the sources, docs or packages fails the build.

- Limits: an agent's own run limits (`AgentRunLimits`: turns, tool calls, minutes, cost per run, the site's where null;
  `AgentsOptions.MaxRunCost`), runs at once per agent and on the site (`MaxRunsAtOncePerAgent`, `MaxRunsAtOnce`: the
  rest wait queued and start as others end), a person's runs not ended (`MaxWaitingPerPerson`), a tool call's timeout
  (`ToolTimeout`) and a stop after failed calls in a row (`MaxToolFailuresInRow`); `AgentService.UsageAsync` and
  `SpentThisMonthAsync`; `IAgentSite.WhyNotOnAsync`, defaulted, for a host that wants something before agents go on.
- Memory: `AgentMemory` (`agents.agent_memories`, a new `IAgentsDbContext.AgentMemories`), the agent's own tools
  `remember`, `recall` and `forget`, an item's newest memories given at a run's start, and `AgentMemoryService` for
  admins (list, delete) and erasure. A memory reaches only runs whose person may read its item; a run that read beyond
  its scope keeps none; one kept after outside content marks the run it reaches. The agent's `Memory` setting, on by
  default; `IAgentItemKind.NeighboursAsync`, defaulted, lets recall look near an item's links.
- Chats: `AgentChat` (`agents.agent_chats`, a new `IAgentsDbContext.AgentChats`), `AgentChatService`
  (places, list, start, send, get, post, delete, and `ForgetAsync` for erasure), the trigger `AgentTriggers.Chat`, and
  `AgentRun.ChatId` and `PostedAt`. A chat's run gets the chat so far, starts marked as its earlier runs were, and keeps
  its reply in the chat until the person posts it. `AgentRunInfo` now carries `Prompt` (for transcript readers),
  `ChatId` and `PostedAt`; `AgentRunStepInfo.Writes` says whether a step could change things. The sample host has the
  routes and two host tests.
- What a step needs before it ends: `IAgentExitChecks` and `AgentExitCheck`. A run with a cause asks the host
  before it ends by an exit; `finish_step` is refused with what's missing, and a final answer is answered with it, at
  most `AgentRunner.MaxReminders` times. With no host check registered nothing changes. The sample host has one, with a
  host test.
- Every agent's instructions say where its work goes: each place the request names, with its tools, then a short note;
  with none named, the answer is the work.
- Tools that reach outside: `AgentToolReach.Outside` marks the run as having read outside content and nothing
  beyond its scope; `AgentTool.AlwaysAsks` makes a change undo can't take back ask every time, never allowed for the run
  or always; `AgentTool.OptIn` offers a tool only once the agent's settings name it.
- `IAgentTools.ListAsync(AgentToolsFor, ct)`, defaulted, lists the tools for one run (its agent, scope and the person
  who started it), and `AgentActing` carries `AgentId`, `ScopeId` and `StartedBy`.
- An agent's picture: `AgentPerson.Picture`, `IAgentAccounts.SetPictureAsync` (the default refuses) and
  `AgentService.SetPictureAsync`, for whoever administers the agent.
- The sample host has an outside read and an outside change, with a host test.
- `IAgentAccounts.CreateAsync` with the account's name in other languages, and `IAgentAccounts.RenameAsync`, both with
  defaults (the account is made with one name and kept as made), so a host that implements neither keeps working. An
  agent's account is made with the agent's names, the name as written being its English one (else its first), and is
  renamed when an update changes the agent's names.

### Security

- A run's answer is cleaned as it's kept and as it's read, as the copy it posts is: an image from elsewhere it was
  steered into writing no longer loads where the answer is drawn.
- A change beyond the run's scope asks before it runs: its item, or the `project` it makes something in, is looked up
  first and the run is marked as reaching beyond its scope, so neither the agent's settings nor a standing permission
  lets it through.
- Item keys in a tool's answer are read from `record` too, and a tool that reads only inside a scope marks the run as
  reaching beyond it when its input names another scope.
- Where a change lands is asked of the item kinds (`IAgentItemKind.ScopeNamedAsync`, defaulted), for a change that
  names no item: a host's change whose landing can't be told counts as beyond the run's scope, and asks.
  "Allow for this run" names the landing scope's key when the change names no item.
- `AgentToolReach.Named`: a tool that reads the scope its input names marks the run when that isn't the run's scope,
  or when it names none.

## [0.1.0-preview.1] - 2026-11-03

The first release as packages, after agents ran in Toda on its records and assets.

### Added

- `Toda.Agents.Contracts`:
  - the seams a host gives: `IAgentAccounts`, `IAgentRights`, `IAgentScopes`, `IAgentItemKind`, `IAgentTools`,
    `IAgentModels`, `IAgentActingScope`, `IAgentRunQueue`, `IAgentEvents`, `IAgentAudit` and `IAgentSite`;
  - the actor (`AgentActor`), items (`AgentItemRef`, `AgentItemAccess`, `AgentBrought`), tools (`AgentTool`,
    `AgentToolReach`) and the model's request and reply;
  - keys that go to the kind they are, for hosts whose kinds' keys can look alike: `IAgentItemKind.Claims` (an object's
    shape) and `AgentTool.ItemKind` (the tool's kind), both off by default;
  - the host's chain of automation handed back with every act (`AgentActing.Chain`), and a day's cap told to the host
    (`IAgentEvents.RunsCappedAsync`, `AgentRunsCapped`).
- `Toda.Agents`, the ABP module:
  - `AgentService`: agents for the site or a scope, offered and taken on with a role, paused, and the site's switch
    under the operator's gate (`Agents:AdminsMayEnable`);
  - `AgentRunService`: asking an agent about an item, starting a run for a trigger (an assignment, a mention, a rule,
    a schedule, a workflow step) by a person or the system, once per cause, reading a run as each reader may,
    stopping it, and getting it ready to undo; one run per agent per item at a time;
  - `AgentStart`: a workflow step's exits (`Exits`, chosen with `finish_step`), the host's chain (`Chain`), the change
    set of the act that caused it (`CausedInChangeSet`: an agent's own act never starts it), rights the host lends a run
    no person started (`ActorId`, a rule's actor), and a sender from outside the team (`Sender`);
  - a day's caps: the runs one agent may start (`MaxRunsPerAgentPerDay`, 200) and one sender (`MaxRunsPerSenderPerDay`,
    20); past them no run starts and the host is told;
  - asking first: a write that may not run without a person asks the run's starter (or the agent's responsible
    person), and the run waits; allow, allow for this run, always allow or refuse; a timeout; questions and standing
    permissions (`AgentQuestion`, `AgentPermission`). "Always allow" only for the host's own tools; in a run no person
    started, whoever may change the agent's settings may give it, and it sets the tool to run;
  - `AgentPermissionService`: a person's standing permissions and an agent's, and taking one back, audited; taking an
    agent off a scope removes them there; each step says what let it run (`AgentRunStep.Allowed`, `AgentAllowances`);
  - `AgentRunner`: a turn at a time, the run's limits (its time leaves out waits for answers), the safety rules, steps
    written before and after;
  - `AgentText`: the Markdown cleaner for what agents write;
  - the run queue (`AgentRunWorker`), transcripts let go (`AgentTranscripts`), and the EF Core model
    (`ConfigureAgents`, schema `agents`).
