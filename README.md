# forge

A Laravel-Artisan-style architectural code generator for .NET, invoked as `dotnet forge <namespace>:<verb>`.

Scaffolds entities, repositories, Wolverine commands and queries, audience-shaped responses,
authorization wiring, and whole solution skeletons — so nobody hand-writes boilerplate that a
convention already dictates.

```bash
dotnet forge make:solution -n Shop
dotnet forge make:entity   -n Order --properties "Reference:string,Total:decimal"
dotnet forge make:repo     -i Order
dotnet forge make:feature  -n PlaceOrder --type command --roles User --properties "Reference:string"
dotnet build          # 0 warnings, 0 errors
```

That sequence writes 26 files and compiles clean. No hand-written code required.

Entities come out **encapsulated** — private setters, a constructor, and an `Update` method,
because a freely mutable entity contradicts the architecture the rest of the template builds:

```csharp
public class Order
{
    public Guid Id { get; private set; }

    public string Reference { get; private set; } = string.Empty;
    public decimal Total { get; private set; }

    private Order() { }                                  // EF Core materialisation

    public Order(string reference, decimal total) { ... }
    public void Update(string reference, decimal total) { ... }
}
```

`Update` changes everything at once because that is all a generator can honestly infer — split it
into intention-revealing methods (`Rename`, `Reprice`, `Cancel`) as the domain earns them. Prefer
the anemic shape? `--public-setters` for one entity, `"encapsulateEntities": false` for the
solution.

### Base classes

Point `entityBaseClass` at a class in your domain project and every generated entity derives from
it:

```jsonc
// forge.config.json
"entityBaseClass": "BaseEntity"
```

```csharp
public class Order : BaseEntity
{
    public string Reference { get; private set; } = string.Empty;
    ...
```

forge reads the base's *own* properties and omits them from what it generates, so a base carrying
`Id` and audit timestamps never produces CS0108 "hides inherited member". Adding a field to the
base is enough — no generator change, and existing entities keep compiling. Works with any base
you already have (`BaseEntity`, `AuditableEntity`, whatever it's called), not just one forge wrote.

Starting fresh? `make:solution --with-base-entity` generates one with `Id`, `CreatedAt`,
`UpdatedAt` and a `Touch()` helper, and sets the config key for you.

## Install

The only prerequisite is the .NET SDK.

```bash
# try it without installing (.NET 10+)
dnx Pitechy.Forge.Cli make:solution -n Shop

# or pin it per-repo, so everyone generates identical output
dotnet new tool-manifest
dotnet tool install --local Pitechy.Forge.Cli
```

## Three principles

**Never overwrite hand-written code.** A generator meeting an existing file skips it. `--force`
overwrites only files whose content still matches what forge generated — `.forge/manifest.json`
records a hash of every one. A file you have edited needs `--force --overwrite-modified`, and the
refusal surfaces as exit code 4 so CI notices.

**Plan, then apply.** Generators return a `GenerationPlan` and never touch disk; only the executor
writes, staging to temp files and moving them together. So `--dry-run` shows exactly what would be
written, a failure part-way through leaves nothing behind, and every command is all-or-nothing.

**Edits are syntax-node surgery, never text.** Wiring a repository into `IUnitOfWork` parses the
file with Roslyn, finds a stable anchor, and inserts a node. Regions, XML doc comments, reordered
members, primary constructors — an adversarial corpus covers the shapes real code takes, and a
patch that cannot be made safely refuses with a clear message instead of guessing.

## The three packages

| Package | What | How it's consumed |
|---|---|---|
| `Pitechy.Forge.Cli` | The `dotnet forge` tool | `dotnet tool install` — never a `PackageReference`, never in your app's restore graph |
| `Pitechy.Forge.Runtime` | Runs seeders inside your app | `PackageReference`, optional |
| `Pitechy.Forge.Runtime.Wolverine` | Adds `invoke:*` | `PackageReference`, only if you use Wolverine |

Most of forge needs none of them:

| Tier | Requires | Commands |
|---|---|---|
| 0 | Nothing but the SDK | `make:*`, `stub:*`, `config:*`, `doctor`, `init`, `runtime:install` — ~80% of the tool |
| 1 | `dotnet-ef` | `db:migrate`, `db:migration`, `db:rollback`, `db:status` |
| 2 | `Forge.Runtime` | `db:seed`, runtime-mode `route:list` |
| 2 | **+** `Forge.Runtime.Wolverine` | `invoke:list`, `invoke:run` |

Tier 0 is pure text-in, text-out and can never conflict with anything, so forge can be adopted on a
legacy solution with no csproj changes at all. `forge doctor` reports which tier is available and
prints the command to reach the next one.

Reaching tier 2 is one command, on a new solution or an existing one:

```bash
dotnet forge runtime:install --dry-run   # preview the exact diff
dotnet forge runtime:install
```

It adds the packages and wires `Program.cs` — the hook, the verb-handler registration, and the
identity lifetime `invoke:run --as-role` depends on. Adding the package by hand is not enough:
without the hook the app starts, ignores the forge argument, and reports nothing.

## Why tier 2 runs out of process

To run a seeder or invoke a handler you need your types, your fully-built container, your Wolverine
configuration, and every transitive dependency at *your* versions. Loading your assemblies into
forge's process would fail on TFM mismatch and diamond conflicts — forge carries Roslyn and its own
graph. So forge launches *your* app with a `--forge:<verb>` argument, `Forge.Runtime` intercepts it
inside the real container, and the answer comes back as versioned JSON. The same trade `dotnet ef`
makes, for the same reasons.

One line in `Program.cs`, placed before `app.Run()` so the web server never binds a port:

```csharp
var app = builder.Build();
if (await app.RunForgeRuntimeAsync(args)) return;   // no-op on a normal start
app.Run();
```

`Forge.Runtime` refuses to run when the hosting environment is Production, and takes an `enabled:`
parameter so you can disable it explicitly.

## Adopting an existing solution

```bash
cd existing-solution
dotnet forge init
```

`init` infers configuration from what is actually in the codebase — locating `IUnitOfWork` by its
declaration rather than guessing at folders, reading the role-guard style from which marker
interface exists, detecting the scheduler from package references. Every inference is printed with
its evidence, and anything it could not determine is flagged rather than silently defaulted.

## Customising the output

```bash
dotnet forge stub:publish
```

Copies the templates into `.forge/stubs/`. Edit them, commit them, and every subsequent generation
uses your shape — a licence header, a different repository base, your own `Result<T>` instead of
`ErrorOr<T>`. `stub:diff` performs a three-way comparison after a forge upgrade so customising is
not a one-way door.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Unhandled error |
| 2 | Usage / parse error |
| 3 | Config missing or invalid |
| 4 | Target exists, refused — benign; CI should not fail on this |
| 5 | Anchor not found, patch aborted |
| 6 | Refused by an environment guard |

## No telemetry

forge collects nothing, sends nothing, and phones home to nobody.

## Licence

MIT
