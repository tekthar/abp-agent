# 0091. Agents as a package

- **Status:** Accepted (the user asked for agents as a package "from day 1" on 1 Nov 2026 and said to go on until
  they're ready)
- **Date:** 2026-11-01
- **Revisit at:** toda-agents 1.0, or a second host besides Toda running agents in production

## Context

Agents (ADR 0086) are AI teammates: an agent has an account of its own, works in runs, uses the tool catalog with the
rights of whoever started the run, and every change it makes is one change set that can be undone. Steps 0 to 3 are
built: about 1,800 lines in `src/Toda.Projects/Agents`, the API in `AgentsController`, and `scripts/e2e_agents.py`.
Steps 4 to 13 are still to come: triggers, conversations, asking first, limits, personal data, outside MCP servers,
templates, the SDK, the phone and the docs.

Workflows became a package after they had grown (ADR 0089): ten steps, most of them taking code out of records. Agents
are at the point where that's cheap. Toda Marketplace needs them too: reviewing a listing is the kind of work an agent
does before a person signs it off.

Most of the agents code is already about agents, not about records:

- **The loop:** `AgentRunner` takes a turn at a time, writes each step before and after, never repeats a write whose
  result isn't known, and stops at its limits.
- **The safety rules:** a tool that only reads runs read-only; a run that has read outside content, or beyond its
  scope, asks before it writes; an unknown write is named to the model instead of repeated.
- **The cleaner:** `AgentText` parses Markdown and takes out what could carry data away when it's shown.
- **Agents, runs and steps** as entities, transcripts let go after their period, the site's switch with the
  operator's gate.

What ties it to Toda:

- **Records and projects:** `AgentRun.ProjectId` and `RecordId`; the record read as data and the answer posted as a
  comment; what a key brings in is worked out from records, comments and record changes; `AgentProject` grants a
  project role through `ProjectService`.
- **Toda's platform:** `IAi`, `ToolCatalog`, `IJobs`, `ActingScope`, `ChangeSets`, `UndoService` and `ISecurityAudit`.
- **Casl abilities** decide who administers an agent and who reads a run.
- **The tables live in the `projects` schema.**

## Decision

**The package owns what an agent is and how a run works. The host owns the items it works on, the tools it may use,
the model it talks to, and the rights.** Agents become `Toda.Agents.Contracts` and `Toda.Agents`, consumed by Toda
first and the marketplace next, and published from `todahq/toda-agents` as toda-workflow is from its repository.
The rest of ADR 0086 (steps 4 to 13) is built inside the package from here, with Toda's parts in Toda.

### What is in the package

- **Entities:** `Agent`, `AgentScope` (today's `AgentProject`), `AgentSettings`, `AgentRun`, `AgentRunStep`, in the
  host's database under schema `agents`, mapped by the host's context (`IAgentsDbContext`, `ConfigureAgents`), as the
  workflow engine's are.
- **`AgentService`:** making, changing, pausing, offering and taking on agents, the site's switch.
- **`AgentRunService`:** starting a run, reading it as each reader may, stopping it, transcripts let go.
- **`AgentRunner`:** one turn at a time, as described above, with the same limits.
- **`AgentText`:** the Markdown cleaner.
- **A run queue of its own** for hosts without a job system: a worker that takes runs one at a time with a lease.

### What stays in Toda

- **The HTTP API and the web,** which turn the package's results into Toda's shapes (`PersonDto`, project keys), so
  the API and `e2e_agents.py` don't change.
- **Undo:** a run's change set is undone by Toda's `UndoService`, as now.
- **The demo model's script** (`AgentDemoResponder`), which answers Toda's `IAi`.
- **The adapters** for each seam below, in `src/Toda.Projects/Agents/Integration`.

### The seams

The services take an `AgentActor(Guid Id, object? Principal)`. The principal is the host's own (Toda's Casl
`Ability`), carried back to the host's seams; the package never reads it. Every seam has a `TryAdd` default where one
makes sense.

| Seam | What it answers | Default | Toda's |
|---|---|---|---|
| `IAgentAccounts` | make an agent's account (never signs in); who people are (name, kind, active) | none: a host must give one | Guard's service accounts, marked as an agent's |
| `IAgentRights` | is the actor a site admin; does the actor manage a scope | site admins only, no scopes | Casl |
| `IAgentScopes` | a scope's id from its key and back; roles that may be given to an agent there; grant and take back a role | none | projects, `ProjectService` |
| `IAgentItemKind` (one per kind) | the item as data for a run; whether a key is one of its items; what keys bring in (outside content, beyond the scope); may the actor read it, change it, read its internal notes; post the answer | none | records; assets answer what their keys bring in |
| `IAgentTools` | the tools there are, with what each does (only reads, comes from a plugin, reads only inside the scope, reads across scopes); run one | none | `ToolCatalog` with `IToolAccess` |
| `IAgentModels` | one reply to a conversation | none | `IAi` |
| `IAgentActing` | everything a run does happens under the agent's name, with the run's rights, in its change set, read-only when it should be | nothing to set | `ActingScope` and `ChangeSets` |
| `IAgentRunQueue` | work a run's turns in the background | the package's worker | Toda's jobs (`agents.run`) |
| `IAgentAudit` | write an audit entry | the log | `ISecurityAudit` |
| `IAgentSite` | is the site in safe mode | no | `TodaPlatformOptions.SafeMode` |

Rules that hold for every host:

- **A run never acts with more than the person who started it.** The host's tools and items check the run's rights
  (the actor's) and stamp the agent's name, as Toda does today ("Analyst, run by Sara").
- **The package writes its own rows in its own units of work,** step by step, so a step written as started is there
  even if the tool's change rolls back. The host's tools commit their own changes.
- **The conversation is the Messages API's shape** (role, content blocks, `tool_use`, `tool_result`). It is stored
  with the run, so a run outlives a restart and moves between servers.
- **Kinds are named as in workflows:** a host's own kind by its key (`record`, `asset`, `listing-version`), a
  plugin's by `{plugin}:{key}`.

### Tables, schema and configuration

- **Schema `agents`:** the five tables move with `SET SCHEMA`: `agents`, `agent_scopes` (from `agent_projects`),
  `agent_settings`, `agent_runs`, `agent_run_steps`. `project_id` becomes `scope_id`; a run's `record_id` becomes
  `item_id`, with `item_kind` (`record` for every row that's there). The migration is Toda's, under its startup lock.
- **The backup and the data map follow** (ADR 0068, ADR 0082): transcripts are personal data.
- **Configuration: `AgentsOptions`, from the "Agents" section:** whether admins may turn agents on, the transcripts'
  period, the run's limits (30 turns, 60 tool calls, 15 minutes), and the product's name for the agent's persona.
  Toda binds its `Toda:Agents:*` settings to it, so nothing changes for an operator.

### The marketplace

The marketplace takes agents after the first release, as it took workflows: its listing versions as an item kind, its
review tools, a model of its choosing, and an agent that reviews a new version before a person signs it off.

### Steps

| Step | What | Size |
|---|---|---|
| A0 | this record, and `docs/toda-agents.md` for the plan and its status | |
| A1 | behaviour pinned: `e2e_agents.py` covers steps 1 and 3; unit tests for the cleaner and the runner's rules (refusals, marks, unknown steps) | S |
| A2 | `Toda.Agents.Contracts`: the seams, the item kind, the actor, the tool and model shapes; an architecture test (no ABP, EF Core, Guard, Casl or Toda) | M |
| A3 | `Toda.Agents`: the entities, services, runner, cleaner and transcripts in a module of their own; Toda's adapters in `Toda.Projects/Agents/Integration`; the controllers map the package's results | L, the riskiest |
| A4 | the tables to schema `agents`, `scope_id`, `item_id`/`item_kind`; the host maps the model; backup sources and the data map | M |
| A5 | the package's own run queue, for hosts without a job system | S |
| A6 | a sample host with its own items (support tickets in its own table), a scripted model, and Postgres tests using only the defaults | M |
| A7 | the repository `todahq/toda-agents`: history, guides, CI, release check; published as 0.1.0-preview.1 | |
| A8 | ADR 0086 steps 4 to 13 continue in the package | |

Each step lands on main with `e2e_agents.py` passing. The full CI runs at A4 and at A6.

### Not now

- **The web as a package.** The agents' pages stay in Toda's web until a second host needs them.
- **Agents without a host's tool catalog.** A host that has no tools yet gets an agent that can only answer.

## Consequences

- Toda's agents keep their API, their history lines and their rules; the tables move schema once.
- Every later step of ADR 0086 has two halves: the package's, and Toda's adapter. That's more files, and it keeps the
  package honest.
- The marketplace gets agents through the same contracts it already uses for workflows: a kind, a scope, a host's
  rights.
- `IAgentReadCheck` is replaced by the item kind's answer of what keys bring in. Assets answer it for their keys.
