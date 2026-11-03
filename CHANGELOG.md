# Changelog

All notable changes to Toda Agents are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

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
