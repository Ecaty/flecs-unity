# Flecs Unity bridge backlog

Package tasks only; product tasks live in the product's `docs/BACKLOG.md`. Keep it short; mark work done only with current evidence.

## Open

1. **Device baseline of bridge costs** on the lowest target phone (IL2CPP): per-frame bridge calls, ms and GC (audit measurements 1 and 6). Waits for the test phone.
2. **Use exported member units/ranges** (`fu_type_layout`) to describe component fields to tools and AI, once Core adds ranges.

## Decisions

- 2026-11-13: Typed reads/writes check each managed mirror against the native member layout once per (managed type, native type).
