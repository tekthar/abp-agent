# Concepts

## Agents

An agent (`Agent`) is a profile on an account of its own. The account is marked as an agent's: it never signs in and
holds no tokens. An agent has:

- **a key,** lowercase and unique on the site; its account's username is made from it (`agent.triage`, or
  `agent.desk.triage` for a scope's agent);
- **names and titles** by language, and **instructions** in Markdown (at most 20,000 characters);
- **a model alias** its runs use (`smart` by default), which your `IAgentModels` turns into a model;
- **tool settings:** for each tool by name, `run`, `ask` or `off`;
- **a responsible person:** an active person (never a service account or another agent) who hears of its failures and
  may pause it;
- **a state:** on, or paused. A paused agent starts no run, and its running ones stop at their next turn;
- **a transcript period** of its own, over the site's, if you give one.

## Scopes

A scope is where agents work: a work tracker's projects, the sample's desks, a marketplace. Your `IAgentScopes` says which there
are, by id and key.

- **A site agent** (no scope) is offered to every scope (`OfferAll`) or to named ones (`OfferedTo`).
- **A scope's agent** belongs to that scope alone, and is taken on by it when it's made.
- **Taking an agent on** (`AgentScope`) gives the agent's account a role in the scope, through your seam. That role is
  what its runs work with when the system starts them; a run a person starts uses that person's rights. An agent can't
  be given a role that administers the scope: your `CantGiveAsync` says which roles those are.

## Runs and steps

A run (`AgentRun`) is one piece of an agent's work: who started it, whose rights it uses, the item it works on, what
it was asked, its state, its conversation so far, its answer, what it cost, and its change set.

A run takes one turn at a time:

1. the model is given the conversation so far (the first message holds the request and the item, read with the run's
   rights) and the tools the agent may use;
2. it answers, or asks for tools; each tool call is a step (`AgentRunStep`), written as started before it runs and
   finished after;
3. the tool results go back to the model on the next turn.

**What starts a run** (`Trigger`): a person asking (`ask`), the item assigned to the agent (`assigned`), a mention
(`mentioned`), a rule (`rule`), a schedule (`schedule`) or a workflow's step (`workflow`). The host notices these and
calls `AgentRunService.StartAsync` with an `AgentStart` ([starting runs](hosting.md#starting-runs)). A person who
started it lends the run their rights, and must be able to read the item; a run the system starts uses the agent's own
role in the item's scope, or rights the host lends it (a rule's actor), and counts as the system's for asking first. An
agent's own acts never start it, a `Cause` (the host's event id, say) starts one run however often the event comes,
and a day's caps bound how many runs an agent, and a sender from outside the team, may start
([loops and floods](safety.md#loops-and-floods)).

**One run per agent per item at a time.** A run is handed to the queue only when the agent has nothing else going on
that item; the others wait in order, and the next is queued when one ends. When a run ends, the host is told
(`IAgentEvents.RunFinishedAsync`), so its rules can start on it.

A run that asks a person before a write waits (`waiting`) until they answer: see [asking first](safety.md#asking-first).
A waiting run holds its item's line too.

A run ends when the model answers without asking for a tool, when it reaches its limits (30 turns, 60 tool calls, 15
minutes of work by default, not counting its waits for answers), or when it's stopped: by whoever started it, the agent's responsible person, a site admin, or
whoever may change its item. Agents switched off, the agent paused, and safe mode stop it too.

Because each step is written before it happens, a run whose server stopped in the middle of a tool call knows it
doesn't know what happened. When it resumes, that step is marked `unknown`, and if it could write, the agent is told
which tool it was and to check before doing it again. The package never repeats it by itself.

## Actors and acting

`AgentActor(Id, Principal)` is who calls a service: the account, and your own principal for it (a host may pass its Casl
ability). The package never reads the principal; it hands it back to your seams.

`AgentActing` is what a run is doing now: the run, its change set, the agent's account, whose rights it uses, whether
this act may only read, and the host's chain of automation the run is part of (`Chain`). The runner sets it around every read, tool call and answer (`AgentActs.Now`), and asks
your `IAgentActingScope` to put the act under the agent's name in your stores.

## Change sets

Every change a run makes is in one change set: its id is on the run (`ChangeSetId`) and in `AgentActing`. Your stores
record it with each change, and "undo this run" takes the set back as a whole. Undo is the host's: the package gets a
run ready to undo (`ReadyToUndoAsync` stops one still going and refuses until it has) and gives you the set's id.

## Transcripts

A run's transcript is its conversation and each step's input and output. Only the person who started the run and site
admins read it; the agent's responsible person and the item's managers see the steps' names, targets and outcomes.

Transcripts go after the site's period (`Agents:TranscriptDays`, 30 days) or the agent's own. Who, when, and each
step's outcome stay. `AgentTranscripts` lets them go every few hours on each server, and a new run lets go of a batch
too.
