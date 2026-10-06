using System.Diagnostics.CodeAnalysis;
using Mono.Cecil;
using Mono.Cecil.Cil;

const string Usage = """
    Usage:
      Patcher patch   <game dir>   Inject the GameHook call & copy GameHook.dll.
      Patcher restore <game dir>   Put the original Terraria.exe back & remove GameHook.dll.
    """;

try
{
    switch (args)
    {
        case ["patch", var dir]:
            GamePatcher.Patch(dir);
            return 0;
        case ["restore", var dir]:
            GamePatcher.Restore(dir);
            return 0;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (PatchException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

/// <summary>A refusal or failure with a message meant for the user.</summary>
sealed class PatchException(string message) : Exception(message);

static class GamePatcher
{
    const string ExeName = "Terraria.exe";
    const string BackupName = "Terraria.exe.orig";
    const string HookFileName = "GameHook.dll";
    const string LiveLogPathFileName = "GameHook.livelog";
    const string HookAssemblyName = "GameHook";
    const string HookTypeName = "GameHook.Hook";
    const string HookMethodName = "OnTick";
    const string TargetTypeName = "Terraria.Main";
    const string TargetMethodName = "DoUpdate";
    static readonly Version ExpectedVersion = new(1, 4, 5, 8);

    public static void Patch(string gameDir)
    {
        string exe = Path.Combine(gameDir, ExeName);
        string backup = Path.Combine(gameDir, BackupName);
        string temp = exe + ".tmp";
        string hookSource = Path.Combine(AppContext.BaseDirectory, HookFileName);
        string hookTarget = Path.Combine(gameDir, HookFileName);

        Require(File.Exists(exe), $"{exe} not found.");
        Require(File.Exists(hookSource), $"{hookSource} not found. Build the solution first.");

        if (File.Exists(backup))
        {
            // The game exe may have been replaced since the backup was taken
            // (game update, file verification). Patching the stale backup
            // would silently downgrade the game, so refuse. Cecil keeps the
            // module's MVID when it rewrites, so ours still matches.
            Require(ReadMvid(exe) == ReadMvid(backup),
                $"{ExeName} does not match {BackupName}; the game was probably updated. " +
                $"Delete {BackupName} if {ExeName} is unpatched, then patch again.");
        }
        else
        {
            Require(!ReferencesHook(exe),
                $"{ExeName} is already patched but {BackupName} is missing; cannot recover the original.");
            File.Copy(exe, backup);
            Console.WriteLine($"Backed up {ExeName} to {BackupName}.");
        }

        try
        {
            WritePatched(gameDir, backup, hookSource, temp);
            VerifyPatched(temp);

            // hook first then exe: there is never a patched exe without its hook, would crash due to startup force JIT
            File.Copy(hookSource, hookTarget, overwrite: true);
            File.Move(temp, exe, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
        Console.WriteLine($"Patched {exe}.");

        WriteLiveLogPath(gameDir);
    }

    /// <summary>
    /// Tells GameHook where LiveLog.exe is so the game can open it
    /// </summary>
    static void WriteLiveLogPath(string gameDir)
    {
        string pathFile = Path.Combine(gameDir, LiveLogPathFileName);
        string patcherProjectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\.."));
        string binTail = Path.GetRelativePath(patcherProjectDir, AppContext.BaseDirectory); // bin\Debug\net10.0
        string liveLog = Path.GetFullPath(Path.Combine(patcherProjectDir, @"..\LiveLog", binTail, "LiveLog.exe"));

        if (File.Exists(liveLog))
        {
            File.WriteAllText(pathFile, liveLog);
            Console.WriteLine($"LiveLog will open with the game ({liveLog}).");
        }
        else
        {
            File.Delete(pathFile);
            Console.WriteLine($"LiveLog not found at {liveLog}; the game will not open it.");
        }
    }

    static void WritePatched(string gameDir, string backup, string hookSource, string temp)
    {
        // writing makes Cecil resolve some referenced types so it needs the game's references
        // TODO: might be a better way to do this without the custom resolver, but i'm not sure as of now
        using var resolver = new GameAssemblyResolver(gameDir, backup);
        var readerParameters = new ReaderParameters { AssemblyResolver = resolver };

        // Always patch from the pristine backup, so running twice can never inject twice.
        using (var module = ModuleDefinition.ReadModule(backup, readerParameters))
        using (var hookModule = ModuleDefinition.ReadModule(hookSource))
        {
            var version = module.Assembly.Name.Version;
            Require(version == ExpectedVersion,
                $"Expected Terraria {ExpectedVersion}, found {version}. Re-verify the injection point first.");
            Require(!ReferencesHook(module), $"{BackupName} is not an unpatched original.");

            MethodDefinition target = FindTarget(module);
            MethodDefinition onTick = FindHook(hookModule);

            // o,porting creates the GameHook assembly reference and member references in Terraria's metadata.
            MethodReference onTickRef = module.ImportReference(onTick);
            Instruction first = target.Body.Instructions[0];
            target.Body.GetILProcessor().InsertBefore(first, Instruction.Create(OpCodes.Call, onTickRef));

            module.Write(temp);
        }
    }

    public static void Restore(string gameDir)
    {
        string exe = Path.Combine(gameDir, ExeName);
        string backup = Path.Combine(gameDir, BackupName);
        string hookTarget = Path.Combine(gameDir, HookFileName);

        Require(File.Exists(backup), $"{backup} not found; nothing to restore.");

        // exe first then hook for restoring
        File.Copy(backup, exe, overwrite: true);
        File.Delete(hookTarget);
        File.Delete(Path.Combine(gameDir, LiveLogPathFileName));
        File.Delete(backup);
        Console.WriteLine($"Restored {exe}.");
    }

    static MethodDefinition FindTarget(ModuleDefinition module)
    {
        TypeDefinition? main = module.GetType(TargetTypeName);
        Require(main != null, $"Type {TargetTypeName} not found.");

        var candidates = main.Methods.Where(m => m.Name == TargetMethodName).ToList();
        Require(candidates.Count == 1, $"Expected one {TargetTypeName}.{TargetMethodName}, found {candidates.Count}.");
        MethodDefinition method = candidates[0];

        Require(method.HasBody && !method.IsStatic
                && method.ReturnType.MetadataType == MetadataType.Void
                && method.Parameters.Count == 1
                && method.Parameters[0].ParameterType is ByReferenceType { ElementType.FullName: "Microsoft.Xna.Framework.GameTime" },
            $"{TargetTypeName}.{TargetMethodName} does not have the expected signature void (ref GameTime).");

        // Cecil branches point at Instruction objects. Anything that jumps to the current first instruction would skip a call inserted before it.
        Instruction first = method.Body.Instructions[0];
        bool targeted = method.Body.Instructions.Any(i =>
                i.Operand == first || (i.Operand is Instruction[] targets && targets.Contains(first)))
            || method.Body.ExceptionHandlers.Any(h =>
                h.TryStart == first || h.HandlerStart == first || h.FilterStart == first);
        Require(!targeted, $"The first instruction of {TargetMethodName} is a branch or handler target.");

        return method;
    }

    static MethodDefinition FindHook(ModuleDefinition hookModule)
    {
        TypeDefinition? type = hookModule.GetType(HookTypeName);
        Require(type is { IsPublic: true }, $"Public type {HookTypeName} not found in {HookFileName}.");

        MethodDefinition? method = type.Methods.SingleOrDefault(m => m.Name == HookMethodName);
        Require(method is { IsPublic: true, IsStatic: true, HasParameters: false }
                && method.ReturnType.MetadataType == MetadataType.Void,
            $"{HookTypeName}.{HookMethodName} must be public static void with no parameters.");

        return method;
    }

    /// <summary>Re-read the written file and check the call is really there.</summary>
    static void VerifyPatched(string path)
    {
        using var module = ModuleDefinition.ReadModule(path);
        Instruction first = FindTarget(module).Body.Instructions[0];
        Require(first.OpCode == OpCodes.Call
                && first.Operand is MethodReference m
                && m.DeclaringType.FullName == HookTypeName
                && m.Name == HookMethodName,
            "Verification failed: the hook call is not the first instruction of the written file.");
    }

    static Guid ReadMvid(string path)
    {
        using var module = ModuleDefinition.ReadModule(path);
        return module.Mvid;
    }

    static bool ReferencesHook(string path)
    {
        using var module = ModuleDefinition.ReadModule(path);
        return ReferencesHook(module);
    }

    static bool ReferencesHook(ModuleDefinition module) =>
        module.AssemblyReferences.Any(r => r.Name == HookAssemblyName);

    static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
            throw new PatchException(message);
    }
}
