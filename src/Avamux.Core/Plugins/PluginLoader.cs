using System.Reflection;
using System.Runtime.Loader;

namespace Avamux.Core.Plugins;

/// <summary>
/// Loads plugin assemblies. Each load gets a context of its own, and the DLL is read into memory first so the file
/// is not held open: a plugin can be rebuilt (or deleted) while avamux runs and loaded again. Anything avamux has
/// itself (the plugin contract, Avalonia, Avamux.Core, the framework) comes from avamux, so types are the same on both
/// sides; the rest comes from beside the plugin.
/// </summary>
public static class PluginLoader
{
    public static Assembly Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"Plugin DLL not found: {full}");
        var context = new Context(full);
        using var dll = new MemoryStream(File.ReadAllBytes(full));
        var symbols = Path.ChangeExtension(full, ".pdb");
        if (!File.Exists(symbols)) return context.LoadFromStream(dll);
        using var pdb = new MemoryStream(File.ReadAllBytes(symbols));
        return context.LoadFromStream(dll, pdb);
    }

    /// <summary>The types of an assembly, with a readable message when some cannot be loaded.</summary>
    public static Type[] Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            throw new InvalidOperationException($"{assembly.GetName().Name} could not be read: {ex.LoaderExceptions.FirstOrDefault(e => e is not null)?.Message ?? ex.Message}");
        }
    }

    /// <summary>Concrete classes in <paramref name="types"/> that implement <typeparamref name="T"/>.</summary>
    public static List<Type> Implementing<T>(IEnumerable<Type> types) =>
        types.Where(t => typeof(T).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) is not null).ToList();

    private sealed class Context(string path) : AssemblyLoadContext("plugin " + Path.GetFileName(path), isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        private readonly string _folder = Path.GetDirectoryName(path)!;

        protected override Assembly? Load(AssemblyName name)
        {
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"))) return null;
            var found = _resolver.ResolveAssemblyToPath(name);
            if (found is null && Path.Combine(_folder, name.Name + ".dll") is var beside && File.Exists(beside)) found = beside;
            return found is null ? null : LoadFromAssemblyPath(found);
        }
    }
}
