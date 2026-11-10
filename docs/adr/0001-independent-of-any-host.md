# 0001. Independent of any host

- **Status:** Accepted
- **Date:** 2026-11-10

## Context

AbpAgent began as the agents of one product, and was made a package so other products could give their people agents
too. Its next hosts serve whole organizations, report figures and run inside partners' pages. None of them can take a
package that carries another product's name, or one that needs another product's platform to run.

## Decision

**The package knows nothing of its hosts.** Everything a host owns reaches it through a seam in `AbpAgent.Contracts`:

| Seam | What it answers | Default |
|---|---|---|
| `IAgentAccounts` | the agents' own accounts, and who people are | none: the host gives it |
| `IAgentRights` | who may administer agents on the site or in a scope | none: the host gives it |
| `IAgentScopes` | where agents work, by id and key | none: the host gives it |
| `IAgentItemKind` | the items agents work on: find, read, who may, what keys bring in, the answer | none: the host gives it |
| `IAgentTools` | the host's tools, with what each reads or writes and how far it reaches | none |
| `IAgentModels` | the model, in the Messages API's shape | none |
| `IAgentActingScope` | who acts while a run works, and in which change set | nothing |
| `IAgentExitChecks` | whether a step's exit is ready, for runs on a process's step | nothing is checked |
| `IAgentRunQueue` | work a run's turns in the background | the package's worker |
| `IAgentEvents` | tell the host what happened | nothing |
| `IAgentAudit` | the host's security audit | nothing |
| `IAgentSite` | safe mode, and whether agents may go on | no, and yes |

- **`AbpAgent.Contracts` is plain .NET:** nothing of ABP, EF Core or any host. `AbpAgent` is an ABP module that
  needs only .NET, ABP, EF Core with Npgsql, Markdig and its contracts. The architecture tests in
  `tests/AbpAgent.Tests` hold both.
- **The tables are the host's:** the package's tables live in the `agents` schema, mapped in the host's own EF Core
  context, and the host owns their migrations.
- **Nothing of a host's name:** ids, namespaces, error codes (`AbpAgent:...`), defaults (`AgentsOptions.ProductName`)
  and package metadata are neutral, and hosts set their own words. `scripts/check-names.sh` fails CI if a former
  host's name comes back; the CHANGELOG is the one place it may stand, to tell clients what was renamed.
- **Comments don't cite records the repository doesn't ship.** A design decision a reader needs is written here, in
  `docs/adr`, or in the guides.

## Consequences

- A host writes an adapter per seam, and the package never forks for one host.
- Renaming the package changed no table, column or stored value, so a host's migration for it is empty.
- Every new feature has two halves: the package's, behind a seam, and the host's adapter.
