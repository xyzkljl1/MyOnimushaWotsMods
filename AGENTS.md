# Repository workflow

- Keep every mod in its own top-level directory.
- Every mod directory must contain `modinfo.ini` and `reframework/`.
- C# source mods go under `reframework/plugins/source/`.
- When the user's entire message is exactly `p`, commit the mod changed most recently, push the current branch to `origin`, and then run `./PackageMods.ps1`.
- Commit messages must say that the changes were AI-generated and name the model as `OpenAI GPT-5 Codex`. If the commit includes user-authored edits, describe those edits and identify them as manual modifications.

## Nexus Mods publishing

- `Updater/` is a publishing tool, not a mod. Do not apply the mod directory-structure rules to it.
- Before any Nexus Mods publishing task, read `Updater/README.md` completely and follow it.
- Build packages with `./PackageMods.ps1`; NexusUpdater consumes the resulting root-level `.7z` whose name and version come from the mod's `modinfo.ini`.
- The local configuration is `Updater/updater.json`. It contains `NEXUSMODS_API_KEY` and per-mod `modId` values and is intentionally ignored by Git.
- If an isolated worktree has no local configuration, use `--config E:\OtherGame\Onimusha\MyOnimushaWotsMods\Updater\updater.json` to reference the main workspace configuration. Never copy that configuration into a worktree.
- Never print, echo, log, commit, or pass `NEXUSMODS_API_KEY` on the command line. Do not expose presigned upload URLs.
- Always run NexusUpdater with `--dry-run` first. Perform a real upload only when the user explicitly requests it.
