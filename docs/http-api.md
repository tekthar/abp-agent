# AbpAgent.HttpApi: the agents' HTTP API

An opt-in package (`AbpAgent.HttpApi`, `AbpAgentHttpApiModule`) that serves AbpAgent's services over HTTP, so every
host has the same agents' API and the same screens can use it (`@abpjs/agent`). A host that runs agents without an
API (a worker) doesn't take it.

## What the host gives

- **The route prefix:** `Agents:HttpApi:RoutePrefix`, `api/agent` by default. Every route below is under it.
- **The caller:** `IAgentHttpCaller` turns a request into the `AgentActor` the services check: who is calling, and the
  host's own rights object for them (`AgentActor.Principal`). The default is ABP's signed-in user with no principal;
  a host whose item kinds read rights from a principal (an ability) replaces it. No caller, no answer (401).
- **An OpenAPI group, if it wants one:** `Agents:HttpApi:OpenApiGroup`. Unset, the routes are in the host's own
  document.

Refusals keep AbpAgent's codes: `AbpAgent:Forbidden` answers 403 and `AbpAgent:Conflict` 409. The services already check
who may do what (an agent's admins, the run's person, rights on items), so the controllers only map requests to them.
Switching agents on and making a site agent are for site admins (`IAgentRights.IsSiteAdminAsync`).

## Routes

| Route | What |
|---|---|
| `GET`, `PUT settings` | The site's switch (site admins) |
| `GET agents`, `POST agents` | The site's agents, a new site agent (site admins) |
| `GET`, `PUT agents/{id}` | One agent; a change (its admins) |
| `POST agents/{id}/pause`, `POST agents/{id}/resume` | Pause, resume |
| `GET agents/{id}/usage` | Its runs and what they cost, today and this month |
| `GET agents/{id}/tools` | The tools it may be given: the host's catalog, the sandbox's while one is available |
| `GET agents/{id}/memories`, `DELETE agents/{id}/memories/{memoryId}` | Its notes (`scope`, `kind`, `key`), forgetting one |
| `GET agents/{id}/graph` | Its graph in a scope (`scope`, `find`, `thing`), no model calls |
| `GET agents/{id}/permissions` | The standing permissions people gave it (its admins) |
| `GET scopes/{scope}/agents`, `POST scopes/{scope}/agents` | A scope's agents, a new agent of the scope's own |
| `PUT`, `DELETE scopes/{scope}/agents/{id}/take` | A scope takes an agent on (`roleKey`), or lets it go |
| `GET items/{kind}/{key}/agents` | The agents its reader may ask about an item |
| `GET`, `POST items/{kind}/{key}/runs` | Runs on an item; asking an agent (`agentId`, `prompt`) |
| `GET runs/{id}` | A run with its steps (inputs, outputs, outcomes) |
| `GET runs/{id}/export` | The same as a JSON file, to dig into what went wrong |
| `POST runs/{id}/stop` | Stops it |
| `GET questions`, `POST questions/{id}/answer` | The questions runs wait for me to answer; an answer (`allow`, `allow-run`, `always`, `refuse`) |
| `GET me/permissions`, `POST permissions/remove` | My standing permissions; taking one back |
| `GET chats/places` | Where I may chat, with the agents there |
| `GET chats`, `POST chats` | My chats; a new one (`agentId`, `prompt`, `scope`, or `kind` and `key`) |
| `GET chats/{id}`, `DELETE chats/{id}` | A chat with its runs; deleting it |
| `POST chats/{id}/messages` | The next message (`prompt`) |
| `POST chats/{id}/runs/{runId}/post` | Posts a reply where its item's people see it |
| `GET chats/{id}/events` | Server-sent events while a run goes: `turn`, `text`, `step`, `question`, `run`, `ping`, each with its id; `Last-Event-ID` resumes |

## What stays the host's

A host's own routes keep what's its own: outside connections and per-person sign-ins, AI limits (its AI gateway's),
pictures, undoing a run (its change sets; AbpAgent says whether a run is ready, `AgentRunService.ReadyToUndoAsync`), and
routes named in its own words (a record's, a project's). A host that served these routes before can keep its old ones
for a release while its screens move.
