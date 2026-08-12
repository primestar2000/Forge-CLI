# CLAUDE.md — `forge` CLI

Operating guide for agents working in this repo. Read this before writing code.

## What this is

`forge` is a Laravel-Artisan-style architectural code generator for .NET, shipped as a `dotnet tool`
and invoked as `dotnet forge <namespace>:<verb>`. It scaffolds entities, repositories, Wolverine
commands/queries, authorization wiring, jobs, and whole solution skeletons.

**Three documents, in precedence order:**

1. **This file** — current, corrected engineering decisions. **Authoritative when it conflicts with
   the design doc.**
2. [DESIGN-REVIEW.md](DESIGN-REVIEW.md) — why those decisions changed, with evidence.
3. [forge-cli-design-roslyn.md](forge-cli-design-roslyn.md) — the original vision doc. Excellent on
   *intent* and *product surface*; several implementation specifics are superseded (see below).

If you find yourself implementing something from the design doc that this file contradicts, this
file wins. If you find a *third* answer that's better than both, say so before writing the code.

---

## Superseded by review — do not implement as originally written

| Design doc says | Actually do | Why |
|---|---|---|
| `invoke:*` / `db:seed` boot the host in-process | Out-of-process via a `Forge.Runtime` companion package | BLOCKER-1: assembly/TFM/deps.json conflicts make in-process loading unworkable |
| `ITemplate` methods return `void` | Return `Task<GenerationPlan>` | BLOCKER-2: `--dry-run`, `--json`, and atomic rollback are otherwise unimplementable |
| Anchor via `.OfType<PropertyDeclarationSyntax>().First(...)` | `MemberDeclarationSyntax` + `FirstOrDefault` + fallback chain | FIX-1: as written it throws on `make:repo`, the flagship command |
| `.WithTriviaFrom(anchor)` | Copy leading *whitespace* only | FIX-3: duplicates doc comments and `#region` directives |
| Class named `SyntaxEditor` | Name it `SyntaxPatcher` | FIX-2: collides with `Microsoft.CodeAnalysis.Editing.SyntaxEditor` |
| Colon commands "grouped under namespace parents" | Flat registration + custom grouped help renderer | FIX-5: mutually exclusive; grouping must be hand-written |
| Shell completion "almost for free" | Budget ~half a day; hand-write the shell scripts | FIX-7: GA ships the `[suggest]` directive, not script generation |

**Also add before v1** (missing from the design): `forge init` (brownfield adoption), a config
`version` field, a documented exit-code contract, `db:migration`, and the Roslyn adversarial test
corpus. See DESIGN-REVIEW.md §4 and §6.

---

## Verified environment facts

Confirmed empirically on this machine — trust these over recollection:

- **SDK:** .NET 10 (`10.0.301`) on this machine. **Target `net8.0` for `Forge.Cli`** — see the
  roll-forward finding below. Do not target `net10.0`.
- **`System.CommandLine` 2.0.10 is stable/GA.** Not a beta risk. **But the GA API differs from
  beta-era examples** — see the API block below and follow it exactly.
- **Colon-named commands work.** `new Command("make:repo", ...)` parses and dispatches correctly.
  They register flat; help lists them flat.
- **`dotnet forge ...` works for local tools** with `ToolCommandName=dotnet-forge`. All of
  `dotnet forge`, `dotnet dotnet-forge`, `dotnet tool run dotnet-forge` are equivalent.
- **`<RollForward>LatestMajor</RollForward>` is mandatory.** Verified by packing a tool targeting a
  framework absent from the machine: without it the tool dies with *"You must install or update
  .NET to run this application."*; with it, it ran fine on a higher major. The default policy is
  `Minor`, which crosses patch and minor versions but **not majors**. Omitting this makes forge
  appear broken on any machine that doesn't happen to have the exact target runtime.
- **`dnx` and `dotnet tool exec` exist on .NET 10** — one-shot tool execution with no install, like
  `npx`. This is how `forge init` bootstraps a repo that has no tool manifest yet.
- **`.cs.txt` stubs need `<WithCulture>false</WithCulture>` AND an explicit `<LogicalName>`.**
  Found the hard way. MSBuild parses `Repository.cs.txt` as *name + culture + extension*, and `cs`
  is a valid culture (Czech) — so by default every stub is compiled into a satellite assembly at
  `cs/Forge.Cli.resources.dll`, invisible to `GetManifestResourceStream`. **The build succeeds and
  every generator fails at runtime.** Separately, the default manifest-name logic strips the `.cs`,
  yielding `...Snippets.Repository.txt`. Both are handled in `Forge.Cli.csproj`; `StubRepositoryTests`
  guards them. Do not "simplify" that ItemGroup.

### System.CommandLine GA API — copy this shape

Beta spellings (`SetHandler`, `AddOption`, `AddCommand`, `new Option<T>("--n", description:)`) do
**not** compile against 2.0.10. Most training data and tutorials show the beta API. Use this:

```csharp
using System.CommandLine;

var nameOpt  = new Option<string>("--name", "-n") { Description = "Entity name", Required = true };
var forceOpt = new Option<bool>("--force")        { Description = "Overwrite existing files" };

var cmd = new Command("make:repo", "Scaffold a repository pair");
cmd.Options.Add(nameOpt);
cmd.Options.Add(forceOpt);
cmd.SetAction(pr => Handle(pr.GetValue(nameOpt)!, pr.GetValue(forceOpt)));   // -> int

var root = new RootCommand("forge");
root.Subcommands.Add(cmd);
return root.Parse(args).Invoke();
```

---

## Non-negotiable invariants

These are the tool's contract with its users. A change that violates one is a bug regardless of what
else it improves.

1. **Never overwrite hand-written code.** A generator hitting an existing target is a no-op plus a
   warning, never a silent overwrite. This is enforced by `.forge/manifest.json`, which records the
   content hash of every fully-generated file:

   | State | `--force` | `--force --overwrite-modified` |
   |---|---|---|
   | Hash matches the manifest (untouched output) | overwrites | overwrites |
   | Hash differs (someone edited it) | **refuses**, exit 4 | overwrites |
   | No manifest entry (unknown provenance) | **refuses**, exit 4 | overwrites |

   Every generator goes through `TemplateContext.CreateOrSkip` — do not reimplement this check.
   A refusal is a `FileAction.Skip { Protected = true }`, which surfaces as exit code 4 so a
   pipeline never mistakes "we protected you" for "we overwrote it".

   Only `Create` targets are tracked. Patched files (`IUnitOfWork`, `DbContext`,
   `DependencyInjection`, Mapster configs) are co-owned with the developer by design, and patches
   are additive and idempotent, so they are never rewritten wholesale.

   **`.forge/manifest.json` must be committed** — it is what makes `--force` safe for the whole team.
2. **Plan, then apply — never write from a generator.** Generators return `GenerationPlan`. Only
   `PlanExecutor` touches disk. This is what makes `--dry-run` honest.
3. **All-or-nothing per command.** A command that writes 8 files writes 0 or 8. Stage to temp files,
   `File.Move` at the end, clean up on any exception.
4. **Anchors are syntax nodes, never regex or line numbers.** Every edit to existing source goes
   through Roslyn. No `string.Replace`, no `Regex`, no line indexing on C# source. Ever.
5. **Never `.First()`, `.Single()`, or `!` on anything derived from user source.** User code will
   have the shape you didn't expect. `FirstOrDefault` + an explicit fallback chain + a clear error.
6. **Idempotent.** Running any command twice produces the same result as running it once.
7. **No formatting reflow.** Emit via `ToFullString()`. Never `NormalizeWhitespace()` on a tree
   containing user code — it reformats the entire file and destroys the reviewable diff.
8. **The core engine knows nothing about onion/Wolverine/ErrorOr.** Architecture-specific knowledge
   lives behind `ITemplate` only. If `Program.cs`, `ConfigLoader`, or `PlanExecutor` mentions
   `Wolverine`, that's a layering violation.
9. **No telemetry. Ever.** This is a stated product promise.

---

## Architecture

```
Forge.Cli/                        # the dotnet tool
├── Program.cs                    # System.CommandLine wiring, flat colon-named commands
├── Cli/
│   ├── GroupedHelp.cs            # custom help + `forge list`, grouped by the `:` prefix
│   ├── GlobalOptions.cs          # --force --dry-run --json --verbosity --no-color
│   └── ExitCodes.cs              # the documented contract below
├── Config/                       # ForgeConfig (versioned), ConfigLoader, ConfigInferrer (forge init)
├── Templates/
│   ├── ITemplate.cs              # returns GenerationPlan; the ONLY architecture-aware seam
│   └── OnionWolverineErrorOr/    # + Snippets/*.cs.txt (embedded default stubs)
├── Planning/
│   ├── GenerationPlan.cs         # FileAction.Create | .Patch | .Skip
│   ├── PlanExecutor.cs           # the ONLY code that writes to disk
│   └── PlanRenderer.cs           # --dry-run diffs and --json output
├── Roslyn/
│   ├── SyntaxPatcher.cs          # NOT "SyntaxEditor" (collides with Roslyn's own)
│   ├── AnchorFinders/            # typed SyntaxNode queries, each with a fallback chain
│   └── TriviaPreserver.cs        # leading-whitespace cloning; never WithTriviaFrom
├── Diagnostics/                  # doctor checks
└── StubRepository.cs             # .forge/stubs/ override -> embedded fallback

Forge.Runtime/                    # companion package the USER's app references
├── ForgeRuntimeExtensions.cs     # AddForgeRuntime(); no-op unless --forge:* in argv
├── IForgeMessageFactory.cs       # user-implemented message factories
└── ISeeder.cs                    # user-implemented seeders
```

**Why `Forge.Runtime` exists:** `forge` cannot load the user's assemblies into its own process
(version/TFM/deps.json conflicts — DESIGN-REVIEW.md BLOCKER-1). Instead `invoke:*`, `db:seed`, and
runtime-mode `route:list` shell out to the user's app with a `--forge:<verb>` sentinel argument.

The hook is one line in their `Program.cs`, placed **before** `app.Run()` so the web server never
binds a port — running `db:seed` while the app is already running cannot collide:

```csharp
var app = builder.Build();
if (await app.RunForgeRuntimeAsync(args)) return;   // no-op on a normal start
app.Run();
```

The wire contract is **versioned JSON wrapped in `<<<FORGE-RESULT>>>` markers**, never shared
types. Markers because the payload shares stdout with the app's own logging; JSON-not-types because
a shared type would reintroduce exactly the version coupling the process boundary exists to avoid.
Version skew between `Forge.Cli` and `Forge.Runtime` is therefore a non-issue by construction.

`Forge.Runtime` is **opt-in** (`make:solution --with-runtime`) for the same reason the tool manifest
is: a `PackageReference` to a version that isn't restorable makes the generated solution fail to
restore, and `make:solution` must never emit a solution that cannot build.

Errors cross the boundary as data, never as a crash. Throw `ForgeRuntimeException` for expected,
actionable conditions — it travels as a bare message; anything else keeps its stack trace, because
for a real bug the trace is the useful part.

`route:list` has two modes. **Metadata mode** (no companion package required) uses
`MetadataLoadContext` — reflection-only, never executes user code. It sees attribute-routed
controllers but **not** minimal APIs or Wolverine.HTTP endpoints. **Runtime mode** (requires
`Forge.Runtime`) queries the real `EndpointDataSource` and sees everything.

## Distribution, isolation, and first run

### Two artifacts, two completely different install mechanisms

Conflating these is the most common source of confusion about how forge works.

| | `Forge.Cli` | `Forge.Runtime` |
|---|---|---|
| What | The `dotnet forge` tool | A small library the user's app references |
| Install | Tool manifest (`.config/dotnet-tools.json`) or global | `<PackageReference>` in their API project |
| Project dependency? | **No** | **Yes** |
| Compiled into their app? | Never | Yes |
| Needed for | Everything | Only `invoke:*`, `db:seed`, runtime-mode `route:list` |

A `dotnet tool` is **not** a NuGet dependency of the consuming project. It is a separate application
with its own `.deps.json`, running in its own process. It never enters the user's restore graph.
`dotnet tool install --local` writes a pinned executable reference to a manifest — it does not add a
`PackageReference`.

### Why `Forge.Cli` cannot cause dependency conflicts

`Forge.Cli` pulls in Roslyn. The user's app may reference a different Roslyn version. There is
**zero interaction** — separate process, separate dependency resolution.

**The constraint that keeps it that way: Roslyn is used purely as a parser, never as a compiler.**
forge reads `.cs` as text, builds a `SyntaxTree`, edits nodes, writes text. It must never build a
`Compilation` or request a `SemanticModel` — those require the user's full reference closure, and
the isolation collapses. **Staying syntactic-only is a hard architectural rule**, not a current
implementation detail. If a feature seems to need semantic information, that's a design discussion,
not something to quietly add.

### Dependency tiers — most of forge needs nothing

`Forge.Runtime` is **opt-in**. Never require it for scaffolding.

| Tier | Requires | Commands |
|---|---|---|
| 0 | Nothing but the SDK | `make:*`, `stub:*`, `config:*`, `doctor`, `init`, `runtime:install` — ~80% of the tool |
| 1 | `dotnet-ef` global tool | `db:migrate`, `db:migration`, `db:rollback`, `db:status` |
| 2 | `Forge.Runtime` PackageReference | `db:seed`, runtime-mode `route:list` |
| 2 | **+** `Forge.Runtime.Wolverine` | `invoke:list`, `invoke:run` |

Tier 0 is pure text-in/text-out and can never conflict with anything, so forge can be adopted on a
legacy solution with no csproj changes at all. `doctor` reports which tier is currently available
and **never hard-fails on a missing optional dependency** — it prints the command to fix it.

**Tier 2 is two packages, not one, and `doctor` must report them per capability.** A solution with
only the core package has a working `db:seed` and an unavailable `invoke:*`; reporting that as
"Tier 2 — all commands available" is how doctor came to contradict `invoke:list` seconds later.

**`forge runtime:install` is the only supported way to reach tier 2.** It adds both packages and
patches `Program.cs` — the usings, `AddForgeWolverine()`, the `RunForgeRuntimeAsync` hook, and the
singleton identity override that a scoped `ICurrentUser` would otherwise defeat under Wolverine's
per-message scope. `make:solution --with-runtime` covers only creation time, and the need is
almost always discovered later. Never tell a user to run `dotnet add package` for these — the
package alone leaves the app silently producing no forge result.

### Why tier 2 needs a process boundary

To invoke a handler you need the user's types, their fully-built DI container, their Wolverine
config, and every transitive dependency at *their* versions. Loading their assemblies into the forge
process fails on TFM mismatch, diamond conflicts (`Microsoft.Extensions.*`, `System.Text.Json` —
first loaded wins), and absent transitive resolution (`Assembly.LoadFrom` doesn't consult their
`.deps.json`). An `AssemblyLoadContext` workaround doesn't save it: types crossing the boundary
exist twice, so the returned `ErrorOr<T>` can't even be cast.

So forge shells out, and the contract between the two processes is **versioned JSON, never shared
types**. Version skew between `Forge.Cli` and `Forge.Runtime` is therefore a non-issue by
construction. Cost is a build plus host startup (~2–5s) — the same trade `dotnet ef` makes. Offer
`--no-build` when the caller knows the output is fresh.

### Packaging requirements — release blockers

1. **`<RollForward>LatestMajor</RollForward>`** in `Forge.Cli.csproj`. Verified mandatory (above).
2. **Target `net8.0`** for `Forge.Cli` — with roll-forward, one build runs on every runtime ≥ 8.
   Targeting `net10.0` locks out every machine that hasn't upgraded.
3. **Multi-target `Forge.Runtime` as `net8.0;net9.0;net10.0`** with near-zero dependencies —
   `Microsoft.Extensions.Hosting.Abstractions` and in-box `System.Text.Json`, nothing more. Put
   Wolverine-specific code in a separate `Forge.Runtime.Wolverine` package. A runtime package that
   causes conflicts defeats its own purpose.
4. **`make:solution` must NOT write a tool manifest by default** — it is opt-in via `--pin-forge`.
   Pinning is good practice in principle, but a manifest takes precedence over a global install:
   from the moment it exists, `dotnet forge` resolves the LOCAL tool. If that exact version is not
   restorable from a configured feed, the very next forge command dies with *"Run dotnet tool
   restore"* — so writing it by default bricks forge in the solution it just created. Observed in
   testing; a stale NuGet cache entry masked it by silently resolving an older binary.
5. **`forge doctor --check`** returns nonzero for CI, so a broken environment fails the pipeline
   instead of producing half-scaffolded code.

### Fresh machine → working code

The only hard prerequisite is the **.NET SDK** (not the runtime — forge shells out to `dotnet new`,
`dotnet build`, `dotnet sln add`, `dotnet ef`).

```bash
# 1. SDK — the only real download (~200MB). Everything after is seconds.
winget install Microsoft.DotNet.SDK.10        # macOS: brew install --cask dotnet-sdk
                                              # Linux: apt-get install -y dotnet-sdk-10.0

# 2a. Greenfield — no install needed, dnx runs it one-shot
dnx Pitechy.Forge.Cli make:solution -n OfficeCommute

# 2b. Brownfield — infer config from an existing solution
cd existing-solution && dnx Pitechy.Forge.Cli init

# 3. Pin for the team (make:solution should do this automatically)
dotnet tool install --local Pitechy.Forge.Cli
git add .config/dotnet-tools.json

# 4. Verify before generating anything
dotnet forge config:validate && dotnet forge doctor

# 5. Optional — only for tier 1/2
dotnet tool install --global dotnet-ef
dotnet forge runtime:install          # packages + Program.cs wiring, in one step

# 6. Use it
dotnet forge make:entity -n Gig --properties "Name:string,BudgetMin:int"
dotnet forge make:repo -i Gig --dry-run
dotnet forge make:repo -i Gig
dotnet build
```

Teammates cloning the repo afterward run `dotnet tool restore` and get **the exact forge version
that generated the existing code**. This matters more than it sounds: an unpinned generator means
two developers produce different output from the same command, and nobody can explain the diff.

Target: **under 10 minutes end to end**, dominated entirely by the SDK download. Protect that.

`dnx` solves the bootstrap chicken-and-egg — `forge init` needs forge before the repo has a
manifest. It is .NET 10+ only; on .NET 8/9 the fallback is `dotnet tool install --global`.

## Exit codes — a compatibility contract

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Unhandled error |
| 2 | Usage / parse error |
| 3 | Config missing or invalid |
| 4 | Target exists, refused (no `--force`) — benign; CI should not fail on this |
| 5 | Anchor not found, patch aborted |
| 6 | Refused by environment guard (`db:fresh` / `invoke:run` against Production) |

Changing one of these is a breaking change. Add new codes rather than repurposing existing ones.

---

## Conventions

- **Async all the way.** `Task<GenerationPlan>`, `CancellationToken` on anything that does I/O.
- **Nullable enabled**, warnings as errors in `Forge.Cli.csproj`.
- **No `Console.WriteLine` outside the `Cli/` layer.** Generators return plans; rendering is the
  CLI layer's job. This keeps generators testable without capturing stdout.
- **Respect `NO_COLOR`** and `--no-color`. Never emit ANSI when stdout is redirected.
- **Every user-facing error names the file and what to do about it.** "Anchor not found" is useless;
  "Could not find an insertion point in `src/App.Infrastructure/.../UnitOfWork.cs` — expected a
  constructor or an `I*Repository` property. Run `forge doctor` to check config alignment." is not.
- **Generated code must match the target project's style** — file-scoped vs block-scoped namespaces,
  nullable, `ImplicitUsings`. Detect into `CodeStyle` at startup, thread through `TemplateContext`.

## Commands

```bash
dotnet build                                     # build (warnings are errors)
dotnet test --filter "Category!=Compile"         # fast suite: plans + Roslyn. ~1s. Run constantly.
dotnet test --filter "Category=Roslyn"           # the adversarial corpus — run on any Roslyn change
dotnet test --filter "Category=Compile"          # THE GATE: scaffold to temp + real dotnet build. ~70s.
dotnet pack ./src/Forge.Cli -c Release -o ./nupkg
```

**Run the compile gate before calling any generator change done.** Nearly every genuine defect in
this codebase was caught there and was invisible to plan-level tests: a NuGet version conflict
(EF Core 9 vs Wolverine), a missing `using` on a patched file, a missing package reference for
`ToTable`, an ambiguous method-group conversion, and a namespace/type collision. A `GenerationPlan`
assertion cannot see any of those — only the compiler can.

The gate asserts **zero warnings as well as zero errors**: warnings are how a code-style mismatch
surfaces (CS8618 on a non-nullable property, an unused using, an async method with no await).

To manually verify the packaged tool end to end, see `/forge-verify`.

Local install for manual testing:

```bash
dotnet new tool-manifest
dotnet tool install --local Pitechy.Forge.Cli --add-source ./nupkg
dotnet forge list
```

## Skills

Load the matching skill before starting one of these tasks — each encodes rules that are easy to get
subtly wrong:

| Task | Skill |
|---|---|
| Adding or changing a `make:*` / `db:*` generator | `forge-generator` |
| Any code that parses or edits C# with Roslyn | `roslyn-patching` |
| Adding a command verb to the CLI surface | `forge-cli-surface` |
| Writing or editing `.cs.txt` stub templates | `forge-stubs` |
| Writing tests for generators or patchers | `forge-testing` |

## Guardrails for agents

- **Do not invent design decisions.** If this file and DESIGN-REVIEW.md don't cover it, ask or flag
  it — don't silently pick and bury the choice in an implementation.
- **A generator without tests is not done.** Minimum: one plan snapshot test, plus adversarial
  Roslyn cases for anything that patches existing files.
- **Verify Roslyn behavior; don't reason about it.** Trivia and syntax-node behavior are full of
  surprises. Write a scratch test and run it rather than predicting what `WithTriviaFrom` does.
- **Never commit or push unless asked.**
- When touching `route:list`, `invoke:*`, or `db:seed`, re-read the `Forge.Runtime` section above
  first — the in-process approach is the intuitive one and it is wrong.
