# Troubleshooting

## "The operator hasn't allowed agents on this site, so they can't be turned on here."

`Agents:AdminsMayEnable` (or the setting your host binds) isn't true. Whoever runs the servers sets it; then a site
admin turns agents on.

## "Agents are off on this site."

The operator allows agents, but the site's switch is off. A site admin turns it on (`AgentService.SwitchAsync`).

## "An agent's responsible person is an active person, not a service account or an agent."

`IAgentAccounts.GetManyAsync` didn't return the account as an active person (`Kind` "person", `IsActive`). Check your
seam returns people with their kind.

## "'helper' isn't a role in DESK." or "An agent can't administer a project"

Your `IAgentScopes.CantGiveAsync` refused the role. Give a role that works on items, not one that administers.

## "DESK hasn't taken triage on."

The agent is offered to the scope (or is the scope's own), but the scope hasn't taken it on with a role. Take it on
first (`AgentService.TakeAsync`).

## A run didn't start

`StartAsync`'s result says why (`AgentStartResult.Why`):

- **"triage has started the 200 runs it may start in a day."** or **"One sender from outside the team has started the
  20 runs a sender may start in a day."** A day's cap (`MaxRunsPerAgentPerDay`, `MaxRunsPerSenderPerDay`). Runs start
  again as the day's earlier ones fall out of the last 24 hours. If the agent is caught in a loop, look at what started
  its runs (their `Trigger` and `Cause`).
- **"The agent's own run did it."** or **"triage started it itself."** The event came from the agent's own act. That's
  the rule against loops working.
- **"triage has a run for that already."** The `Cause` has a run: an event that came twice.
- **"Whoever started it can't read DESK-1."** or **"Whose rights it would use can't read DESK-1."** Your item kind's
  `AccessAsync` gave the starter (or `ActorId`) no `Read`.

## "Always allow" isn't offered

It's offered only for your own tools, never a plugin's; not once the run has read outside content or beyond its
scope; and, in a run no person started, only to someone who may change the agent's settings.

## "Agents don't work on tickets here."

No `IAgentItemKind` with that `Kind` is registered. Register yours under the interface.

## A run stays queued

- **The package's worker:** it runs on each server as a hosted service. Check the host started (a test host that never
  makes a request doesn't). The run's `LeasedUntil` shows when a server took it.
- **Your own queue:** check your job ran and called `AgentRunner.TurnAsync` until the turn said `Over`.

## A run ends at once with "Stopped."

It was stopped, the agent was paused, agents were turned off, or `IAgentSite.SafeMode` is true. The run's `Error` says
which.

## "The model didn't answer (code): …"

Your `IAgentModels` threw `AgentModelRefusedException`: no model for the alias, a limit, a provider error. The code and
message are yours.

## A run waits for an answer

It asked before a write (the step's outcome is `asked`, the run's `Question` says what):

- **"its changes ask a person first":** the run read outside content or beyond its scope. Check what your item kinds'
  `BroughtAsync` said, and the tools' `Reach`.
- **"this agent's settings say it asks first":** set the tool to `run` in the agent's `Tools`, or have the person
  answer "always".
- **A plugin's tool:** always counts as one that changes things.

Whoever it asks answers it (`AnswerAsync`). Unanswered, it's refused after `QuestionTimeout`.

## "Only whoever it asks answers it."

Questions are answered by the person they ask: the run's starter, or the agent's responsible person for runs the system
started. Anyone else may stop the run, if they may change its item.

## The answer isn't on the item

The run read beyond its scope (`Wide`), so the answer stays in the run for the person who started it; or it answered
with nothing; or your `AnswerAsync` threw a friendly exception, which is logged.

## "The run was still going: it stops after its current step."

Undo waits for the run to stop, so its change set is whole. Undo it once its state is `stopped`, `done` or `failed`.
