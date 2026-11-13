# Unity package instructions

Read README.md and Ecaty's canonical [PROJECT](https://github.com/Ecaty/Ecaty/blob/main/docs/PROJECT.md), [DEVELOPMENT](https://github.com/Ecaty/Ecaty/blob/main/docs/DEVELOPMENT.md) and [STATUS](https://github.com/Ecaty/Ecaty/blob/main/docs/STATUS.md). In the composed checkout they are at `../../docs/`.

Use namespace `Runtime.Flecs`; keep runtime and Editor assemblies separate. Unity owns presentation while native Flecs owns behavior/data. Keep main-thread P/Invoke lifecycle and batched snapshots. Preserve C ABI layout and `.meta` GUIDs. Bootstrap is configurable and must not impose sample content/cameras or a product UI. Do not put provider credentials or chat history in this reusable package. All new platform claims require a matching native build and test. Verify with the parent application's native/Unity checks.
