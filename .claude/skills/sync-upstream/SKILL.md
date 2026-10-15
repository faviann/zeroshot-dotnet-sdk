---
name: sync-upstream
description: This skill should be used to move the SDK's native Zeroshot binding to a newer upstream release with feature parity, or to dry-run that move. It diffs upstream between the pinned and target releases, classifies every change in a parity checklist, implements and tests each wire change, and moves the one native pin. Triggers include "bump native Zeroshot", "sync with upstream", "bind 10.x", "upstream released a new version", and "parity dry run".
---

# Sync the SDK with an upstream Zeroshot release

The SDK binds exactly one native release. `native.props` holds that pin, and `tools/qualification/NativePin.cs` refuses any tracked file that names a different native version. A bump has two parts. The checklist accounts for every upstream change. The implementation brings the SDK to parity and moves the pin. A **dry run** stops after the checklist.

`scripts/upstream_delta.py` (next to this file) produces the checklist and checks it. Run it with `python3 -I`. It keeps a blobless upstream clone in `${TMPDIR:-/tmp}/zeroshot-upstream`, or wherever `--upstream` points, and fetches only when a release it needs is missing. Don't point it at a checkout someone is working in. Below, `$UP` is that clone and `$WORK` is `${TMPDIR:-/tmp}/sync-upstream`.

## 1. Pick the target

```bash
gh release list -R the-open-engine/zeroshot --exclude-drafts --limit 10
gh release view <tag> -R the-open-engine/zeroshot --json targetCommitish,isPrerelease,assets
```

- Target the newest stable `v*` release unless the user named one. Ignore `zeroshot-python-*` tags; they release upstream's Python SDK.
- If a patch release ships while you work, retarget to it.
- A prerelease (for example `11.0.0-rc.1`) can't be mirrored by SDK versions (README "Versioning"). Stop and tell the user.
- The tag's commit must equal `targetCommitish`. That SHA becomes `ZeroshotNativeSourceRevision`.

## 2. Generate the checklist

```bash
mkdir -p "$WORK"
python3 -I .claude/skills/sync-upstream/scripts/upstream_delta.py --to <tag> > "$WORK/checklist-<tag>.md"
```

`--from` defaults to the pinned release. The script refuses to run when that tag doesn't resolve to the pinned source revision. Keep the checklist out of the repository. It names the target version, so qualification would refuse it as drift. The checklist goes in the PR body.

Row kinds:

| ID | Source |
| --- | --- |
| `C:` | Each upstream commit in range. The bracket lists the areas it touched. |
| `S:` | A `$defs` entry the SDK copies into `src/Zeroshot.Sdk/Schemas/*.schema.json` that changed upstream. |
| `R:` | A definition that the SDK's copied definitions now reference but that the SDK schemas lack. |
| `N:` | A definition new upstream that nothing in the SDK closure reaches yet. |
| `M:` | An OpenRPC method added, removed or changed. |
| `F:` | An upstream file the SDK copies (compiled IR schema, license, cluster golden fixtures) that changed. |
| `P:` | An upstream source file the SDK's docs cite as evidence that changed. |

The SDK schemas are verbatim copies of upstream `protocol/openengine-cluster/v1` definitions, with three exceptions. `WorkerDescriptor` is a deliberate local override. `TokenCount` and `UnixTimestampMillis` are local names for inline native schemas.

## 3. Classify every row

Fill in Class and SDK handling for each row. Read the evidence before classifying:

- the release notes for each release in range: `gh release download <tag> -R the-open-engine/zeroshot -p 'zeroshot-release-notes-*' -D "$WORK/notes"`;
- `git -C "$UP" show <sha> -- protocol crates zeroshot/src sdks ui` for each commit that touches those areas;
- `docs/contracts/coverage.json` for what the SDK binds, and the doc that cites a `P:` file for why it was cited.

Read the whole diff of every commit that touches `zeroshot/src` or `ui`, not just its subject. Native publishes no schema for the history, dashboard, hosting and target transport DTOs that `src/Zeroshot.Sdk/Contracts/*.cs` writes by hand, so the commit row is the only place a change to them shows up. Built-in templates and the dashboard catalog are wire data too. A template that starts sending a value the SDK rejects breaks decoding.

| Class | Meaning | Handling must say |
| --- | --- | --- |
| `no-change` | It touches a surface the SDK binds, but the SDK already handles it. | Why. For example, the field is an open string, or the value fits an existing nullable or limit. |
| `additive` | A new optional field, variant, definition, method or route the SDK must bind. | The SDK type or operation, and the `tests/...cs` file that proves it. |
| `breaking` | A shape change that existing SDK code or callers can't accept as is. | The SDK change, the test file, and the migration note if public API changes. |
| `not-bound` | Outside what the SDK binds. Docs, UI assets, packaging, native CLI flags, and native-internal behavior go here. | Which surface it is and why the SDK doesn't bind it. |

Parity means every upstream HTTP and OECP capability in the `coverage.json` scope is either bound or recorded as `not-bound` with its reason. A new route or method never drops out silently. Upstream's own Python SDK (`sdks/`) is a parity signal. If it exposes a new capability, the .NET SDK needs a row that binds it or explains why not.

Classify by what the SDK must do:

- Run DTOs are strict and reject unknown fields (`docs/contracts/README.md` names the open ones). A field native now sends to a strict DTO is therefore never `no-change`, even when it is optional. It is `additive` if the SDK can add it without changing its public API, and `breaking` otherwise.
- A native behavior change on an operation the SDK binds is `no-change` when every shape stays the same. Say which shape you checked.
- A new discovery marker or capability flag is `additive`. Bind it the way existing markers are bound (see `CapabilityBindingTests`). Gate the operations that need it, so a request to an older target fails with a clear refusal.

A commit row may point at the `S:`, `M:` or `P:` rows its change produced instead of repeating them. Pure docs and release commits are `not-bound`.

Then run the check:

```bash
python3 -I .claude/skills/sync-upstream/scripts/upstream_delta.py --check "$WORK/checklist-<tag>.md" --stage plan
```

It regenerates the rows. It fails on a missing or stale row, an unknown class, empty handling, or an `additive` or `breaking` row that names no test path. **A dry run ends here.** Report the checklist path, the `additive` and `breaking` rows, and the open questions.

## 4. Implement

Follow #110, #115, #118 and #132, the earlier bumps. Land one PR titled `feat(sdk): bind native Zeroshot <version>`. Steps 1 to 4 don't build on their own, so commit them together once the build and tests pass. Steps 5 and 6 make a second commit, once `pack` passes.

1. **Schemas.** Copy every `S:` and `R:` definition verbatim from upstream at the target into the SDK schema file that holds its neighbours, keeping the three exceptions above. Replace `F:` copies from upstream. Run `python3 tools/generate-contracts.py`. A second run must leave `git status` clean.
2. **Hand-written contracts.** Native publishes no schema for history, dashboard, hosting or target transport DTOs. Update `src/Zeroshot.Sdk/Contracts/*.cs` from the `P:` and `C:` rows that change `zeroshot/src` wire types.
3. **Tests.** Every `additive` and `breaking` row gets a test in the file it names. Call the public API the way a consumer does. Decode or encode literal native JSON with `NativeJson`, or drive the operation through the TestKit fakes, and assert against literal expected values. Also check that the previous native binding is refused as `Mismatched`, as `SubmitTests` does.
4. **Public API.** Record additions in `src/Zeroshot.Sdk/PublicAPI.Unshipped.txt`. A removed or changed entry needs `docs/migration/<native version>.md`, shaped like the newest file already there.
5. **Pin.** Edit `native.props`. Set the version, the source revision, and four digests. Take the archive digests from the release's `SHA256SUMS`. For the executable digests, extract `zeroshot` from `zeroshot-<tag>-x86_64-unknown-linux-musl.tar.gz` and `zeroshot.exe` from `zeroshot-<tag>-x86_64-pc-windows-msvc.tar.gz`, then run `sha256sum` on each. Set `ZeroshotSdkRevision` in `src/Directory.Build.props` to 1. Add a `<version>.*` row to the README "Native compatibility" table. Move `nativeVersion` and `sourceRevision` in `docs/contracts/coverage.json`, and update every operation row a change touched. Move the `Zeroshot.Client` package version in every `examples/*` project to the new candidate. With a stale version, NuGet warnings reach the consumers' JSON output and the witness fails.
6. **Prose.** `pack` (below) lists every stale version mention by file and line. Move live statements to the new release. Earlier-release history stays, and gets an allowlist entry only when it truly cites an older binding. The scanner also flags an old version inside a link path, so link to `docs/migration/` rather than to an older release's notes. Add a dated note to `docs/contracts/native-inventory.md` saying what changed in its scope, as #110 did.

## 5. Verify

All of these must pass on the real artifacts:

```bash
dotnet build Zeroshot.sln -c Release                     # 0 warnings
dotnet test --project tests/Zeroshot.Sdk.Tests/Zeroshot.Sdk.Tests.csproj -c Release
dotnet test --project tests/Zeroshot.Cli.Tests/Zeroshot.Cli.Tests.csproj -c Release
dotnet test --project tests/Qualification.Tests/Qualification.Tests.csproj -c Release
dotnet run --project tools/qualification -c Release -- pack --out "$WORK/candidate"
tools/native-witness/run.sh                              # stock binary of the new pin, Linux x64
python3 -I .claude/skills/sync-upstream/scripts/upstream_delta.py --check "$WORK/checklist-<tag>.md" --stage done
```

`--stage done` also requires every test file a row names to have changed since `origin/main`. The witness needs root or passwordless `sudo` (see `tools/native-witness/README.md`). If it can't run, say so in the PR; don't skip it silently. CI runs qualification on six platforms and the Windows witness. Gate the merge on the newest run of each workflow, because cancelled duplicate runs show as failed.

## 6. PR body

Follow #110's PR body. Lead with the target release, its source revision, and the consumer impact (a client bound to the old release is refused as `Mismatched`). Give a "Contract differences" table built from every checklist row that isn't `not-bound`. Count the `not-bound` rows in one sentence. Finish with the verification output.
