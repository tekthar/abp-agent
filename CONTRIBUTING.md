# Contributing

Thank you for helping. Issues and pull requests are welcome on GitHub.

- **Before a large change**, open an issue to agree on the approach. A smaller change that is easy to review is more
  likely to land.
- **Build and test** as the README says: `dotnet build`, `dotnet test` (the host tests need PostgreSQL), and
  `dotnet pack`. A pull request runs the same in CI, on GitHub Actions.
- **Keep what hosts rely on.** These are a contract: changing one is a breaking change and goes in the CHANGELOG under
  the version that makes it.
  - the contracts in `AbpAgent.Contracts`: the seams, the item kind, tools and models;
  - the conversation's shape (the Messages API's), which runs keep while they're going;
  - the safety rules: what a run may do once it has read outside content or beyond its scope;
  - error codes;
  - the tables' shape.
- **The model.** A change to the agents' tables changes every host's migrations: say so in the CHANGELOG, with what a
  host runs (`dotnet ef migrations add`).
- **Nothing of a host's in the package.** It reaches its host only through the seams; the architecture tests in
  `tests/AbpAgent.Tests` hold that.
- **Tests** come with the change: a unit test for logic, and a test through the sample host for anything a host sees.
- **CHANGELOG.md:** write what changes for someone using the packages under `## [Unreleased]`.

By contributing you agree that your contribution is licensed under the Apache License, Version 2.0.
