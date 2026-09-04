# Repository workflow

- Keep every mod in its own top-level directory.
- Every mod directory must contain `modinfo.ini` and `reframework/`.
- C# source mods go under `reframework/plugins/source/`.
- When the user's entire message is exactly `p`, commit the mod changed most recently, push the current branch to `origin`, and then run `./PackageMods.ps1`.
- Commit messages must say that the changes were AI-generated and name the model as `OpenAI GPT-5 Codex`. If the commit includes user-authored edits, describe those edits and identify them as manual modifications.
