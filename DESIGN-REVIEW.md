# Design Review — `forge` CLI

Review of [forge-cli-design-roslyn.md](forge-cli-design-roslyn.md), against the bar the doc sets
for itself: **production ready, Artisan-parity or better**.

Overall: the design is unusually strong. The three principles in §2 are the right ones, the Roslyn
decision in §6.3 is correct and well-argued, and `stub:publish` (§8) is genuinely the highest-leverage
idea in the document. What follows is not a rewrite — it's the set of things that will bite during
implementation, ordered by how expensive they are to fix late.

Findings are tagged **[BLOCKER]** (design is not implementable as written), **[GAP]** (missing for
"production ready"), **[FIX]** (concrete error), and **[OK]** (verified correct — no action).

---

## 0. What I verified empirically

Before the review proper, I built throwaway probes on this machine (SDK `10.0.301`) to settle
claims the design leans on. Results:

| Claim | Verdict | Evidence |
|---|---|---|
| `ToolCommandName=dotnet-forge` → user types `dotnet forge ...` for a **local** tool | **CONFIRMED** | Packed a probe tool, installed via manifest. All three of `dotnet forgeprobe`, `dotnet dotnet-forgeprobe`, `dotnet tool run dotnet-forgeprobe` executed and passed `make:repo -i Gig` through as literal args. §1.2's core packaging bet is sound. |
| Colon-named commands (`make:repo`) parse correctly | **CONFIRMED** | `System.CommandLine` accepts `new Command("make:repo", ...)` and dispatches it. The colon is not a reserved token. |
| System.CommandLine is production-viable | **CONFIRMED, with a caveat** | **2.0.10 is stable/GA** (shipped alongside .NET 10, Nov 2025). No longer a beta gamble. **But the GA API differs sharply from the beta APIs** that dominate tutorials and model training data — see [FIX-6]. |
| §10: "shell completion ships almost for free" | **OVERSTATED** | See [FIX-7]. |
| §3: colon commands can be "grouped under namespace parents" | **CONTRADICTORY** | See [FIX-5]. |

---

## 1. [BLOCKER-1] — `invoke:*`, `db:seed`, and `route:list` cannot work as described

This is the most important finding in the review, and it affects three of the six command
namespaces.

**The problem.** §6.4 says `invoke:run` "boots the real host (same `AddWolverine()` config, same
handler assemblies)". §3.2 says `db:seed` will "discover and run all registered seeders via
reflection". Both descriptions imply `forge` loads the *user's* application assemblies into the
`forge` process and executes them.

That process is a `dotnet tool`. It has already loaded its own dependency graph — including
`Microsoft.CodeAnalysis.CSharp`, which is large and version-sensitive. Loading the user's app into
it means:

- **TFM mismatch.** `forge` ships as one TFM. The user's app may target `net8.0` while `forge`
  targets `net10.0`. A `net10.0` host cannot load a `net8.0` app's full dependency closure reliably.
- **Diamond dependency conflicts.** The user's app references `Microsoft.Extensions.DependencyInjection`,
  `System.Text.Json`, `Newtonsoft.Json` at *their* versions. `forge` references them at *its* versions.
  Whichever loads first wins, and the loser gets `FileLoadException` or silent behavioral drift.
- **No `deps.json`.** The user's app resolves its transitive dependencies through its own
  `.deps.json` and `.runtimeconfig.json`. Loaded via `Assembly.LoadFrom` into a foreign process,
  none of that resolution happens — you get `FileNotFoundException` on the first transitive
  dependency touched.

This is precisely why `dotnet ef` does **not** do this. EF Core's design-time tooling builds the
target project, then executes *inside the application's own process and dependency context*, with
the `Microsoft.EntityFrameworkCore.Design` package referenced by the app supplying the entry point.

**The fix — one companion package solves all three commands.**

Ship a second NuGet package, `Forge.Runtime`, which the user references from their API project:

```xml
<PackageReference Include="Pitechy.Forge.Runtime" Version="1.0.0" />
```

It contributes a single hosted entry point that activates only when a sentinel argument is present:

```csharp
// In the user's Program.cs — one line, added by make:solution and by forge init
builder.AddForgeRuntime();   // no-op unless argv contains --forge:<verb>
```

`forge` then **shells out** rather than loading:

```
forge invoke:run CreateGigCommand --factory CreateGigFactory
  └─> dotnet run --project src/App.API -- --forge:invoke CreateGigCommand --forge:payload-file <tmp>
        └─> AddForgeRuntime() intercepts, resolves IMessageBus from the REAL container,
            calls InvokeAsync, serializes ErrorOr<T> to stdout as JSON, exits.
  └─> forge parses that JSON and renders it green/red.
```

This is strictly better on every axis: no version conflicts, no TFM coupling, the middleware
pipeline is genuinely the real one (which was the whole point of §6.4), and `forge` itself stays a
pure code-generation tool with no runtime knowledge of Wolverine.

**Command-by-command consequence:**

| Command | Correct mechanism |
|---|---|
| `invoke:*` | Out-of-process via `Forge.Runtime`. `IForgeMessageFactory<T>` also lives in `Forge.Runtime`, which resolves the "where does this interface come from" question §6.4 leaves open. |
| `db:seed` | Same. Seeders need a real `DbContext` from the real container — this was never doable in-process. `ISeeder` also belongs in `Forge.Runtime`. |
| `route:list` | Two tiers. **Tier 1 (no package required):** `System.Reflection.MetadataLoadContext` over the built assembly — reflection-only, never executes user code, immune to all the conflicts above. Handles attribute-routed controllers, which is what §3.3 describes. **Tier 2 (with `Forge.Runtime`):** query the real `EndpointDataSource`, which is the *only* way to see minimal-API `app.MapGet(...)` and Wolverine.HTTP endpoints — these are invisible to metadata reflection because they're registered by executing code. |

**Note the scope change:** `route:list` as specified in §3.3 silently cannot see minimal APIs or
Wolverine.HTTP endpoints. If the template is expected to grow those, say so in the doc and plan for
Tier 2 from the start.

---

## 2. [BLOCKER-2] — `ITemplate` returning `void` makes `--dry-run`, `--json`, and atomicity unimplementable

§9's contract:

```csharp
void ScaffoldRepository(ForgeConfig config, string entity);
```

A method that returns `void` and writes files as a side effect cannot honestly support any of the
three cross-cutting flags in §3.6:

- `--dry-run` has nothing to print — it would need a parallel "what would I do" code path, which
  will drift from the real one and lie to the user. §7 promises the preview is "byte-for-byte what
  would actually be written"; that promise is unkeepable with this signature.
- `--json` ("assert N files created") has no file list to serialize.
- §7's "no partially patched file left behind" guarantee is per-file. But `make:feature --with-repo
  --with-controller` writes ~8 files across 3 projects. If file 6 fails, files 1–5 are already on
  disk. The design has no story for that.

**The fix — separate planning from execution.** This is a small refactor now and a large one later.

```csharp
public interface ITemplate
{
    string Name { get; }
    GenerationPlan PlanRepository(TemplateContext ctx, string entity);
    GenerationPlan PlanFeature(TemplateContext ctx, FeatureSpec spec);
    // ...
}

// A plan is data. It does not touch disk.
public sealed record GenerationPlan(IReadOnlyList<FileAction> Actions)
{
    public static GenerationPlan Empty { get; } = new([]);
    public GenerationPlan Concat(GenerationPlan other) => new([.. Actions, .. other.Actions]);
}

public abstract record FileAction(string Path)
{
    // full new-file content
    public sealed record Create(string Path, string Content)          : FileAction(Path);
    // Roslyn patch: original + patched text, both already computed
    public sealed record Patch (string Path, string Before, string After) : FileAction(Path);
    // generator decided this was already done
    public sealed record Skip  (string Path, string Reason)           : FileAction(Path);
}
```

Everything then falls out of one executor:

- `--dry-run` → render the plan as a unified diff. Guaranteed accurate, because it *is* the content.
- `--json` → serialize the plan.
- Normal run → `PlanExecutor.Apply(plan)`: write every file to `*.forge-tmp`, `File.Move` them all
  at the end, delete temps on any exception. Command-level atomicity, for free.
- Tests → assert on plans without touching a filesystem. This makes the golden-file tests in §12
  cheap instead of expensive.
- Composition → `make:feature --with-repo` becomes `PlanFeature(...).Concat(PlanRepository(...))`,
  with conflict detection (two actions targeting one path) before anything is written.

Also make these `async` (`Task<GenerationPlan>`) — Roslyn parsing and file I/O are both async, and
retrofitting async through a template contract later is painful.

**Also:** passing `ForgeConfig` to every method is a smell. Fold it into a `TemplateContext` that
carries config, the resolved solution root, the `StubRepository`, and the detected code style
(see [GAP-5]).

---

## 3. [FIX] — Concrete errors in the design

### [FIX-1] The §6.3 anchor query cannot match its own example — the code as written throws

```csharp
var anchor = iface.Members
    .OfType<PropertyDeclarationSyntax>()          // ← properties only
    .First(p => p.Identifier.Text == "SaveChangesAsync" ||
                p.Type.ToString().Contains("Task"));
```

In every `IUnitOfWork` this design describes, `SaveChangesAsync` is a **method**:

```csharp
Task<int> SaveChangesAsync(CancellationToken ct);   // MethodDeclarationSyntax
```

`.OfType<PropertyDeclarationSyntax>()` filters it out before the predicate ever runs. The predicate
then finds nothing, and `.First()` throws `InvalidOperationException` — on the primary code path of
the tool's flagship command, `make:repo`.

The second clause (`p.Type.ToString().Contains("Task")`) is also wrong in intent: it would match any
property returning a `Task`, which is not what "anchor before SaveChangesAsync" means.

**Fix:** query `MemberDeclarationSyntax`, match by identifier across member kinds, and never use
`.First()` on user code:

```csharp
static MemberDeclarationSyntax? FindAnchor(InterfaceDeclarationSyntax iface) =>
    // 1. preferred: the SaveChangesAsync member, whatever kind it is
    iface.Members.FirstOrDefault(m => m.NameOf() == "SaveChangesAsync")
    // 2. fallback: after the last I*Repository property
    ?? iface.Members.OfType<PropertyDeclarationSyntax>()
           .LastOrDefault(p => IsRepositoryType(p.Type))
    // 3. fallback: last member — insert after it
    ?? iface.Members.LastOrDefault();
    // 4. null => empty interface; caller inserts as first member
```

§12 already lists this fallback chain as a roadmap item. Given `.First()` throws on the main path,
it belongs in v1, not the roadmap.

### [FIX-2] `SyntaxEditor` collides with a real Roslyn type

§4 names a class `Roslyn/SyntaxEditor.cs`. `Microsoft.CodeAnalysis.Editing.SyntaxEditor` is a real,
widely-used type shipped in `Microsoft.CodeAnalysis.Workspaces` — a package you will almost
certainly end up referencing. Every file that uses both will need an alias, and every reader will be
briefly confused.

Rename to **`SyntaxPatcher`**. Also decide explicitly whether you're taking the `Workspaces`
dependency: if yes, Roslyn's own `SyntaxEditor` and `SyntaxGenerator` do a lot of §4's work for you.
If no, say so — it's a defensible choice that keeps the tool's package footprint smaller.

### [FIX-3] `WithTriviaFrom` on an insert-before anchor produces a subtle formatting bug

```csharp
SyntaxFactory.ParseMemberDeclaration("IGigRepository Gig { get; }")!.WithTriviaFrom(anchor);
```

`WithTriviaFrom` copies **both** leading and trailing trivia. If the anchor member carries a doc
comment or a `#region` in its *leading* trivia, that trivia is now duplicated onto the inserted
node — you get a second copy of the XML doc comment, or an orphaned `#region` directive that breaks
compilation.

For insert-before, take only the anchor's **leading whitespace**, not its full leading trivia:

```csharp
var indent = anchor.GetLeadingTrivia()
    .Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia))
    .LastOrDefault();

var newMember = SyntaxFactory.ParseMemberDeclaration(text)!
    .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed, indent)
    .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
```

This is exactly the kind of case `TriviaPreservingRewriter` (§4) exists for — the design names the
component correctly but the sample code bypasses it. Make the sample match the design.

### [FIX-4] §5's config has no version field

`forge.config.json` will change shape. Without a version, a `forge` upgrade meeting an old config
either crashes or silently misreads it. Add as the first key:

```json
{
  "$schema": "https://raw.githubusercontent.com/Pitechy/forge/main/schema/forge.config.v1.json",
  "version": 1,
  "template": "onion-wolverine-erroror",
  ...
}
```

`version` lets the loader migrate or refuse. `$schema` gives free IntelliSense and validation in
VS Code and Rider — high value for near-zero cost, and it makes `config:validate` a second line of
defense rather than the only one.

### [FIX-5] §3 contradicts itself on command grouping

§3 says commands are "grouped under namespace parents for `list`", and §4's `Program.cs` comment
repeats it. But `make:repo` registered as a colon-named command is **flat** — I confirmed this: the
help output lists `make:repo` at the root level with no grouping, because `make` is not a parent
command, it's the first half of a string.

You cannot have both automatic grouping and `make:repo` syntax from the same registration. Pick:

- **Recommended:** register flat colon-named commands (confirmed working), and write a custom help
  renderer that groups by the substring before `:`. `dotnet forge list` (§3) already implies you're
  writing custom output; extend it to `--help` so both surfaces agree. ~40 lines.
- Alternative: real nested commands (`forge make repo`) with colon-named hidden aliases. Gets free
  grouping, but now there are two spellings of every command in the wild.

Either way, correct §3 and the §4 comment — as written they describe something that cannot exist.

### [FIX-6] The System.CommandLine GA API is not the API in most examples

2.0.0 GA (stable since Nov 2025, currently `2.0.10`) renamed and reshaped the core API relative to
the betas. Since beta-era code is what most tutorials — and most LLM-generated code — will produce,
pin the correct shape in the repo's guidance. Verified working on `2.0.10`:

```csharp
using System.CommandLine;

var nameOpt  = new Option<string>("--name", "-n") { Description = "Entity name" };
var forceOpt = new Option<bool>("--force")        { Description = "Overwrite existing files" };

var cmd = new Command("make:repo", "Scaffold a repository pair");
cmd.Options.Add(nameOpt);
cmd.Options.Add(forceOpt);
cmd.SetAction(pr => Handle(pr.GetValue(nameOpt), pr.GetValue(forceOpt)));  // returns int

var root = new RootCommand("forge");
root.Subcommands.Add(cmd);
return root.Parse(args).Invoke();
```

Notably **not**: `SetHandler`, `AddOption`, `AddCommand`, `new Option<T>("--name", description:)`,
`InvokeAsync(args)` on the command. Those are all beta spellings.

### [FIX-7] Shell completion is not "almost for free"

§10 claims System.CommandLine "ships this almost for free". Verified: it does **not**. GA gives you
the `[suggest]` **directive** — `forge [suggest]` prints completion candidates, which I confirmed
works — but there is no `completions bash|zsh|pwsh` command that emits a shell script. That was the
separately-installed `dotnet-suggest` global tool, which requires every user to install a *second*
tool and register a shell hook.

Realistically this is a half-day: write three small scripts (bash/zsh/pwsh, ~30 lines each) that
shell out to `forge [suggest]`, and a `forge completion <shell>` command that prints the right one.
Worth doing — just budget for it honestly rather than treating it as a freebie.

### [FIX-8] Verify the tool manifest path claim in §11

§11 documents the standard `.config/dotnet-tools.json`. On this machine (SDK 10.0.301),
`dotnet new tool-manifest --force` followed by `dotnet tool install --local` produced the manifest
at the **repo root** (`./dotnet-tools.json`), not under `.config/`. This may be an artifact of
`--force`, or a .NET 10 SDK behavior change. Low severity, but §11's instructions are what users
will copy-paste — confirm the path on a clean directory before publishing docs.

---

## 4. [GAP] — Missing for "production ready"

### [GAP-1] There is no way to adopt `forge` into an existing solution

`make:solution` creates a new solution and writes `forge.config.json`. Every other command requires
that config to exist. Nothing generates it for a solution that already exists — which is the
majority of real-world adoption, and includes both of the production codebases the template is
modeled on (`OfficeCommute`, `MusixGig`).

**Add `forge init`:** walk the solution, infer `domainProject`/`applicationProject`/etc. from csproj
names and folder conventions, detect `roleGuardStyle` by looking for `IRequireExplicitRoles` vs
`IRequiresExplicitRoles` in the source, detect `scheduler` from PackageReferences, then write the
config and print what it inferred with confidence markers for the user to confirm.

This is arguably the single most important missing command — without it, `forge` is a greenfield-only
tool, and greenfield is the rarer case.

### [GAP-2] No migration *creation* command

§3.2 has `db:migrate` (wrapping `dotnet ef database update`) but nothing wrapping
`dotnet ef migrations add`. That's the command developers run far more often, and it's the one with
the annoying `--project` / `--startup-project` flags that `db:migrate` exists to hide. Add:

| Command | Purpose |
|---|---|
| `db:migration <Name>` | `dotnet ef migrations add` with paths resolved from config |
| `db:rollback [--to <Migration>]` | `dotnet ef database update <target>` |
| `db:status` | List applied vs. pending migrations |

### [GAP-3] Exit codes are undefined

A CLI that §10 explicitly wants scriptable in CI must document its exit codes as contract. Nothing
in the design mentions them. Propose, and treat as a compatibility surface:

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Unhandled error |
| 2 | Usage / parse error |
| 3 | Config missing or invalid |
| 4 | Target exists, refused (no `--force`) — *distinct, so CI can treat it as benign* |
| 5 | Anchor not found; patch aborted |
| 6 | Refused by environment guard (e.g. `db:fresh` in Production) |

Code 4 matters: "already exists" is the expected outcome of a re-run, and a pipeline needs to
distinguish it from a real failure without parsing stdout.

### [GAP-4] `--force` is too blunt for the design's own stated values

§2's principle 2 says "hand-written business logic is sacred", but `--force` is a single global
switch that overwrites everything, including handler bodies a developer spent a day on.

**Add a generated-file manifest.** Record every generated file in `.forge/manifest.json` with its
path and a content hash at generation time. Then `--force` can distinguish two very different cases:

- Hash matches → the file is untouched boilerplate. Overwrite silently; this is safe and is the
  common case after a template upgrade.
- Hash differs → a human edited it. Refuse even under `--force`; require `--force --overwrite-modified`,
  and print the diff first.

This turns `--force` from "hope you committed first" into something you can run confidently, and it
makes template upgrades (regenerate everything untouched, flag everything customized) tractable.

### [GAP-5] Generated code will not match the target project's style

Generated C# must agree with the project it lands in on at least: file-scoped vs block-scoped
namespaces, nullable reference types on/off, `ImplicitUsings` (determines whether to emit `using
System;`), and `.editorconfig` indentation. If it doesn't, every generated file lands with warnings
or a formatting diff, and the "minimal, reviewable diff" promise of §7 dies on arrival.

Detect once at startup into a `CodeStyle` record — read the target `.csproj` for `<Nullable>` and
`<ImplicitUsings>`, and sample an existing source file in the target folder for namespace style —
then thread it through `TemplateContext`. Cheap to do up front, invasive to retrofit.

### [GAP-6] Testing is on the roadmap; for this tool it is the product

§12 lists golden-file tests as future work. For a tool whose entire value proposition is
"it edits your source files correctly and non-destructively", the test suite is not a nice-to-have —
it is the only thing standing between `forge` and a corrupted `UnitOfWork.cs` in someone's repo.

Move to v1, in three layers:

1. **Plan snapshot tests** (fast, hundreds of them). With [BLOCKER-2] fixed, assert on
   `GenerationPlan` objects via `Verify.Xunit`. No filesystem.
2. **Roslyn patcher adversarial corpus** (the highest-value tests in the project). For each patcher,
   a directory of hostile-but-legal input files: `#region`-wrapped members, primary-constructor
   `UnitOfWork`, file-scoped namespace, block-scoped namespace, XML doc comments on the anchor,
   empty interface, anchor absent entirely, member already present (idempotency), `partial` split
   across files, `#if` conditional compilation. Every one of these is a real shape that will appear
   in a real repo, and every one is a way `.First()` throws or trivia duplicates.
3. **Compile gate** (slow, a handful). Scaffold a full solution into a temp dir, run `dotnet build`,
   assert zero errors. This is the only test that catches "the generated code doesn't compile",
   which is the failure users will actually report.

### [GAP-7] `invoke:run`'s production guard is too weak

§6.4 says `doctor` "can optionally warn" if `ASPNETCORE_ENVIRONMENT=Production`. But §6.4 also
states plainly that `invoke:run` performs real DB writes and real external calls. An optional
warning from a *different command* is not a guard.

Make it a hard refusal in `invoke:run` itself (exit 6), overridable only by an explicit
`--i-know-this-is-production` flag. Compare `db:fresh`, which §3.2 already gets right by refusing
outright. Apply the same standard to the command that runs arbitrary handlers.

### [GAP-8] Standard CLI hygiene not mentioned

Small, expected, and cheap — but conspicuous by absence in a doc this thorough:
`--version`, `--verbosity q|m|d`, `--no-color` **plus** honoring the `NO_COLOR` environment
variable, and reading `FORGE_CONFIG` / `FORGE_TEMPLATE` env overrides for CI. Also: §3.6 scopes the
cross-cutting flags to "every `make:*` and `db:*` command" — they should be global, including on
`invoke:*` and `stub:*`.

---

## 5. [OK] — Verified sound, no change needed

Recorded so these don't get re-litigated later:

- **§1.2 packaging strategy.** Confirmed working end-to-end. Decoupling `PackageId` from
  `ToolCommandName` is the right call and the `dotnet-` prefix trick delivers `dotnet forge` exactly
  as described.
- **§6.3 Roslyn-over-regex.** Correct, and the justification is the right one. Syntax-node anchors
  genuinely survive `#region`s and reordering in a way text matching cannot.
- **§8 `stub:publish` + `stub:diff`.** The strongest idea in the document. `stub:diff` in particular
  is what keeps `stub:publish` from being a one-way door — most tools that copy this pattern from
  Artisan forget the second half. One addition: record the tool version that produced the published
  stubs in `.forge/stubs/.forge-version` so `stub:diff` can do a proper three-way diff (original
  default → your edit → new default) instead of a two-way one that can't tell your change from
  upstream's.
- **§6.4 `InvokeAsync` over `PublishAsync`/`SendAsync`.** Correct reasoning — it's the only one that
  returns the handler result, which this template needs since every handler returns `ErrorOr<T>`.
- **§6.4 factories-vs-fixtures distinction.** Well drawn, and the right two primitives.
- **§9 `ITemplate` as the seam.** Right abstraction in the right place (modulo the `void` return in
  [BLOCKER-2]). Keeping the core engine architecture-agnostic from day one is what makes §9.1
  possible later.
- **§2 principle 2 (append, never overwrite).** The correct default, and rare.
- **No telemetry (§12).** Good. Keep it that way; say so in the README, it's a selling point.

---

## 6. Recommended v1 scope change

The design's roadmap (§12) defers several things I'd pull forward, and includes some that can slip.
Suggested reshuffle:

**Pull into v1** (each is either a correctness issue or blocks adoption):
- `forge init` [GAP-1] — without it there is no adoption path for existing codebases
- `GenerationPlan` refactor [BLOCKER-2] — retrofitting this later touches every generator
- `Forge.Runtime` companion package [BLOCKER-1] — determines whether `invoke:*`/`db:seed` work at all
- Anchor fallback chain [FIX-1] — currently throws on the main path
- Roslyn adversarial test corpus [GAP-6.2] — the tool's core risk
- Exit code contract [GAP-3] — cheap now, a breaking change later

**Safe to defer** (as the doc already has them): third-party template packages (§9.1), the second
reference template, the REPL, `make:factory` handling nested/collection shapes.

**Suggested v1 cut line:** `make:solution`, `forge init`, `make:entity`, `make:repo`, `make:feature`,
`make:resource`, `stub:publish`, `stub:diff`, `config:*`, `doctor`, `db:migration`, `db:migrate`,
`db:seed`. That's a coherent, genuinely useful tool. `invoke:*` and `route:list` are excellent but
both depend on `Forge.Runtime` landing first — they make a strong v1.1 that ships a month later
rather than a v1 that ships late.

---

## 7. Summary table

| # | Severity | Item | Cost to fix now | Cost to fix late |
|---|---|---|---|---|
| BLOCKER-1 | Critical | `invoke`/`seed`/`route` need out-of-process runtime | Medium | Very high — rearchitects 3 namespaces |
| BLOCKER-2 | Critical | `ITemplate` must return a plan, not `void` | Low | Very high — touches every generator |
| FIX-1 | High | Anchor query throws on main path | Trivial | Bug reports on `make:repo` |
| FIX-3 | High | `WithTriviaFrom` duplicates doc comments | Trivial | Corrupted user files |
| GAP-1 | High | No `forge init` for existing solutions | Low | Blocks all brownfield adoption |
| GAP-6 | High | Tests are roadmap, not v1 | Medium | Corrupted files in the wild |
| FIX-4 | Medium | Config has no `version` field | Trivial | Breaking change |
| GAP-3 | Medium | Exit codes undefined | Trivial | Breaking change for CI users |
| GAP-4 | Medium | `--force` too blunt | Low | Data loss incidents |
| GAP-5 | Medium | Code style not detected | Low | Every generated file warns |
| GAP-7 | Medium | `invoke:run` prod guard too weak | Trivial | Production incident |
| FIX-2/5/6/7/8 | Low | Naming collision, doc contradictions, API drift | Trivial | Confusion |
| GAP-2/8 | Low | Missing migration cmds, CLI hygiene | Low | Papercuts |
