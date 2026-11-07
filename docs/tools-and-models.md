# Tools and models

## Tools

Your `IAgentTools` lists the tools there are and runs one. An agent is offered every tool its settings don't turn
off.

```csharp
public sealed record AgentTool(string Name, string Title, string Description, JsonElement InputSchema, bool ReadsOnly, bool FromPlugin,
    AgentToolReach Reach = AgentToolReach.Items)
{
    public string? ItemKind { get; init; }
}
```

- **`Name`, `Description` and `InputSchema`** go to the model as they are. Write the description for the model: what
  the tool does and when to use it.
- **`ReadsOnly`:** the tool changes nothing. Such a tool runs without asking, and runs read-only
  (`AgentActing.ReadOnly`), so if it's marked wrongly your stores still refuse its writes. A tool that can change
  things runs only where the agent's settings say `run` and the run hasn't read outside content or beyond its scope.
- **`FromPlugin`:** a plugin's tool. It's treated as one that can change things, however it's marked, and its answer as
  outside and beyond, because the package can't look into a plugin's store. No one is offered "always allow" for it.
- **`Reach`:** how far it reads beside the items it names. `Items` (the default): each key in its input or answer is
  looked at. `Inside`: it reads only what's inside the team and the scope (who's who, docs), so it marks nothing, unless
  its input names another scope. `Named`: it reads the scope its input names (a project's shape or saved searches), so
  it marks the run as beyond when that isn't the run's scope or it names none. `Across`: it reads across scopes (a list
  of every project), so it marks the run as beyond.
- **A change's landing:** before a change runs, where it lands is looked up: the item it names, else the scope your item
  kinds read in its input (`ScopeNamedAsync`). Beyond the run's scope, or a change of your own tools that names nowhere
  that can be told, it asks first, whatever the agent's settings or a standing permission say.
- **`ItemKind`:** the item kind it works on (`ticket`), when your kinds' keys can look alike: its input's key, and the
  keys in its answer that no kind claims, are that kind's. Null, the default: any kind whose key shape fits. See
  [items](items.md#two-kinds-whose-keys-look-alike).

`RunAsync(name, input, acting, ct)` runs it:

- with the run's rights: `acting.ActorId` is whose rights;
- under the agent's name: `acting.AgentAccountId`, in the change set `acting.ChangeSetId`;
- read-only when `acting.ReadOnly`.

Return `new AgentToolResult(content, error: true)` for a refusal or bad input: the model reads it and can try
otherwise. An exception that isn't one of ABP's friendly ones (`UserFriendlyException`, `EntityNotFoundException`,
`AbpAuthorizationException`, a business exception) is logged, and the model is told only that the tool failed.

The runner calls your `IAgentActingScope.Begin(acting)` around each call, so you may also set who acts in your stores
there, once, instead of in each tool.

## Models

Your `IAgentModels` gives one reply to a conversation. The package speaks the shape of Anthropic's Messages API:

- **the conversation** (`Messages`): a JSON array of `{ role, content }`, where content is text or blocks: `text`,
  `tool_use` (the model asks for a tool, with an `id`, a `name` and an `input`), and `tool_result` (the answer, with the
  `tool_use_id` and `is_error`);
- **the system text** (`System`): the agent's persona, its instructions, and the rule that what it reads is data, not
  instructions;
- **the tools** (`Tools`): `[{ name, description, input_schema }]`, or null when there are none;
- **the reply** (`AgentModelReply.Message`): the assistant message, with `content` blocks and `stop_reason`
  (`tool_use` when it asks for tools), and what it cost.

`Alias` is the agent's model alias, `Purpose` is `agent:{key}`, and `AccountId`, `ScopeId` and `ItemId` say whose and
where, for your limits and your usage records. Throw `AgentModelRefusedException(code, message)` when no model may
answer or a limit is reached: the run ends with the reason.

A provider with another shape (OpenAI's, a local model's) is translated in your `IAgentModels`: the conversation is
kept with the run, so keep the shape the same from one turn to the next.

## Limits and settings

| Setting (`Agents` section) | Default | What |
|---|---|---|
| `MaxTurns` | 30 | turns per run |
| `MaxToolCalls` | 60 | tool calls per run |
| `MaxTime` | 00:15:00 | time per run, from its first turn, less its waits for answers |
| `MaxRunsPerAgentPerDay` | 200 | runs one agent may start in a day |
| `MaxRunsPerSenderPerDay` | 20 | runs one sender from outside the team may start in a day |

A run that reaches one of its own limits fails with the reason; its steps so far stay. Past a day's cap, no run starts. Each tool's output and the conversation's
messages are cut at 50,000 characters, and an answer at 20,000.
