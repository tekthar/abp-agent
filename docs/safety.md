# Safety

An agent reads what people wrote, some of whom are outside your team, and a model can be talked into things. The
package's rules assume the model will sometimes do what the text it read tells it to, and keep that from reaching
anything that matters. They hold only if your seams do their part.

## The rules

1. **No more than the person who asked.** A run started by a person uses that person's rights; one the system starts
   uses the role its scope gave the agent, or the rights your host lends it (`AgentStart.ActorId`: a rule's actor). The
   agent's own name is on what it does, beside the person's.
2. **Reading is read-only.** A tool marked as only reading runs read-only; a plugin's tool never counts as one.
3. **Writes ask once a run has read outside content** (a customer's words, a mail, another system's) **or beyond its
   scope**, whatever the agent's settings or anyone's "always allow" say.
4. **A tool that can change things runs only where the agent's settings say `run`,** or the person who started the run
   allowed it (for this run on this item, or always). Otherwise the run asks: see [asking first](#asking-first).
5. **The answer goes only where it's safe.** After outside content, it's for the team only. After reading beyond the
   scope, it stays in the run, for the person who started it.
6. **What the agent writes is cleaned.** Images (but the item's own files), links (their address shown beside their
   text), reference definitions and HTML become text, so nothing it writes loads or sends anything when it's shown.
7. **A write whose result isn't known is never repeated by itself.** The agent is told to check first.
8. **What the model reads is data.** The persona tells it so, and the request and the item are inside tags. This rule
   is the weakest of all: the others are what hold.

## Asking first

A write that may not run without a person makes the run ask, and wait (`waiting`):

- **Whom:** the person who started the run, or the agent's responsible person when no person did: an assignment by a
  rule, a schedule, a workflow step, a rule's run with its actor's rights, a mention by someone outside the team. A
  customer is never asked about the run their words started.
- **What they see:** the tool, the item, why it asks, and what the tool would be given. A run's `Question` shows it to
  whoever is asked and whoever may read the run's steps; `AgentRunService.QuestionsAsync` lists a person's open ones;
  `IAgentEvents.QuestionAskedAsync` lets your host tell them (Toda sends a notice).
- **The answers** (`AgentRunService.AnswerAsync`): `allow` (this call), `allow-run` (this tool on this item for the
  rest of the run), `always` (the person's own runs of this agent with this tool in this scope, kept as an
  `AgentPermission`), or `refuse` (the agent is told not to try another way). Only whoever it asks answers.
- **When `always` is offered:** only for your own tools, never a plugin's (`FromPlugin`), since nothing says undo can
  take a plugin's acts back; and not once the run has read outside content or beyond its scope. Given anyway, it counts
  for the run. In a run no person started, it's offered to whoever is asked if they may change the agent's settings,
  and it sets the tool to `run` in them, audited as a change of the settings.
- **In time:** unanswered after `QuestionTimeout` (a day), it's refused, and the run goes on. Waiting isn't the run's
  work: its `MaxTime` leaves out each question's wait, so a late answer lets it go on. Stopping a waiting run ends it
  at once.
- **Where it goes on:** the run kept the turn's other tool calls (`Paused`); once answered it's queued again, runs or
  refuses the call it asked about, and makes the rest.

## Standing permissions

An `always` answer is a standing permission: this person's runs of this agent may run this tool in this scope without
asking. People can see what they gave and take it back (`AgentPermissionService.MineAsync`, `RemoveAsync`), and so can
whoever administers the agent (`ForAgentAsync`). Giving and taking one back are audited, taking an agent off a scope
removes the ones given there, and each permission says when it last let a call through (`LastUsedAt`).

A step that changes things says what let it run without asking (`AgentRunStep.Allowed`, `AgentAllowances`): the
agent's settings (`settings`), someone's standing permission (`always:{person}`), an earlier answer in the run
(`this-run`), or the question's own answer (`answer:{answer}`).

## Loops and floods

Agents start on what people and automation do, and what agents do is people's and automation's to react to. These keep
that from going round:

- **An agent's own acts never start it:** an event whose change set is one of the agent's runs (`CausedInChangeSet`),
  or that the agent's account started, starts nothing.
- **One run per cause:** an event that comes twice (`Cause`) starts one run.
- **The host's chain:** a run keeps the chain of automation you gave it (`Chain`) and hands it back on every act, so
  your own loop rules see agents' writes as part of the chain. Toda stops a chain 10 deep, or when an agent worked the
  item already in it, or three agents deep.
- **A day's caps:** past `MaxRunsPerAgentPerDay` for the agent, or `MaxRunsPerSenderPerDay` for a sender from outside
  the team, no run starts and the item waits for people; `IAgentEvents.RunsCappedAsync` tells you.

## Your part

- **Check rights in every tool and store,** with `acting.ActorId`, as you would for the person. The package doesn't
  know your rules.
- **Refuse writes when `acting.ReadOnly`.** Toda's stores refuse a save while an act may only read; that's what makes
  rule 2 hold even when a tool is marked wrongly.
- **Answer `BroughtAsync` honestly, and fail closed.** If a tool returns content the package can't see into, give it
  `Reach = Across`, or mark it as a plugin's.
- **Mark `ReadsOnly` only for tools that never write,** and `Inside` only for tools that read nothing a customer could
  have written.
- **Clean rich text in your stores' saves** with `AgentText.CleanWrites` (it acts while `AgentActs.Now` is set), so a
  tool that writes text is cleaned however it writes.
- **Give what a start needs to stop loops:** `CausedInChangeSet` with every run an event starts, and your `Chain`, with
  your depth rule around the acts that carry it.
- **Lend rights with `ActorId`, never `StartedBy`,** for a run no person asked for (a rule's), so its questions go to
  the agent's responsible person; and give `Sender` for a run someone outside the team started.
- **Keep the operator's gate.** `Agents:AdminsMayEnable` is off by default, so a restored copy of production doesn't run
  agents until someone says so.
- **Give `IAgentSite.SafeMode`** if your host has a safe mode: it holds every agent.
