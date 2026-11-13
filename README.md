# Flecs Unity integration

Embedded Unity package for the matching native Flecs C ABI. Includes `FlecsSession`, `FlecsWorld`, a configurable `FlecsBootstrap`, a `.flecs` TextAsset importer and domain-reload cleanup. It has no product camera, chat UI or AI account credentials.

Place the matching native library in the consuming Unity project's plugin directory. The initial verified target is Windows x64 with Unity 6000.6.7f1. Configure startup with an application TextAsset and material; native App and Core are built by the consuming project.

Canonical context: [Ecaty AGENTS](https://github.com/Ecaty/Ecaty/blob/main/AGENTS.md), [PROJECT](https://github.com/Ecaty/Ecaty/blob/main/docs/PROJECT.md), [DEVELOPMENT](https://github.com/Ecaty/Ecaty/blob/main/docs/DEVELOPMENT.md), [STATUS](https://github.com/Ecaty/Ecaty/blob/main/docs/STATUS.md).

Current C ABI: **v2**, 80-byte view unchanged. `fu_write_presented` accepts batched presentation acknowledgements into reflected `Presented`; authoritative components remain native-owned. Match the native binary and managed bridge version. Label owns its reflected UTF-8 string through C++ lifecycle hooks, including prefab copies.

`FlecsSession.ManagedScriptSource(path)` reads the active native managed source; `SetManagedScript(path, code)` creates/reloads it. Use the matching native library containing the additive `fu_managed_script_source` export. In Ecaty's pinned upstream runtime, managed Script reacts to Clock and external component inputs. The bridge presents its normal snapshot and writes Presented; it does not execute a second behavior system. See [runtime engine](https://github.com/Ecaty/Ecaty/blob/main/docs/RUNTIME-ENGINE.md).


The matching native runtime now supplies generic `TryRead<T>`, `WriteBatch<T>` and `ReadQuery<T>` for unmanaged layouts validated against runtime reflection, plus `Lookup`, `QueryChanges`, component JSON, pointer/keyboard events, source edit/undo/capture, Shell and native metrics/alerts/documentation. These have no product/provider dependency. Unsafe code is limited to pinned data transfer; strings/owning types use reflected serialization. Do not retain entity/type IDs across session replacement. Source and selected-state persistence are composed by the consuming app, not hidden inside this package.

The separate embedded `com.runtime.capabilities` package supplies the reusable binding registry/lifecycle, reflected Script schema, Unity capability adapters and native audio/geometry wrappers. Adapters receive configuration and publish results against a native session; dictionaries retain disposable bindings rather than another ECS world. Explicit pose/appearance ownership protects the normal presentation bridge. Optional licensed plugin adapters live in the composed application's Assets source because several vendors use Assembly-CSharp; they are independently gated and registered. Full workflow and coverage: host project https://github.com/Ecaty/capabilities-unity/blob/main/Documentation~/RUNTIME-CAPABILITIES.md.

This bridge assembly no longer references the Unity navigation, rigging, Cinemachine, Timeline, VFX or URP capability assemblies. Those dependencies belong to the capability package. Technical package/namespace identifiers and the native C ABI remain stable.
