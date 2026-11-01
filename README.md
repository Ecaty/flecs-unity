# Flecs Unity integration

Embedded Unity package for the matching native Flecs C ABI. Includes `FlecsSession`, `FlecsWorld`, a configurable `FlecsBootstrap`, a `.flecs` TextAsset importer and domain-reload cleanup. It has no product camera, chat UI or AI account credentials.

Place the matching native library in the consuming Unity project's plugin directory. The initial verified target is Windows x64 with Unity 6000.6.7f1. Configure startup with an application TextAsset and material; native App and Core are built by the consuming project.

Canonical context: [Ecaty AGENTS](https://github.com/Ecaty/Ecaty/blob/main/AGENTS.md), [PROJECT](https://github.com/Ecaty/Ecaty/blob/main/docs/PROJECT.md), [DEVELOPMENT](https://github.com/Ecaty/Ecaty/blob/main/docs/DEVELOPMENT.md), [STATUS](https://github.com/Ecaty/Ecaty/blob/main/docs/STATUS.md).

Current C ABI: **v2**, 80-byte view unchanged. `fu_write_presented` accepts batched presentation acknowledgements into reflected `Presented`; authoritative components remain native-owned. Match the native binary and managed bridge version. Label owns its reflected UTF-8 string through C++ lifecycle hooks, including prefab copies.
