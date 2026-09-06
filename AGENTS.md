# Repository workflow

- Keep every mod in its own top-level directory.
- Every mod directory must contain `modinfo.ini` and `reframework/`.
- C# source mods go under `reframework/plugins/source/`.
- ModBase is split into `Util/ModBase.cs` (identity, logging, one-time error reporting, and managed-object helpers) and the optional `Util/ModBase.Config.cs` (configuration persistence and all ImGui helpers). The configuration module depends on the base module and must never be copied alone. Any selected module must be copied verbatim from the same specific committed Git revision and concatenated into the mod's own source file, with a separate embedded-source header recording that file's source blob SHA-1 and the shared source commit hash. Never install the utility modules as standalone source plugins, and never embed uncommitted utility working-tree changes. If either utility file must change, commit the intended state of both files before copying either one into a mod.
- Never create runtime mod configuration files anywhere under a mod's source directory. Store them only in the installed game's `reframework/data/` directory, named directly as `<ModName>.json` without a per-mod subdirectory.
- Never commit or include runtime mod configuration files in release packages. Packaging scripts must explicitly exclude them even if one is accidentally placed under a mod directory.
- When the user's entire message is exactly `p`, commit the mod changed most recently, push the current branch to `origin`, and then run `./PackageMods.ps1`.
- When the user's entire message is exactly `up`, package the mods and use NexusUpdater to publish the intended mod to Nexus Mods. The intended target mod's directory must have no uncommitted or untracked changes before packaging or uploading; if that mod directory is not clean, stop and ask the user what to do without committing, stashing, discarding, or otherwise altering those changes. Unrelated changes elsewhere in the repository do not block the upload and must be left untouched. If the intended target mod is not already unambiguous, stop and ask the user which mod to publish.
- When the user's entire message has the form `up:<text>`, follow the same workflow and preconditions as `up`, and use all text after the first colon as the Nexus Mods changelog for that upload.
- Commit messages must say that the changes were AI-generated and name the model as `OpenAI GPT-5 Codex`. If the commit includes user-authored edits, describe those edits and identify them as manual modifications.

## IL2CPP dump indexing and diagnostic process safety

- Treat `il2cpp_dump.json` and similarly large exports as local diagnostic data. Never commit them, place them in a mod directory, or include them in a release package.
- Use the local streaming indexer at `LocalDebug/DumpIndex/` for IL2CPP dump analysis. Its default cache is `LocalDebug/Il2CppDumpCache/`; the tool, generated shards, indexes, extracted query results, and other diagnostic artifacts must remain local and untracked.
- If the dump is new or has changed, build the index with `dotnet run --project LocalDebug/DumpIndex/DumpIndex.csproj -- build il2cpp_dump.json LocalDebug/Il2CppDumpCache 64`. The indexer records source metadata and queries must reject a stale cache.
- Search the index with `find-type` or `find-member`, then use `extract-type` to read only the exact type needed. Prefer indexed queries over rescanning the original dump.
- Never load an entire large dump or log into PowerShell or memory. In particular, do not use `$lines = Get-Content <large-file>`, `ReadAllText`, full-file deserialization, `0..N`, or `$lines[a..b]` on large inputs.
- When indexed lookup is insufficient, use bounded streaming tools such as `rg` with a match limit and small context, `Get-Content -Tail`, `StreamReader`, or `Utf8JsonReader`. Keep only the current record and the requested result window in memory.
- Check a file's size before analysis. For files larger than 100 MB, use a streaming or indexed approach, cap match counts and output size, and run no more than one full-file scan at a time.
- Probe exports and logs must be targeted and bounded: restrict object depth, collection length, sampling frequency, match count, and log size. Read only relevant tails or indexed records.
- A command that yields a running session or child process remains active independently of the current task. Always retain its session identifier and poll or wait until it completes. If it is no longer needed, terminate that exact diagnostic process after verifying its identity; never leave an abandoned parser, build, probe, watcher, or server running.
- Before reporting a diagnostic or mod task complete, verify that every process started for that task has either completed or is an explicitly intended long-running process. Do not assume that completing a conversation turn automatically stops child processes.
- Monitor long-running diagnostic processes. Stop and redesign a diagnostic command if its private memory exceeds 1 GB without a clearly justified need; do not work around excessive allocation by relying on the page file.

## Nexus Mods publishing

- `Updater/` is a publishing tool, not a mod. Do not apply the mod directory-structure rules to it.
- Before any Nexus Mods publishing task, read `Updater/README.md` completely and follow it.
- Build packages with `./PackageMods.ps1`; NexusUpdater consumes the resulting root-level `.7z` whose name and version come from the mod's `modinfo.ini`.
- The local configuration is `Updater/updater.json`. It contains `NEXUSMODS_API_KEY` and per-mod `modId` values and is intentionally ignored by Git.
- If an isolated worktree has no local configuration, use `--config E:\OtherGame\Onimusha\MyOnimushaWotsMods\Updater\updater.json` to reference the main workspace configuration. Never copy that configuration into a worktree.
- Never print, echo, log, commit, or pass `NEXUSMODS_API_KEY` on the command line. Do not expose presigned upload URLs.
- Always run NexusUpdater with `--dry-run` first. Perform a real upload only when the user explicitly requests it.
- Every uploaded Nexus file version must become the primary version. NexusUpdater enforces this with a fixed `primary_mod_manager_download = true`; do not make it configurable.
- Do not change a mod's version merely because its changes are committed or pushed.
- Before every Nexus Mods upload, verify that the version in the mod's `modinfo.ini` has never been used by any existing version of the target Nexus file. Never upload two files with the same version.
- Change the local `modinfo.ini` version only after Nexus confirms that the new file version was created successfully. That change prepares the next release and must use a version not already present on Nexus; if the next version is not unambiguous, ask the user instead of guessing.
