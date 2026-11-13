# Flecs Unity bridge: start here

Unity package `com.runtime.flecs`: managed session, typed bridge and presentation for the native Flecs runtime (C ABI). It is part of the runtime engine every product builds on. This repo stands alone: this file and [BACKLOG](BACKLOG.md) are enough to work here. In a product checkout it is a `Packages/` submodule; the product repo's `docs/README.md` is the cross-repo overview and links every repo's backlog.

## Read on demand

- [BACKLOG](BACKLOG.md): open tasks and decisions for this package.
- [README](README.md): package overview.
- Native side and bridge audit: [flecs-native AGENTS](https://github.com/Ecaty/flecs-native/blob/main/AGENTS.md), [FLECS-ARCHITECTURE-AUDIT](https://github.com/Ecaty/flecs-native/blob/main/docs/FLECS-ARCHITECTURE-AUDIT.md).

## Rules

Use namespace `Runtime.Flecs`; keep runtime and Editor assemblies separate. Unity owns presentation while native Flecs owns behavior/data. Keep main-thread P/Invoke lifecycle and batched snapshots. Preserve C ABI layout and `.meta` GUIDs. Bootstrap is configurable and must not impose sample content/cameras or a product UI. Do not put provider credentials or chat history in this reusable package. All new platform claims require a matching native build and test. Verify with the parent application's native/Unity checks.

## Working across repos

- Package tasks go in this BACKLOG; product tasks in the product's `docs/BACKLOG.md`. Keep both short; mark work done only with current evidence.
- Commit and push a feature branch here first, then update the pointer in the product repo.
- Package docs go in `Documentation~/` (ignored by Unity, so no `.meta` files); root files need `.meta` files.
- Links to `https://github.com/Ecaty/Ecaty/blob/main/...` are the first product's docs; in a composed checkout they are at `../../`.
