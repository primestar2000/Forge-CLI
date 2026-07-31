
# `forge` — ASP.NET Core Scaffolding CLI

**A Laravel-Artisan-style architectural code generator for .NET**, invoked as `dotnet forge ...`.

---

## 1. Naming & Packaging

### 1.1 The command

The command is **`forge`**, invoked as `dotnet forge <namespace>:<verb>`, e.g.:

```bash
dotnet forge make:repo -i Gig
dotnet forge db:seed
dotnet forge route:list
```

### 1.2 Why this is safe to use

A GitHub org called `dotnet-forge` exists (an abandoned, unrelated Rails-like generator), and
`Forge.*` is a common prefix on NuGet (`Forge.OpenAI`, `Forge.Base`, `Autodesk.Forge`,
`Forge.Application`, etc.) No exact match for a published package literally named `forge` or
`dotnet-forge` was found — but publishing under the bare name `Forge` would still be fighting an
already-crowded prefix, and a GitHub search for "dotnet-forge" would land people on the dead repo
instead of yours.

The fix costs nothing: **the NuGet package ID and the invoked command are independent strings.**
The .NET tooling convention (confirmed against Microsoft's own docs) is:

> If the command is prefixed by `dotnet-`, you can include or omit the prefix when you invoke the
> tool — `dotnet dotnet-doc`, `dotnet doc`, and `dotnet tool run dotnet-doc` are equivalent.

So:

| Element | Value |
|---|---|
| **PackageId** (what you search/install by) | `{YourOrg}.Forge.Cli` — distinctive, no collision |
| **ToolCommandName** (what `dotnet` routes) | `dotnet-forge` |
| **What the user types** | `dotnet forge make:repo -i Gig` |

```xml
<PropertyGroup>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>dotnet-forge</ToolCommandName>
  <PackageId>YourOrg.Forge.Cli</PackageId>
  <PackageOutputPath>./nupkg</PackageOutputPath>
</PropertyGroup>
```

This gets you the short, memorable `forge` verb at the terminal without gambling on a NuGet name
clash later. Before your first public release, run `dotnet tool search forge` (or check
nuget.org directly) once more to reconfirm — package availability can change, and it's a
thirty-second check that removes all doubt.

---

## 2. Purpose & Philosophy

`forge` scaffolds recurring architectural artifacts — entities, repositories, Wolverine
commands/queries, authorization wiring, background jobs, API resources, and entire solution
skeletons — so a developer never hand-writes boilerplate that a convention already dictates.

Three principles drive every design decision below:

1. **One memorable entry point, colon-namespaced underneath.** Not `dotnet forge create-repo`,
   but `dotnet forge make:repo` — the same trick that makes `artisan` commands legible at a
   glance (`make:*` scaffolds, `db:*` touches data, `route:*` inspects HTTP surface, `config:*`
   inspects the tool's own state).
2. **Generators append, they never silently overwrite.** Re-running a generator against something
   that already exists is a no-op with a warning, unless `--force` is passed. Hand-written
   business logic is sacred.
3. **The core engine knows nothing about any specific architecture.** `onion-wolverine-erroror`
   is the first template, not a special case — a second template (Vertical Slice + MediatR, or
   Minimal APIs + FastEndpoints) is a peer implementation of the same `ITemplate` contract, not a
   fork.

It ships with one **built-in template**, `onion-wolverine-erroror`, modeled on two production
patterns (referred to internally as `OfficeCommute` and `MusixGig`):

- **Onion / Clean Architecture** project layout (Domain → Application → Infrastructure → API)
- **Wolverine** as the in-process message bus, with **secure-by-default** role-check middleware
- **ErrorOr\<T\>** for expected-failure handling instead of exceptions
- **Unit of Work** as a named-property facade over per-aggregate repositories

---

## 3. Command Reference

Commands are grouped by namespace prefix. `dotnet forge list` reflects over every registered
command and prints this table grouped by prefix, with descriptions — the tool documents itself.

### 3.1 `make:*` — scaffolding

| Command | Purpose |
|---|---|
| `make:solution -n <Name> [--template <id>]` | One-time: scaffold all projects, wire references, drop in ideology files |
| `make:entity -n <Name> --properties "Prop:type,..."` | Scaffold a Domain entity + EF Core `IEntityTypeConfiguration<T>` |
| `make:repo -i <Entity>` | Scaffold `I{Entity}Repository` + `{Entity}Repository`, wire into `IUnitOfWork` |
| `make:feature -n <Name> --type command\|query [--roles Role1 Role2] [--anonymous] [--with-repo] [--with-controller]` | Scaffold a command/query record, handler, validator — optionally chained with a repo and/or controller action |
| `make:seeder -n <Name>` | Scaffold a class implementing `ISeeder` |
| `make:policy -n <Resource>` | Scaffold an `IAuthorizationHandler` / `IAuthorizationRequirement` pair for fine-grained, per-resource checks (e.g. "can this user edit *this* gig"), complementing the route-level `IRequireExplicitRoles` guard |
| `make:event -n <Name>` | Scaffold a domain event record + a Wolverine handler stub |
| `make:listener -n <Name> --for <Event>` | Scaffold an additional handler for an existing event (one event, many listeners) |
| `make:job -n <Name>` | Scaffold a background job class, wired to the configured scheduler (Wolverine scheduled messages, Hangfire, or Quartz) |
| `make:resource -n <Entity>` | Scaffold a `{Entity}Result` DTO + a Mapster `IRegister` mapping config |
| `make:middleware -n <Name>` | Scaffold a Wolverine `Before`/`After`/`Finally` middleware class stub |
| `make:dto -n <Name> --properties "..."` | Scaffold a bare request/response DTO with no entity backing (for cases `make:resource` doesn't fit) |
| `make:test -n <Name> --for <Feature>` | Scaffold an xUnit test class + fixture pre-wired to the target feature's handler |
| `make:controller -n <Entity>` | Generate a thin `ApiController`-derived class with one action per existing feature found under that entity's folder |
| `make:factory -n <MessageType> [--state <name>]` | Scaffold an `IForgeMessageFactory<T>` for an existing command/query/event, with stub property values already typed to the message's shape — used by `invoke:run` to build test payloads (§6.4) |

### 3.2 `db:*` — data lifecycle

| Command | Purpose |
|---|---|
| `db:seed [--only <Seeder>]` | Discover and run all registered seeders via reflection |
| `db:fresh` | Drop, recreate, migrate, and reseed the database — local/dev only, refuses to run if `ASPNETCORE_ENVIRONMENT=Production` |
| `db:migrate` | Thin wrapper around `dotnet ef database update`, resolved from the config's infrastructure project so you don't have to remember `--project`/`--startup-project` flags |

### 3.3 `route:*` — API introspection

| Command | Purpose |
|---|---|
| `route:list [--role <Role>]` | Reflect over controllers/attributes (`[HttpGet]`, `[HttpPost]`, etc.) and print method + path + controller + required roles, no server run needed |

### 3.4 `stub:*` — template customization

| Command | Purpose |
|---|---|
| `stub:publish [--only <name>]` | Copy the embedded `.cs.txt` snippets into `.forge/stubs/` in the solution so a team can edit the generated shape without forking the tool |
| `stub:diff` | Show which published stubs have drifted from the tool's built-in defaults (useful after a `forge` upgrade, to see what changed upstream) |

### 3.5 `config:*` — tool self-diagnostics

| Command | Purpose |
|---|---|
| `config:show` | Print the resolved `forge.config.json` after merging defaults, with the solution root it detected |
| `config:validate` | Check every path in the config actually exists and every namespace resolves — catches a stale config before a generator fails halfway through a write |
| `doctor` | Broader health check: confirms `dotnet-ef` is installed if `db:*` is used, confirms the configured `roleGuardStyle` matches what's actually in `IUnitOfWork.cs`, flags stubs that reference types no longer in the solution |

### 3.6 Cross-cutting flags (every `make:*` and `db:*` command)

| Flag | Effect |
|---|---|
| `--force` | Overwrite instead of refusing when the target already exists |
| `--dry-run` | Print the diff / file list without writing anything |
| `--json` | Emit machine-readable output instead of the human summary — for CI pipelines that want to assert "N files created" |

### 3.7 `invoke:*` — handler testing (Wolverine-specific)

Boots the real host (same `AddWolverine()` config, same handler assemblies) and drives a
message straight through Wolverine's runtime — no HTTP, no transport — so a handler can be
exercised in isolation the way `php artisan tinker` lets you call a class directly. Full detail
in §6.4.

| Command | Purpose |
|---|---|
| `invoke:list` | Reflect over `IWolverineRuntime.Handlers.Chains` and print every message type with a registered handler, grouped by namespace |
| `invoke:run <MessageType> [--payload <json>] [--factory <name>] [--state <name>] [--json]` | Build a payload (see resolution order in §6.4) and run it through `IMessageBus.InvokeAsync`, pretty-printing the returned `ErrorOr<T>` — errors in red, success in green |
| `invoke:fixture:save <MessageType> <name>` | Persist whatever payload was just built (from a factory, a prompt, or `--payload`) as a named JSON fixture under `.forge/fixtures/{MessageType}/{name}.json` |
| `invoke:fixture:list [<MessageType>]` | List saved fixtures, optionally filtered to one message type |

### Example session

```bash
dotnet forge make:solution -n OfficeCommute
dotnet forge make:entity -n Gig --properties "Name:string,BudgetMin:int,BudgetMax:int"
dotnet forge make:repo -i Gig
dotnet forge make:feature -n CreateGig --type command --roles User
dotnet forge make:policy -n Gig
dotnet forge db:seed
```

produces:

```
✔ Created Gig entity in src/OfficeCommute.Domain/Entities
✔ Created IGigRepository + GigRepository
✔ Wired Gig into IUnitOfWork / UnitOfWork
✔ Created Errors.Gig.cs stub
✔ Created CreateGigCommand + CreateGigCommandHandler + CreateGigCommandValidator
✔ Created GigAuthorizationHandler + GigAuthorizationRequirement
✔ Ran 3 seeders (UserSeeder, RoleSeeder, GigSeeder)
```

Every command is a thin wrapper: parse args → load `forge.config.json` → find the solution root
→ resolve the configured template → run the relevant generator → write files → print a short
success/diff summary.

---

## 4. Tool Architecture

```
Forge.Cli/
├── Program.cs                     # System.CommandLine wiring — one Command per verb,
│                                   # grouped under namespace parents for `list`
├── Config/
│   ├── ForgeConfig.cs              # deserialized forge.config.json
│   └── ConfigLoader.cs             # finds .sln upward, loads config, validates paths
├── Diagnostics/
│   ├── DoctorCheck.cs               # contract every health check implements
│   └── Checks/                      # EfToolInstalledCheck, RoleGuardShapeCheck, StubDriftCheck...
├── Templates/
│   ├── ITemplate.cs                 # contract every architecture template implements (§7)
│   ├── OnionWolverineErrorOr/
│   │   ├── OnionTemplate.cs
│   │   ├── RepoTemplate.cs
│   │   ├── FeatureTemplate.cs
│   │   ├── PolicyTemplate.cs
│   │   ├── EventTemplate.cs
│   │   ├── JobTemplate.cs
│   │   └── Snippets/                # raw .cs.txt content, the default stubs
│   └── VerticalSliceMediatR/        # future template, same contract
├── Generators/
│   ├── SolutionGenerator.cs         # shells to `dotnet new`, wires csproj references
│   ├── RepositoryGenerator.cs       # repo + interface + UnitOfWork facade patch
│   ├── FeatureGenerator.cs          # command/query + handler + validator
│   ├── PolicyGenerator.cs
│   ├── EventGenerator.cs
│   ├── JobGenerator.cs
│   ├── ResourceGenerator.cs
│   ├── RouteListGenerator.cs        # reflects over the API project, no codegen output
│   ├── MessageFactoryGenerator.cs   # make:factory — parses the message's syntax tree for its shape
│   └── UnitOfWorkPatcher.cs         # orchestrates the three Roslyn edits in §6.3 via SyntaxEditor
├── Roslyn/
│   ├── SyntaxEditor.cs               # shared engine: load → find anchor node → insert/replace → save
│   ├── AnchorFinders/                # typed queries over a SyntaxTree, one per anchor shape
│   │   ├── InterfaceMemberAnchor.cs   # e.g. "the property list inside IUnitOfWork"
│   │   ├── ConstructorParamAnchor.cs  # e.g. "the last parameter before the closing paren"
│   │   └── LastMemberOfTypeAnchor.cs  # e.g. "the last public I*Repository property"
│   └── TriviaPreservingRewriter.cs   # CSharpSyntaxRewriter subclass that clones leading/trailing
│                                      # trivia from a sibling node so inserted members match
│                                      # existing indentation without a full-file format pass
├── Invocation/
│   ├── HandlerDiscovery.cs          # walks IWolverineRuntime.Handlers.Chains for invoke:list
│   ├── FactoryDiscovery.cs          # reflects over loaded assemblies for IForgeMessageFactory<T>
│   ├── PayloadResolver.cs           # implements the resolution order in §6.4
│   └── FixtureRepository.cs         # reads/writes .forge/fixtures/{MessageType}/{name}.json
└── StubRepository.cs                 # resolves .forge/stubs/ overrides before falling back
                                       # to embedded Snippets/
```

**Dependency note:** this pulls in `Microsoft.CodeAnalysis.CSharp` (Roslyn's compiler-as-a-service
package) as a direct dependency of `Forge.Cli` — it's the same package family every C# IDE/analyzer
is built on, and it ships as a plain NuGet package with no MSBuild SDK entanglement, so it packs
cleanly into a `dotnet tool` without dragging in a full compiler toolchain.

**Design principle:** generators never overwrite hand-written business logic. They only ever
*append* — a new property, a new file, a new constructor parameter. Re-running a generator
against an entity that already exists is a no-op with a warning, not a silent overwrite, unless
`--force` is explicit.

---

## 5. Config Schema — `forge.config.json`

Lives at the solution root (found the same way `dotnet` finds `.sln` — walk upward from CWD).
Every generator reads this before writing anything.

```json
{
  "template": "onion-wolverine-erroror",
  "solutionName": "OfficeCommute",

  "domainProject": "src/OfficeCommute.Domain",
  "domainEntitiesPath": "Entities",
  "domainNamespace": "OfficeCommute.Domain",

  "applicationProject": "src/OfficeCommute.ApplicationService",
  "applicationFeaturesPath": "Features",
  "applicationRepoPath": "Common/Interfaces/Persistence",
  "applicationUnitOfWorkInterfacePath": "Common/Interfaces/Persistence/Common/IUnitOfWork.cs",
  "applicationErrorsPath": "Common/Errors",
  "applicationNamespace": "OfficeCommute.ApplicationService",

  "infrastructureProject": "src/OfficeCommute.Infrastructure",
  "infrastructureRepoPath": "Persistence/Repository",
  "infrastructureUnitOfWorkImplPath": "Persistence/Repository/Common/UnitOfWork.cs",
  "infrastructureJobsPath": "BackgroundJobs",
  "infrastructureNamespace": "OfficeCommute.Infrastructure",

  "apiProject": "src/OfficeCommute.API",
  "apiNamespace": "OfficeCommute.API",

  "roleEnum": "UserRole",
  "roleGuardStyle": "single-array",
  "scheduler": "wolverine",

  "stubOverridesPath": ".forge/stubs"
}
```

`roleGuardStyle` toggles between the two shapes seen in the wild:

- `"single-array"` — `IRequireExplicitRoles { UserRole[] Roles }` + bare `IAllowRoleCheckBypass`
  marker.
- `"role-and-subrole"` — `IRequiresExplicitRoles` with both `AllowedRoles` and `AllowedSubRoles`,
  plus `IAllowAnonymousRoles`.

`scheduler` toggles `make:job`'s output between a Wolverine scheduled-message stub, a Hangfire
job class, or a Quartz `IJob` implementation.

The `FeatureTemplate` and `JobTemplate` read these flags and emit the matching shape, so one tool
serves multiple team conventions without a fork.

---

## 6. The `onion-wolverine-erroror` Template in Detail

### 6.1 Project layout produced by `make:solution`

```
src/
├── {Solution}.Domain/
│   ├── Entities/
│   ├── Enums/
│   └── Common/Authorization/
│       ├── IRequireExplicitRoles.cs        (or IRequiresExplicitRoles.cs, per roleGuardStyle)
│       └── IAllowRoleCheckBypass.cs         (or IAllowAnonymousRoles.cs)
├── {Solution}.ApplicationService/
│   ├── Common/
│   │   ├── Interfaces/
│   │   │   ├── Authentication/ICurrentUser.cs   (+ ICurrentUserSetter)
│   │   │   └── Persistence/Common/IUnitOfWork.cs
│   │   ├── Errors/                          (one Errors.{Entity}.cs per aggregate)
│   │   └── Response/
│   │       ├── PagedResult.cs
│   │       ├── PaginationExtensions.cs
│   │       └── PaginationParams.cs
│   └── Features/{Feature}/{Commands|Queries}/{Name}/
├── {Solution}.Infrastructure/
│   ├── Persistence/Repository/
│   │   ├── Common/UnitOfWork.cs
│   │   └── {Entity}Repository.cs
│   └── BackgroundJobs/
└── {Solution}.API/
    ├── Controllers/ApiController.cs         (ErrorOr<T> → ProblemDetails mapping)
    └── Middleware/
        ├── CurrentUserMiddleware.cs
        └── RoleCheckMiddleware.cs
```

### 6.2 Ideology files (written once, never regenerated)

**`ICurrentUser` / `ICurrentUserSetter` split** — application code depends only on the
read-only interface; only the Wolverine middleware is trusted with `SetUser(...)`:

```csharp
public interface ICurrentUser
{
    Guid Id { get; }
    string Email { get; }
    UserRole Role { get; }
    bool IsAuthenticated { get; }
    bool IsAdmin();
}

public interface ICurrentUserSetter : ICurrentUser
{
    void SetUser(Guid id, string email, UserRole role);
}
```

**Secure-by-default Wolverine middleware** — every message is denied unless it opts in:

```csharp
public static class RoleCheckMiddleware
{
    public static void Before(MessageContext context, ICurrentUser currentUser)
    {
        var message = context.Envelope?.Message;

        if (message is IAllowRoleCheckBypass) return;

        if (message is IRequireExplicitRoles explicitRole)
        {
            if (!explicitRole.Roles.Contains(currentUser.Role))
                throw new UnauthorizedAccessException(
                    $"Access Denied: Role '{currentUser.Role}' cannot execute this command.");
            return;
        }

        throw new UnauthorizedAccessException(
            $"Security Lockdown: '{message?.GetType().Name}' is restricted by default. " +
            $"Implement {nameof(IRequireExplicitRoles)} or {nameof(IAllowRoleCheckBypass)}.");
    }
}
```

**`ApiController` base** — turns `ErrorOr<T>` into the correct HTTP response so feature
controllers stay one-liners:

```csharp
public abstract class ApiController : ControllerBase
{
    protected readonly IMessageBus _bus;

    protected IActionResult HandleResult<T>(ErrorOr<T> result) =>
        result.Match(value => Ok(value), errors => HandleErrors(errors));

    private IActionResult HandleErrors(List<Error> errors) =>
        errors.All(e => e.Type == ErrorType.Validation)
            ? ValidationProblem(errors)
            : Problem(errors.First());
}
```

**`PagedResult<T>` + `ToPagedResult()`** — written once as generic ideology, reused by every
list query without per-entity duplication.

### 6.3 Generated-per-entity artifacts

**Repository pair**, wired into the `IUnitOfWork` facade automatically:

```csharp
public interface IGigRepository
{
    Task<Gig> CreateAsync(Gig gig);
    Task<Gig?> GetByIdAsync(Guid id);
    Task<IEnumerable<Gig>> GetAllAsync();
    Task<Gig> UpdateAsync(Gig gig);
    Task<bool> DeleteAsync(Guid id);
}
```

`RepositoryGenerator` then patches (not regenerates) three places by parsing each target file
into a Roslyn `SyntaxTree`, locating a stable *syntax node* — not a line of text — as the anchor,
and inserting new nodes relative to it:

1. `IUnitOfWork.cs` → find the `InterfaceDeclarationSyntax`, locate the `PropertyDeclarationSyntax`
   named `SaveChangesAsync`, and insert a new `IGigRepository Gig { get; }` property node
   immediately before it via `InsertNodesBefore`.
2. `UnitOfWork.cs` → same idea across three anchors in one file: the last
   `PropertyDeclarationSyntax` of type `I*Repository` (property), the constructor's
   `ParameterListSyntax` (parameter), and the last assignment statement in the constructor body
   (field assignment) — three coordinated `SyntaxEditor` edits applied and saved together.
3. `Errors.Gig.cs` → create if absent (no patch needed — this one's a fresh file).

Because the anchor is a syntax node found via `SyntaxKind` and a member's `Identifier.Text`
rather than a regex pattern matched against raw text, insertion survives things that would break
a text-based patch — reordered members, a `#region` wrapped around the property list, a
differently-formatted parameter list, or an extra blank line a developer added by hand. The new
node's leading/trailing trivia is cloned from its neighbor (`TriviaPreservingRewriter`, §4) so the
inserted line matches the file's existing indentation without running a formatter over the whole
file — the same "minimal, reviewable diff" goal the design already commits to, just backed by the
compiler's own parser instead of string matching.

```csharp
var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
var root = (CompilationUnitSyntax)tree.GetRoot();

var iface = root.DescendantNodes()
    .OfType<InterfaceDeclarationSyntax>()
    .First(i => i.Identifier.Text == "IUnitOfWork");

var anchor = iface.Members
    .OfType<PropertyDeclarationSyntax>()
    .First(p => p.Identifier.Text == "SaveChangesAsync" ||
                p.Type.ToString().Contains("Task"));

var newProperty = SyntaxFactory.ParseMemberDeclaration(
    "IGigRepository Gig { get; }")!.WithTriviaFrom(anchor);

var newIface = iface.InsertNodesBefore(anchor, new[] { newProperty });
var newRoot = root.ReplaceNode(iface, newIface);

File.WriteAllText(path, newRoot.ToFullString());
```

**Errors stub** (`ErrorOr` convention, one partial class per entity):

```csharp
public static partial class Errors
{
    public static class Gig
    {
        public static Error NotFound => Error.NotFound("Gig.NotFound", "Gig not found");
    }
}
```

**Feature (command/query + handler + validator)**, role-guarded per the configured style:

```csharp
public record CreateGigCommand(/* params */) : IRequireExplicitRoles
{
    public UserRole[] Roles => [UserRole.User];
}

public class CreateGigCommandHandler
{
    private readonly IUnitOfWork _unitOfWork;
    public CreateGigCommandHandler(IUnitOfWork unitOfWork) => _unitOfWork = unitOfWork;

    public async Task<ErrorOr<GigResult>> Handle(
        CreateGigCommand command, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated) return Errors.Authentication.UserNotFound;
        // TODO: implement
        await _unitOfWork.SaveChangesAsync(ct);
        return default!;
    }
}

public class CreateGigCommandValidator : AbstractValidator<CreateGigCommand>
{
    public CreateGigCommandValidator()
    {
        // TODO: rules
    }
}
```

**Authorization policy pair** (`make:policy -n Gig`), for checks finer-grained than the
route-level role guard — "is this the owner of *this* gig", not just "is this a `User`":

```csharp
public class GigAuthorizationRequirement : IAuthorizationRequirement
{
    public string Action { get; }
    public GigAuthorizationRequirement(string action) => Action = action;
}

public class GigAuthorizationHandler
    : AuthorizationHandler<GigAuthorizationRequirement, Gig>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GigAuthorizationRequirement requirement,
        Gig resource)
    {
        // TODO: e.g. resource.OwnerId == currentUserId
        return Task.CompletedTask;
    }
}
```

**Domain event + handler** (`make:event -n GigPublished`):

```csharp
public record GigPublished(Guid GigId, DateTime OccurredAt) : IAllowRoleCheckBypass;

public class GigPublishedHandler
{
    public Task Handle(GigPublished @event)
    {
        // TODO: implement
        return Task.CompletedTask;
    }
}
```

A second `make:listener -n NotifyOwner --for GigPublished` adds another handler for the same
event without touching the first.

### 6.4 Message Factories & Handler Invocation (`forge invoke`)

Scaffolding a handler is only half the loop — a developer immediately needs to *call* it without
standing up a controller, an HTTP client, or a full integration test. `invoke:run` closes that
loop by driving a message directly through Wolverine's runtime inside the same host/DI
container the real app uses, so middleware (role checks, validation, logging) still runs and the
result is representative of production behavior.

**The factory contract**

```csharp
public interface IForgeMessageFactory<TMessage>
{
    TMessage Create();
    TMessage Create(string state); // e.g. "invalid", "large-order"
}
```

Factories live in the *application* solution (not inside Forge itself), conventionally under
`Forge/Factories/`, one per message type, and are discovered the same way `db:seed` discovers
`ISeeder` implementations — reflection over loaded assemblies for closed `IForgeMessageFactory<T>`
implementations, registered into DI so a factory can pull in its own dependencies if it needs
them (though most should stay dependency-free):

```csharp
public class CreateGigFactory : IForgeMessageFactory<CreateGigCommand>
{
    public CreateGigCommand Create() => new(
        Name: new Faker().Commerce.ProductName(),
        BudgetMin: 100,
        BudgetMax: 500);

    public CreateGigCommand Create(string state) => state switch
    {
        "invalid" => Create() with { BudgetMin = -1 },
        _ => Create()
    };
}
```

`make:factory -n CreateGig` scaffolds this stub by parsing `CreateGigCommand`'s source file with
Roslyn and walking its `RecordDeclarationSyntax`/`ClassDeclarationSyntax` — reading the primary
constructor's `ParameterListSyntax` or the type's `PropertyDeclarationSyntax` members directly off
the syntax tree, rather than via runtime reflection against a built assembly. This matters
practically: `make:factory` typically runs seconds after `make:feature` scaffolded the message
type, before the solution has necessarily been rebuilt, so there's no compiled `Type` to reflect
over yet — the source *is* the only available source of truth at that point. Per-parameter type
names (`Guid`, `int`, `string`, `UserRole`, a nullable `T?`) map to stub value generators
(`Guid.NewGuid()`, enum defaults, Bogus fakers for `string`) the same way one `AnchorFinders`
query maps a `SyntaxKind` to an edit elsewhere in the tool — the same "never start from a blank
page" principle every other `make:*` generator follows. Bogus (`Faker`) is the natural fit for
realistic fake data instead of hand-typed literals.

**Payload resolution order for `invoke:run <MessageType>`**

1. `--payload '<json>'` or stdin JSON — explicit input always wins.
2. `--factory <name> [--state <name>]` — use a specific factory if more than one is registered
   for the message type.
3. Exactly one `IForgeMessageFactory<T>` registered for the type → use it automatically.
4. More than one factory registered → prompt the user to pick one interactively.
5. No factory exists → fall back to an interactive property-by-property prompt, or accept raw
   JSON typed in at the terminal.
6. Whatever payload was just built can be persisted via `invoke:fixture:save` for replay next
   time, without re-running a factory or re-typing JSON.

**Invocation and result rendering**

```bash
dotnet forge invoke:run CreateGigCommand --factory CreateGigFactory
```

Uses `IMessageBus.InvokeAsync<ErrorOr<GigResult>>(message)` — not `PublishAsync`/`SendAsync` —
because `InvokeAsync` runs the pipeline synchronously in-process *and* returns the handler's
result, which matters directly for this template since every handler already returns
`ErrorOr<T>`. Output is color-coded from that result: green + the serialized value on success,
red + each `Error.Code`/`Description` on `IsError`.

**Guardrails**

- `invoke:run` executes the full middleware pipeline for real — DB writes, role checks, external
  calls included. It's meant to run against a dev/test configuration, not production; `doctor`
  can optionally warn if `ASPNETCORE_ENVIRONMENT=Production` is detected when an `invoke:*`
  command is used.
- Factories are code, fixtures are data: a factory is dynamic and composable (`with` expressions,
  Bogus-generated values), while a fixture is a frozen JSON snapshot — reach for a fixture when
  replaying an exact edge case (e.g. one that broke in production last week), and a factory when
  a plausible-but-fresh instance is enough.
- This is deliberately narrower than a REPL (see §12) — `invoke:run` calls one message through
  once and exits, rather than opening an interactive session against the running host.

---

## 7. Non-Destructive Editing Rules

Because `RepositoryGenerator` (and similarly `UnitOfWorkPatcher`) edit existing files rather than
only creating new ones, they follow strict guardrails:

- **Idempotent**: before inserting, `SyntaxEditor` checks whether a member with the target
  identifier already exists on the type (a real semantic check against the syntax tree, not a
  text `Contains`) — if `IGigRepository Gig` is already a member of `IUnitOfWork`, the patch
  skips with a message rather than duplicating it.
- **Anchor-based insertion, resolved as syntax nodes**: insertions are relative to a stable
  `SyntaxNode` — e.g. "the `PropertyDeclarationSyntax` named `SaveChangesAsync`", "the last
  `PropertyDeclarationSyntax` whose type name matches `I*Repository`" — found via
  `DescendantNodes().OfType<T>()` queries, never a fixed line number or a regex against raw text.
  This means member reordering, added comments, a `#region` block, or a differently-wrapped
  parameter list elsewhere in the file don't break the patch the way a text-pattern match would.
- **`--dry-run`**: renders the post-edit `SyntaxTree` to a string and diffs it against the
  original on disk, without writing — so the preview is byte-for-byte what would actually be
  written, not an approximation.
- **`--force`**: the only way to make a generator overwrite. Without it, every `make:*` command
  refuses and prints what already exists — this is the explicit, documented default across the
  whole tool, not just repo wiring.
- **No formatting reflow**: `TriviaPreservingRewriter` clones the anchor node's leading/trailing
  trivia (whitespace, newlines, indentation) onto the inserted node, and the tree is emitted via
  `ToFullString()` rather than passed through `NormalizeWhitespace()` or a full formatter — so the
  diff is exactly the new member, not a reflow of the whole file's whitespace.
- **Parse-then-patch, never a partial write**: all edits to a given file are computed against one
  in-memory `SyntaxTree` and written in a single `File.WriteAllText` at the end. If any anchor in
  a multi-anchor file (e.g. `UnitOfWork.cs`'s property + constructor param + assignment) can't be
  found, the whole file's patch is aborted before anything is written — there's no partially
  patched file left behind to hand-fix.

---

## 8. Stub Customization (`stub:publish`)

Every generator's template snippet is embedded as a `.cs.txt` file under
`Templates/{TemplateName}/Snippets/`. Rather than hardcoding those strings permanently,
`stub:publish` copies them into the consuming solution:

```bash
dotnet forge stub:publish
# → copies every embedded snippet into .forge/stubs/
```

Every generator checks `.forge/stubs/` **first**, falling back to the built-in embedded template
only if a matching file is absent. This means a team can:

- Add a license header or company boilerplate comment to every generated file.
- Change a generated class's base type or add an attribute (e.g. `[ExcludeFromCodeCoverage]`)
  without forking the tool.
- Rename generated members to match an in-house convention that differs slightly from the
  built-in default.

`stub:diff` compares the published stubs against the tool's current embedded defaults — useful
after upgrading the `forge` NuGet package, to see whether an upstream stub changed in a way you
might want to pull into your customized copy.

This is the single highest-leverage feature borrowed from Artisan: without it, every template
tweak requires forking the CLI's source. With it, the CLI's source stays generic and every
team's specific conventions live in version-controlled files sitting right in their own repo.

---

## 9. Supporting Other Templates

The core engine (`Program.cs`, `ConfigLoader`, CLI parsing, the `doctor`/`config:*` diagnostics)
has no onion-specific knowledge. Everything architecture-specific lives behind one interface:

```csharp
public interface ITemplate
{
    string Name { get; }                                     // "onion-wolverine-erroror"

    void ScaffoldSolution(ForgeConfig config);                 // make:solution
    void ScaffoldEntity(ForgeConfig config, EntitySpec spec);  // make:entity
    void ScaffoldRepository(ForgeConfig config, string entity);// make:repo
    void ScaffoldFeature(ForgeConfig config, FeatureSpec spec);// make:feature
    void ScaffoldPolicy(ForgeConfig config, string entity);    // make:policy
    void ScaffoldEvent(ForgeConfig config, string name);       // make:event
    void ScaffoldListener(ForgeConfig config, string @event, string name); // make:listener
    void ScaffoldJob(ForgeConfig config, string name);         // make:job
    void ScaffoldResource(ForgeConfig config, string entity);  // make:resource
}
```

`forge.config.json`'s `"template"` field selects the implementation at startup via a simple
registry:

```csharp
var templates = new Dictionary<string, ITemplate>
{
    ["onion-wolverine-erroror"]     = new OnionWolverineErrorOrTemplate(),
    ["vertical-slice-mediatr"]      = new VerticalSliceMediatRTemplate(),
    ["minimal-api-fastendpoints"]   = new MinimalApiFastEndpointsTemplate(),
};

var template = templates[config.Template];
```

Adding a new template means writing one class that implements `ITemplate` and dropping its
snippet files under `Templates/{Name}/Snippets/` — the CLI surface (`make:solution`, `make:repo`,
`make:feature`, ...) stays identical across templates; only the generated shape changes. This is
what makes `dotnet forge make:repo -i Gig` behave identically whether the solution uses
Wolverine + ErrorOr + a UnitOfWork facade, or MediatR + FluentResults + a generic `IRepository<T>`
— the verb is the same, the template decides the shape.

### 9.1 Third-party template packages (future direction)

Once there are two or more templates in active use, it's worth letting a template ship as its
*own* NuGet package rather than being compiled into `Forge.Cli` itself — a company could publish
`Forge.Templates.VerticalSlice` and reference it from their tool manifest, and `forge` would
discover it via a marker attribute or a `forge.templates.json` opt-in list, the same way MSBuild
SDKs are resolved. This is explicitly **not** in scope for v1 (see §12), but the `ITemplate`
contract is designed so it isn't precluded later — nothing about it assumes compile-time-only
registration.

---

## 10. Additional Features Worth Building

Beyond direct Artisan parity, these earn their place for a tool that's going to be a daily driver
across a team, not a one-off script:

| Feature | Why it matters |
|---|---|
| **`doctor`** | Catches drift between config and reality before a generator fails halfway through a multi-file write — e.g. `roleGuardStyle: single-array` in config but `IRequiresExplicitRoles` (the other shape) actually in the codebase. |
| **`config:validate`** | Same idea, narrower: just checks paths/namespaces resolve. Fast enough to run as a pre-commit hook. |
| **`--json` on every command** | Lets CI assert "this PR's `make:feature` produced exactly 3 files" instead of parsing colored terminal output. |
| **Shell completion (`forge completion bash\|zsh\|pwsh`)** | System.CommandLine ships this almost for free; skipping it is leaving usability on the table. |
| **`make:test`** | Every other `make:*` command scaffolds production code; test scaffolding was conspicuously the one thing Artisan-parity lists usually forget. Wires a fixture that already knows how to construct the feature's handler with fakes for `IUnitOfWork`/`ICurrentUser`. |
| **Stub versioning via `stub:diff`** | Without it, `stub:publish` is a one-way door — you customize once and silently stop receiving upstream improvements. With it, upgrading the tool doesn't mean losing track of what changed. |
| **Idempotent `db:seed`** | Seeders should be safe to re-run against a database that already has some rows — the `ISeeder` contract should nudge implementers toward `upsert`-style logic, not `INSERT` that throws on the second run. |
| **`route:list --role`** | Filtering route output by role turns "what can a Guest actually hit" from a manual audit into one command — genuinely useful for a security review, not just a nice-to-have. |
| **Non-interactive mode for every `make:*` command** | All flags can be supplied inline (`-n`, `-i`, `--type`, `--roles`) specifically so `forge` is scriptable in CI/codegen pipelines, not just a human-interactive tool. |
| **`invoke:run` + message factories (§6.4)** | Lets a developer call a handler directly — through the real Wolverine pipeline, with a real DI-resolved `ErrorOr<T>` result — without standing up a controller or an integration test harness. `make:factory` removes the blank-page problem of hand-typing JSON payloads for every command/query. |

Deliberately **out of scope** for the reasons in §12: a full `tinker`-style interactive REPL
session (as opposed to the one-shot `invoke:run`), a plugin marketplace, and telemetry/analytics
of any kind.

---

## 11. Packaging & Installation

```bash
dotnet pack ./Forge.Cli -o ./Forge.Cli/nupkg

# once per solution:
dotnet new tool-manifest
dotnet tool install --local YourOrg.Forge.Cli --add-source ./Forge.Cli/nupkg

# committed to source control via .config/dotnet-tools.json — every teammate runs:
dotnet tool restore
```

Because `ToolCommandName` is set to `dotnet-forge`, once installed the tool integrates as a
first-class verb: `dotnet forge ...` rather than `dotnet tool run forge ...`.

---

## 12. Roadmap

- [ ] `make:entity --properties` parsing into real C# property declarations and an EF Core
      `IEntityTypeConfiguration<T>` stub (currently a TODO placeholder).
- [ ] `make:controller` — one action per existing feature discovered in a folder.
- [ ] `--dry-run` diff preview across every command that writes, not just repo wiring.
- [ ] A second reference template (`vertical-slice-mediatr`) to validate the `ITemplate`
      abstraction actually holds up under a genuinely different architecture.
- [ ] Golden-file tests: for each template, scaffold into a throwaway temp solution and assert it
      compiles (`dotnet build`) as part of the tool's own CI.
- [ ] `doctor` checks expanded beyond role-guard-shape and stub-drift (e.g. verify the configured
      `scheduler` package is actually referenced in the infrastructure project).
- [ ] Shell completion scripts for bash/zsh/PowerShell.
- [ ] `make:factory`'s Roslyn-based shape inspection for records with nested objects, collections,
      and `required` members — the interactive/JSON prompt fallback (§6.4) needs to stay reliable
      for message shapes the generator can't yet stub cleanly.
- [ ] `AnchorFinders` coverage for edge cases the current three don't handle yet — e.g. a
      `UnitOfWork.cs` where the constructor uses primary-constructor syntax instead of a
      body-assignment pattern, or an `IUnitOfWork` with no `SaveChangesAsync` member to anchor
      against at all (falls back to "last member in the interface" in that case).
- [ ] `invoke:run --json` output shape, so CI can assert "invoking `CreateGigCommand` with fixture
      X returns `IsError == false`" the same way other commands' `--json` flag is used today.
- [ ] **Explicitly deferred, not rejected:** third-party template packages (§9.1), a full
      `tinker`-style interactive REPL bootstrap (closest .NET analog is a companion
      `dotnet-script` `.csx` file that pre-wires `DbContext`/DI — worth a mention in docs, not
      worth building bespoke; `invoke:run` deliberately stays one-shot rather than growing into
      this), and any telemetry/usage-analytics collection.
