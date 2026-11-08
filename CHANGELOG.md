# Changelog

All notable changes to Toda Agents are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

### Added

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
- Chats (ADR 0102): `AgentChat` (`agents.agent_chats`, a new `IAgentsDbContext.AgentChats`), `AgentChatService`
  (places, list, start, send, get, post, delete, and `ForgetAsync` for erasure), the trigger `AgentTriggers.Chat`, and
  `AgentRun.ChatId` and `PostedAt`. A chat's run gets the chat so far, starts marked as its earlier runs were, and keeps
  its reply in the chat until the person posts it. `AgentRunInfo` now carries `Prompt` (for transcript readers),
  `ChatId` and `PostedAt`; `AgentRunStepInfo.Writes` says whether a step could change things. The sample host has the
  routes and two host tests.
- What a step needs before it ends (ADR 0099): `IAgentExitChecks` and `AgentExitCheck`. A run with a cause asks the host
  before it ends by an exit; `finish_step` is refused with what's missing, and a final answer is answered with it, at
  most `AgentRunner.MaxReminders` times. With no host check registered nothing changes. The sample host has one, with a
  host test.
- Every agent's instructions say where its work goes: each place the request names, with its tools, then a short note;
  with none named, the answer is the work.
- Tools that reach outside (ADR 0096): `AgentToolReach.Outside` marks the run as having read outside content and nothing
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
