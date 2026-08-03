using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Runtime.Wolverine;

/// <summary>
/// Applies an identity before an invocation, so <c>invoke:run --as-role Admin</c> or
/// <c>--as admin</c> can exercise a role-guarded message.
///
/// Without this, invoke:run is useless against this template: the pipeline is secure by default,
/// a CLI-launched process has nobody signed in, and every guarded message is denied as Guest.
/// Verified for real — the first successful invocation was a 403 from RoleCheckMiddleware.
///
/// This relies on the generated Program.cs registering the current-user services as SINGLETONS
/// under a forge invocation. Scoped registrations would defeat it: Wolverine creates a scope per
/// message, so the instance set here would not be the instance the middleware reads. Also
/// verified — the same command failed as Guest scoped, and succeeded singleton.
///
/// Duck-typed rather than interface-based: ICurrentUserSetter lives in the user's generated
/// ApplicationService project and this package cannot reference it, nor can the template
/// implement an interface from an opt-in package. Reflection is the only seam that couples
/// neither way.
/// </summary>
internal static class Impersonation
{
    private const string SetterInterface = "ICurrentUserSetter";
    private const string SetterMethod = "SetUser";

    /// <summary>
    /// Applies the identity described by <paramref name="identityJson"/>. Throws a clear,
    /// actionable error rather than silently invoking as Guest and letting the role guard
    /// produce a denial the user then has to diagnose.
    /// </summary>
    public static void Apply(IServiceProvider services, string identityJson)
    {
        JsonObject identity;
        try
        {
            identity = JsonNode.Parse(identityJson)?.AsObject()
                       ?? throw new ForgeRuntimeException("The identity payload is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ForgeRuntimeException($"The identity payload is not valid JSON: {ex.Message}");
        }

        var setter = FindSetter(services)
            ?? throw new ForgeRuntimeException(
                $"No registered {SetterInterface} was found, so the identity cannot be applied." +
                Environment.NewLine +
                "  -> The generated Program.cs registers one. If you replaced it, ensure " +
                $"{SetterInterface} resolves." + Environment.NewLine +
                "  -> Under a forge invocation it must be a SINGLETON: Wolverine scopes each " +
                "message, so a scoped instance would not reach the handler.");

        var method = setter.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == SetterMethod && m.GetParameters().Length == 3)
            ?? throw new ForgeRuntimeException(
                $"{setter.GetType().Name} has no {SetterMethod}(id, email, role) method.");

        var parameters = method.GetParameters();
        var arguments = new object?[3];

        arguments[0] = ReadId(identity, parameters[0].ParameterType);
        arguments[1] = identity["email"]?.GetValue<string>() ?? "forge@invoke.local";
        arguments[2] = ReadRole(identity, parameters[2].ParameterType);

        try
        {
            method.Invoke(setter, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new ForgeRuntimeException($"{SetterMethod} threw: {ex.InnerException.Message}");
        }
    }

    private static object ReadId(JsonObject identity, Type parameterType)
    {
        var raw = identity["id"]?.GetValue<string>();

        if (parameterType == typeof(Guid))
        {
            if (string.IsNullOrWhiteSpace(raw)) return Guid.Empty;

            return Guid.TryParse(raw, out var parsed)
                ? parsed
                : throw new ForgeRuntimeException($"Identity 'id' is not a valid Guid: '{raw}'.");
        }

        if (parameterType == typeof(string)) return raw ?? string.Empty;

        throw new ForgeRuntimeException(
            $"{SetterMethod}'s first parameter is {parameterType.Name}, which forge cannot map an id onto.");
    }

    private static object ReadRole(JsonObject identity, Type parameterType)
    {
        var role = identity["role"]?.GetValue<string>()
            ?? throw new ForgeRuntimeException("The identity payload has no 'role'.");

        if (!parameterType.IsEnum)
        {
            throw new ForgeRuntimeException(
                $"{SetterMethod}'s third parameter is {parameterType.Name}, not an enum, " +
                "so forge cannot map a role name onto it.");
        }

        if (Enum.TryParse(parameterType, role, ignoreCase: true, out var parsed) && parsed is not null)
            return parsed;

        throw new ForgeRuntimeException(
            $"'{role}' is not a value of {parameterType.Name}." + Environment.NewLine +
            $"  -> Valid roles: {string.Join(", ", Enum.GetNames(parameterType))}");
    }

    /// <summary>Finds the setter by interface NAME — the type lives in the user's assembly.</summary>
    private static object? FindSetter(IServiceProvider services)
    {
        foreach (var candidate in CandidateTypes())
        {
            var resolved = services.GetService(candidate);
            if (resolved is not null) return resolved;
        }

        return null;
    }

    private static IEnumerable<Type> CandidateTypes()
    {
        // AppDomain rather than DI enumeration: IServiceProvider offers no way to list
        // registrations, so loaded assemblies are the only place to find the interface.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            catch { continue; }

            foreach (var type in types)
            {
                if (type.IsInterface && type.Name == SetterInterface) yield return type;
            }
        }
    }
}
