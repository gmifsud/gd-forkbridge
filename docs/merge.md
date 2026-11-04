# Merge workspace

Merge a **source** branch **into** a **target** branch of **one** configured repository, resolve conflicts in the
browser, and publish the result as an integration branch — without touching your checkout.

It is a separate module (`src/ForkBridge.Merge`) beside the transfer pipeline and Harmony. It does not use
alignment profiles, SyncBase, product paths or policy filters: **Git's own merge is the source of truth**, and the
UI explains it.

```
choose repository + branches ─► analyze (pinned SHAs, merge-tree) ─► session (isolated worktree, git merge --no-commit)
   ─► resolve supported conflicts ─► finalize candidate ─► validate ─► approve exact candidate ─► publish integration branch
```

## Terminology

| Term | Meaning |
|---|---|
| Source | the branch being merged (`feature/x`) |
| Target | the branch it is merged **into** (`develop`) |
| Base | Git's merge base. With several merge bases Git builds a virtual base; conflicted files show that base, clean files show merge base #1 |
| Result | what the candidate will contain for a path |
| Plan | immutable analysis at pinned commits (`mplan-…`) |
| Session | executable, versioned merge workspace for one plan (`msess-…`) |
| Candidate | the local merge commit created by Finalize; parents are exactly `[target, source]` |

## Using the Merge tab

1. **Merge** in the top navigation → choose a **repository** (any alias in `forkbridge.yml`; no Harmony profile needed).
2. **Refresh refs** fetches your checkout (read-only) and its remote **into the harness mirror**. *Offline refresh*
   reads only your checkout. Branches are grouped by provenance, and the selection carries the namespace:

   | Group | Ref in the harness mirror | Meaning |
   |---|---|---|
   | remote (fetched by ForkBridge) | `refs/remotes/origin/<b>` | fetched from the checkout's remote URL at the last refresh |
   | local branch (your checkout) | `refs/fb/local/heads/<b>` | your local branch, may contain unpushed commits |
   | checkout's remote-tracking ref | `refs/fb/local/remote/<b>` | as **you** last fetched it |

   Refs older than one hour, offline refreshes and never-refreshed repositories are marked **stale**.
3. Pick **source** and **target** (*Swap* exchanges them) → **Analyze merge**. The server resolves both selections
   to commit ids; the header reads `Merging <source> @ <sha> into <target> @ <sha>`.
4. Review the outcome:

   | Outcome | Meaning | Session |
   |---|---|---|
   | Already up to date | source is contained in target (incl. identical tips) | refused: nothing to merge |
   | Fast-forward possible | target has no own commits | allowed; creates a `--no-ff` merge commit |
   | Clean merge | Git merged everything | allowed |
   | Conflicts need resolution | Git left paths unmerged | allowed |
   | Blocked | unrelated histories, case collisions on this host | refused, with the reason |

   Filters: **All changes**, **Unresolved**, **Resolved**, **Auto-merged** (changed on both sides, merged by Git),
   **Special cases** (binary, add/add, modify/delete, unsupported). Each row shows path (and old path), what Git did,
   what can be done here, the state and the reason.
5. **Start merge session** → `/merge/sessions/<id>`. Click a file to open the **inspector**:

   | Tab | Shows |
   |---|---|
   | Result | editable text (supported text conflicts), otherwise read-only |
   | Target changes | base → target |
   | Source changes | base → source |
   | Base | base content |
   | Final diff | target → current result |

   Actions appear only when the entry supports them: **Use target file**, **Use source file**, **Keep modified
   file**, **Delete file**, **Save draft**, **Mark resolved**. Keys outside the editor: ←/→ (K/J) files, 1–5 tabs,
   Esc close. Inside the editor only **Ctrl/⌘+S** (save draft) is handled. Unsaved edits are never discarded by
   one keystroke: close/next/previous asks twice.
6. **Finalize** (all conflicts resolved, no pending drafts) → **Validate** → **Approve** → **Publish**.

The URL keeps the session, open file and filter (`?file=&filter=`), so a reload restores the same view. Drafts and
resolutions live on the server; the browser keeps only the last repository/branch choice.

## Supported conflicts

| Conflict | Here | Notes |
|---|---|---|
| Text content | edit result, use target, use source | diff3 markers; marker lines are a warning — marking resolved with markers needs explicit confirmation |
| Add/add (text) | edit result, use target, use source | no base |
| Modified in target, deleted in source / deleted in target, modified in source | keep modified file, delete file | deletion removes the index entry; never an empty file |
| Binary | use target, use source | no text editor |

Not supported (shown with the reason; **finalization stays blocked**; no fake *Mark resolved*): rename/rename,
rename/delete, rename collisions, file/directory, symlinks, submodules, file-type/mode conflicts, case-only
collisions on case-insensitive hosts, unsafe paths. Resolve those in a local clone and analyze again.

Editing is also unavailable (version choice still works) for: content that is not valid UTF-8 (UTF-8 with BOM and
UTF-16 with BOM are supported), mixed line endings, files above `merge.maxEditableBytes` (default 1 MiB; never sent
as a truncated preview), and paths with a `filter=` attribute such as Git LFS (the stored pointer is shown).

Edits keep the file's encoding, BOM, line-ending style (LF/CRLF), presence or absence of a final newline, and its
file mode. The editor always works on the **repository form** of the content: an unresolved conflict is rebuilt from
Git's stage blobs (`git merge-file --diff3`), never read from the worktree, where `.gitattributes` checkout conversions
(`eol`, `working-tree-encoding`, `ident`) may have changed the bytes.

## Concurrency, recovery, abort

* Every write carries `expectedSessionRevision` and, for content, `expectedContentHash`. A save from a stale tab is
  rejected (HTTP 409, exit 13) with a **Reload file** action; nothing is overwritten silently.
* Session mutations take a per-session lock (cross-process) and run through the web host's single job gate.
* Before each mutation the workspace is verified: HEAD = target, MERGE_HEAD = source, unmerged set = unresolved
  conflicts, each resolution present in the index, no unstaged or untracked changes. A mismatch marks the session
  **Damaged**.
* **Recover** rebuilds the worktree (pinned target, same merge) and replays every recorded resolution from its blob.
  It also completes a session whose creation was cancelled or interrupted. A finalized candidate survives only if
  the rebuilt tree is identical.
* **Abort** removes the harness worktree(s) (ownership-checked) and the session refs; the session record, drafts and
  audit log are kept.

## Finalize, validation, approval

* **Finalize** re-verifies the workspace, writes the tree, checks that it differs from Git's merge result only in
  conflicted paths, and creates the candidate with `git commit-tree -p <target> -p <source>` (no hooks). After
  finalize, resolutions are frozen; **Reopen** discards the candidate, its validation binding and any approval.
* **Validate** runs `merge.repositories.<alias>.validation` in a separate detached worktree of the exact candidate.
  Commands execute only when the profile is `trusted: true` **and** you tick *Trust validation commands for this run*.
  Verdicts: `PASSED`, `FAILED`, `NOT_CONFIGURED`, `UNTRUSTED` (nothing executed), `USER_REPO_MODIFIED` (your checkout
  changed during validation). Worktrees are not a sandbox — see `docs/troubleshooting-and-limitations.md`.
* **Approve** names the exact candidate and needs a validation record for it. `PASSED` approves directly;
  `NOT_CONFIGURED`/`UNTRUSTED` require *I approve this candidate without passing validation*; `FAILED` and
  `USER_REPO_MODIFIED` cannot be approved. The approval binds repository + remote, plan + analysis hash,
  session + revision, source/target refs and SHAs, candidate, validation id, merge-options fingerprint, integration
  branch and PR target.

## Publication

* Pushes `forkbridge/merge/<target>/<session-id>` (prefix `merge.branchPrefix`) to the checkout's remote. Never the
  target branch, never `+`/force, never a protected branch (`merge.protectedBranches`). The push is create-only
  (`--force-with-lease=<branch>:` with an empty expected value = "must not exist"), so a branch created concurrently
  is refused rather than fast-forwarded. Your local checkout is not updated.
* Before pushing, the remote target must still equal the pinned target SHA, otherwise publication is refused
  (analyze again). Source movement is reported, not merged: the plan stays pinned; analyze again to include newer
  source commits.
* Retry is safe: the integration branch is checked first; if it already points at the approved candidate (a push whose
  record was lost) it is recorded as already present, even if the target moved afterwards; at any other commit
  publication is refused. A failed pull-request creation is retried by publishing again.
* *Create a pull request* uses `publish.provider` (Azure DevOps today). A PR failure after a successful push is
  recorded; the branch stays published.

## Isolation guarantees

Analysis and resolution never modify your checkout, its index, current branch, in-progress merge/rebase or remote
configuration (asserted by tests with a checkout that is itself mid-merge with staged and untracked files). All work
happens in `<stateDir>/mirrors/<alias>.git` and `<stateDir>/merge/`.

Every analysis/workspace git call runs with an isolated configuration: no system or global git config
(`GIT_CONFIG_NOSYSTEM=1`, harness `GIT_CONFIG_GLOBAL`), no global attributes file (`core.attributesFile` = empty
harness file), attributes read from the pinned target tree (`--attr-source`), an empty harness hooks directory, `core.fsmonitor=false`,
literal pathspecs and no replace objects. Consequently user/system merge drivers, clean/smudge filters (including
LFS), hooks and credential helpers never run there. Pinned merge configuration (part of the options fingerprint):

```
strategy=ort  merge.renames=true  merge.directoryRenames=false  merge.conflictStyle=diff3  merge.renormalize=false
rerere.enabled=false  core.autocrlf=false  core.safecrlf=false  commit.gpgsign=false
```

`merge-tree --write-tree` writes objects into the harness mirror (never a worktree or index). Unrelated histories
are refused; `--allow-unrelated-histories` is never enabled. The commit identity is read from your checkout's
`user.name`/`user.email` at session creation.

## Configuration

```yaml
merge:
  branchPrefix: forkbridge/merge            # integration branches: <prefix>/<target>/<session>
  protectedBranches: [main, master, development, develop, "release/*", "hotfix/*"]
  maxEditableBytes: 1048576
  repositories:
    qisl-backend:
      validation:
        trusted: true
        steps:
          - id: build
            kind: build
            run: [dotnet, build, solutions/QiSL.Platform.Web.All.sln, -c, Release]
            timeoutSeconds: 3600
```

## Requirements

Git **2.40+** (`merge-tree --write-tree`, and `--attr-source` so the bare-mirror analysis and the worktree merge read the same `.gitattributes`); older Git is refused with an explicit message.

## API

`/api/merge/*`, same loopback + token + JSON-only model as the rest of the API. Inputs are aliases, a namespace +
exact branch name, and harness ids — never paths or git arguments.

```
GET  /api/merge/repositories                     GET  /api/merge/repositories/{alias}/refs
POST /api/merge/repositories/{alias}/refresh     POST /api/merge/plans            GET /api/merge/plans/{id}
POST /api/merge/plans/{id}/sessions              GET  /api/merge/sessions/{id}    GET /api/merge/sessions/{id}/files/{entryId}
POST /api/merge/sessions/{id}/drafts | resolutions | finalize | reopen | validate | approve | publish | recover | abort
```

Errors: 400 usage, 404 unknown id, 409 stale/refused (exit 13 for stale), 422 unsupported conflict, 403 safety.

## Demo

```powershell
pwsh examples/demo/New-ForkBridgeMergeDemo.ps1 -Path D:\Temp\fb-merge-demo
forkbridge-web --config D:\Temp\fb-merge-demo\forkbridge.yml
```

Merge → `demo-shop` → Refresh refs → source `feature/checkout`, target `develop` → Analyze merge. Also try
`feature/fast-forward` (fast-forward), `feature/merged` (up to date) and `feature/rename` (unsupported
rename/delete).

Browser smoke test against that demo (Playwright installed globally, not a project dependency):

```powershell
$env:NODE_PATH = (npm root -g); $env:FB_URL = 'http://127.0.0.1:5178'; $env:FB_TOKEN = '<token from the URL>'
node ui/e2e/merge-smoke.mjs
```
