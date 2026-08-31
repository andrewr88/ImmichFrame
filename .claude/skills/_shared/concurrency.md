# Shared working tree — concurrency policy

This working tree is shared with parallel developers. Any change you did not author — staged, unstaged, or untracked — is someone else's in-flight work. Leave it alone.

- Never run `git stash`, `git reset`, `git restore`, `git checkout --`, or `git clean` here — a parallel developer sees their files vanish and assumes the work is lost, and `git stash pop` silently drops conflicting hunks. No exceptions, no "unless the tree is fully yours". Need a clean baseline? Use `git worktree add`.
- Never `git add -A`; stage explicit paths only.
- Scope edits to your task's files.
- Do not flag others' hunks as scope leak or recommend reverting them.
