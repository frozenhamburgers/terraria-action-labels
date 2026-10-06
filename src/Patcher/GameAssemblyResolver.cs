using Mono.Cecil;

/// <summary>
/// Resolves Terraria's references the way the game does at runtime: the GAC ->
/// game folder -> assemblies embedded in Terraria.exe
/// </summary>
sealed class GameAssemblyResolver : IAssemblyResolver
{
    static readonly string[] GacRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\assembly\GAC_32"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\assembly\GAC_MSIL"),
    ];

    readonly string _gameDir;
    readonly string _exePath;
    readonly Dictionary<string, AssemblyDefinition> _cache = new();

    public GameAssemblyResolver(string gameDir, string exePath)
    {
        _gameDir = gameDir;
        _exePath = exePath;
    }

    public AssemblyDefinition Resolve(AssemblyNameReference name) =>
        Resolve(name, new ReaderParameters());

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_cache.TryGetValue(name.FullName, out var cached))
            return cached;

        parameters.AssemblyResolver ??= this;
        AssemblyDefinition assembly = FromGac(name, parameters)
            ?? FromGameDir(name, parameters)
            ?? FromEmbedded(name, parameters)
            ?? throw new AssemblyResolutionException(name);

        _cache[name.FullName] = assembly;
        return assembly;
    }

    // GAC layout: <root>\<name>\v4.0_<version>_<culture>_<token>\<name>.dll,
    // where an invariant culture is an empty string.
    AssemblyDefinition? FromGac(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (name.PublicKeyToken is not { Length: > 0 } token)
            return null;

        string culture = string.IsNullOrEmpty(name.Culture) || name.Culture == "neutral" ? "" : name.Culture;
        string folder = $"v4.0_{name.Version}_{culture}_{Convert.ToHexStringLower(token)}";
        foreach (string root in GacRoots)
        {
            string path = Path.Combine(root, name.Name, folder, name.Name + ".dll");
            if (File.Exists(path))
                return AssemblyDefinition.ReadAssembly(path, parameters);
        }
        return null;
    }

    AssemblyDefinition? FromGameDir(AssemblyNameReference name, ReaderParameters parameters)
    {
        string path = Path.Combine(_gameDir, name.Name + ".dll");
        return File.Exists(path) ? AssemblyDefinition.ReadAssembly(path, parameters) : null;
    }

    // Mirrors the game's handler in WindowsLaunch.Main: the first manifest
    // resource whose name ends with "<simple name>.dll".
    AssemblyDefinition? FromEmbedded(AssemblyNameReference name, ReaderParameters parameters)
    {
        using var exe = ModuleDefinition.ReadModule(_exePath);
        var resource = exe.Resources
            .OfType<EmbeddedResource>()
            .FirstOrDefault(r => r.Name.EndsWith(name.Name + ".dll"));
        if (resource == null)
            return null;

        // GetResourceData copies the bytes, so they outlive the exe module.
        return AssemblyDefinition.ReadAssembly(new MemoryStream(resource.GetResourceData()), parameters);
    }

    public void Dispose()
    {
        foreach (var assembly in _cache.Values)
            assembly.Dispose();
        _cache.Clear();
    }
}
