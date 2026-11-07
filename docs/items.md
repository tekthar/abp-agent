# Items

An item is anything a run works on: a ticket, a record, a listing. The package never sees your entity. It sees an
`AgentItemRef` (kind, id, key, scope) and asks your `IAgentItemKind` for the rest.

Register one class per kind under `IAgentItemKind`. A kind is named by its key (`ticket`); a plugin's kind, if your
host has plugins, by `{plugin}:{key}`.

## The members

| Member | What it must do |
|---|---|
| `Kind` | the kind's name, as runs store it in `ItemKind` |
| `Noun` | what the request calls the item: the tag around it in the run's first message (`<ticket key="DESK-1">`) and in the agent's persona |
| `IsKey(key)` | whether a text has the shape of one of your keys. The runner collects the `key` and `recordKey` values in a tool's input and answer that any kind says are keys, and asks each kind what its own bring in: see [two kinds whose keys look alike](#two-kinds-whose-keys-look-alike) |
| `Claims(item)` | whether an object in a tool's answer is one of your items by its shape (Toda's records have their project, its assets their schema). Optional: false by default |
| `FindAsync(key)` | the item by key, in any scope, or null. Asking an agent finds the item this way |
| `ScopeNamedAsync(input)` | the scope a tool call's input names, or makes something in, in your words (a desk, a project, the type of item it makes), when it names no item by key. Optional: null by default, so a change of your own tools that names no item asks first |
| `GetAsync(id)` | the item by id, or null. Keep finding items that were deleted, so their runs can still be read |
| `AccessAsync(actor, item)` | what the actor may do: read it, change it, manage it, read its internal notes. Asking needs `Read`; stopping a run needs `Change`; reading a run's steps comes with `Manage` |
| `ReadAsync(acting, item)` | the item as JSON, read with the run's rights (`acting.ActorId`), or null when the run can't read it. This goes to the model as data |
| `BroughtAsync(scopeId, item, keys)` | for the keys that are yours: whether they hold outside content, and whether any is beyond the run's scope or item. See below |
| `AnswerAsync(acting, item, text, internalOnly)` | puts the run's answer on the item, by the person who started the run (`acting.ActorId`), run by the agent (`acting.AgentAccountId`), in its change set. When `internalOnly`, only the team may read it |

## What keys bring in

`BroughtAsync` is how the package knows what a run has read. It's called with every key a tool's input or answer
named, and the run's own item when the run starts. Answer for the keys that are yours, and ignore the rest.

- **Outside:** content that came from outside the team: a customer's request, a mail, a comment by someone who doesn't
  work in the item's scope, an anonymous visitor's, something an import brought from another system. Once a run has
  read it, its writes ask a person first, and its answer is for the team only.
- **Beyond:** an item in another scope, or one the run's item doesn't name (in Toda, a record's run reads an asset as
  beyond unless the record names it; an asset's run, an asset in another schema). Once a run has read beyond, its
  writes ask, and its answer stays in the run, for the person who started it, so what it read with their reach can't
  land where others with less would read it.

Fail closed. If you can't tell, say outside.

A kind that runs don't work on can still answer this: one whose items no one may read through the package
(`AccessAsync` says none), but whose keys a tool's answer may name, so they're checked.

## Two kinds whose keys look alike

With one kind, or kinds whose keys never look alike, skip this. In Toda, a project and an asset schema may share a key,
so `IT-5` is a record and an asset. Were every key given to every kind, a run on the record would read the asset of
the same key as beyond its scope, and keep its answer. So a key goes to the kind it is:

- **the run's own item,** to its kind;
- **a tool's input key, and the keys in its answer,** to the tool's kind, when the tool names one
  (`AgentTool.ItemKind`, see [tools](tools-and-models.md#tools));
- **an object a kind claims by its shape** (`Claims`), to that kind, and so are the objects inside it that no kind
  claims;
- **a `recordKey`** names another item than the object it's in, so it goes to every kind whose `IsKey` fits, whatever
  claims the object.

Any other key, of no known kind, goes to every kind whose `IsKey` fits, as with one kind.

## The answer

`AnswerAsync` is called once, when the run is done, unless the run read beyond its scope or answered with nothing. Write
it as the person who started the run, marked as the agent's act, in the run's change set, so undoing the run takes it
back. If your store cleans rich text, `AgentActs.Now` is set while you write: call `AgentText.CleanWrites` in your
context's save, as the sample does.
