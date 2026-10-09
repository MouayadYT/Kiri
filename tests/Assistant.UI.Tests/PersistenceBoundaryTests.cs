using System.Reflection;
using Assistant.Data;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// Where saved data may be reached from inside the app (ARCHITECTURE.md, "Persistence flow"). The app runs no SQL of its
/// own, and only the history adapter (<c>Assistant.UI.History</c>) and the composition root (<c>Assistant.UI.Bootstrap</c>)
/// know the Data module: view models, settings pages, views and controls reach saved conversations and settings through
/// Core's contracts and <see cref="IHistorySource"/> alone, so none of them can hold a connection, a repository or SQL.
/// </summary>
public sealed class PersistenceBoundaryTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly App = typeof(HistoryViewModel).Assembly;

    // The Data module and the SQLite provider under it.
    private static readonly string[] DataAssemblies = ["Assistant.Data", "Microsoft.Data.Sqlite"];

    private static readonly string[] MayKnowTheData = ["Assistant.UI.History", "Assistant.UI.Bootstrap"];

    [Fact]
    public void TheAppHasNoSqliteOfItsOwn()
    {
        var references = App.GetReferencedAssemblies().Select(name => name.Name!).ToArray();

        // The composition root registers the Data module; nothing in the app talks to SQLite itself.
        Assert.Contains("Assistant.Data", references);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", references);
        Assert.DoesNotContain(references, name => name.StartsWith("SQLitePCLRaw", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheHistoryAdapterAndTheCompositionRootKnowTheDataModule()
    {
        var outside = App.GetTypes().Where(type => !MayKnowTheData.Any(space => IsIn(type, space))).ToArray();

        var offenders = outside
            .SelectMany(type => TypesUsedBy(type).Where(IsData).Select(used => $"{type.FullName} uses {used.FullName}"))
            .Distinct()
            .ToArray();

        // The rule covers the view models and the settings pages, among everything else.
        Assert.Contains(outside, type => type == typeof(HistoryViewModel));
        Assert.Contains(outside, type => type == typeof(Assistant.UI.Settings.SettingsViewModel));
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheCheckSeesTheDataModuleWhereverAClassCouldUseIt()
    {
        // Each sample reaches the database in a different way; the check above must see every one of them.
        Assert.Contains(typeof(HoldsTheHistory).SelectNested().SelectMany(TypesUsedBy), IsData);
        Assert.Contains(typeof(TakesTheInitializer).SelectNested().SelectMany(TypesUsedBy), IsData);
        Assert.Contains(typeof(CatchesItsFailure).SelectNested().SelectMany(TypesUsedBy), IsData);
        Assert.Contains(typeof(AwaitsItsOptions).SelectNested().SelectMany(TypesUsedBy), IsData);
        Assert.Contains(typeof(ListsItsIds).SelectNested().SelectMany(TypesUsedBy), IsData);
        Assert.DoesNotContain(typeof(Clean).SelectNested().SelectMany(TypesUsedBy), IsData);
    }

    private static bool IsIn(Type type, string space) =>
        type.Namespace is { } name && (name == space || name.StartsWith(space + ".", StringComparison.Ordinal));

    private static bool IsData(Type type) => DataAssemblies.Contains(type.Assembly.GetName().Name);

    // Every type a class names where reflection can see it: what it derives from and implements, its fields (captured
    // variables and an async method's locals among them, in the classes the compiler makes), properties and events, what
    // its methods take and return, their locals, and the exceptions they catch, with the arguments of any generic type.
    private static IEnumerable<Type> TypesUsedBy(Type type)
    {
        var named = new List<Type>();
        if (type.BaseType is { } baseType)
        {
            named.Add(baseType);
        }

        named.AddRange(type.GetInterfaces());
        named.AddRange(type.GetFields(Declared).Select(field => field.FieldType));
        named.AddRange(type.GetProperties(Declared).Select(property => property.PropertyType));
        named.AddRange(type.GetEvents(Declared).Select(@event => @event.EventHandlerType).OfType<Type>());
        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            if (method is MethodInfo { ReturnType: var returns })
            {
                named.Add(returns);
            }

            named.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
            if (method.GetMethodBody() is { } body)
            {
                named.AddRange(body.LocalVariables.Select(local => local.LocalType));
                named.AddRange(body.ExceptionHandlingClauses
                    .Where(clause => clause.Flags == ExceptionHandlingClauseOptions.Clause)
                    .Select(clause => clause.CatchType!));
            }
        }

        return named.SelectMany(Expand);
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.IsGenericParameter)
        {
            yield break;
        }

        if (type.HasElementType)
        {
            foreach (var element in Expand(type.GetElementType()!))
            {
                yield return element;
            }

            yield break;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return argument;
            }
        }
    }

    // Samples for the check itself. None of them is used.
    private sealed class HoldsTheHistory
    {
        public SqliteConversationService? History { get; init; }
    }

    private sealed class TakesTheInitializer(IDatabaseInitializer initializer)
    {
        public override string ToString() => initializer.GetType().Name;
    }

    private static class CatchesItsFailure
    {
        public static bool Run(Action work)
        {
            try
            {
                work();
                return true;
            }
            catch (DatabaseException)
            {
                return false;
            }
        }
    }

    // Only the async method's own local names the Data module: the compiler keeps it in the class it makes for the method.
    private static class AwaitsItsOptions
    {
        public static async Task<string> RunAsync(Func<Task<object>> open)
        {
            var options = (DatabaseOptions)await open();
            return options.DatabasePath;
        }
    }

    private static class ListsItsIds
    {
        public static int Count(Func<List<DatabaseTooNewException>> read) => read().Count;
    }

    private sealed class Clean
    {
        public HistoryViewModel? History { get; init; }
    }
}

/// <summary>A type with the types nested in it, such as the ones the compiler makes for its lambdas and async methods.</summary>
file static class NestedTypes
{
    public static IEnumerable<Type> SelectNested(this Type type) =>
        [type, .. type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(SelectNested)];
}
