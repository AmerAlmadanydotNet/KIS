// IExplorerCommand implementations for the KIS top-level Explorer menu.
//
// Layout:  KIS  →  Compress to .kis
//                   Compress to .kis (encrypted)…
//                   Extract here          (only on .kis archives)
//                   Verify integrity      (only on .kis archives)
//                   Show info             (only on .kis archives)
//
// Each command, when invoked, spawns the standalone kis.exe alongside the DLL.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace KIS.ShellExt;

// ─── Base helper ─────────────────────────────────────────────────────────
public abstract class ExplorerCommandBase : IExplorerCommand
{
    public abstract string Title { get; }
    public virtual string IconPath => Path.Combine(AssemblyDir, "kis.exe");
    public virtual ExpCmdFlags Flags => ExpCmdFlags.Default;
    public virtual ExpCmdState GetStateFor(string[] paths) => ExpCmdState.Enabled;
    public abstract void Run(string[] paths);

    protected static string AssemblyDir =>
        Path.GetDirectoryName(typeof(ExplorerCommandBase).Assembly.Location) ?? "";

    protected static string KisExe => Path.Combine(AssemblyDir, "kis.exe");

    public int GetTitle(IShellItemArray? psiItemArray, out IntPtr ppszName)
        { ppszName = ShellHelpers.Str(Title); return HResults.S_OK; }

    public int GetIcon(IShellItemArray? psiItemArray, out IntPtr ppszIcon)
        { ppszIcon = ShellHelpers.Str(IconPath); return HResults.S_OK; }

    public int GetToolTip(IShellItemArray? psiItemArray, out IntPtr ppszInfotip)
        { ppszInfotip = IntPtr.Zero; return HResults.E_NOTIMPL; }

    public int GetCanonicalName(out Guid pguidCommandName)
        { pguidCommandName = Guid.Empty; return HResults.S_OK; }

    public virtual int GetState(IShellItemArray? psiItemArray, bool fOkToBeSlow, out uint pCmdState)
    {
        var paths = ShellHelpers.GetPaths(psiItemArray).ToArray();
        pCmdState = (uint)GetStateFor(paths);
        return HResults.S_OK;
    }

    public int Invoke(IShellItemArray? psiItemArray, IntPtr pbc)
    {
        try
        {
            var paths = ShellHelpers.GetPaths(psiItemArray).ToArray();
            Run(paths);
            return HResults.S_OK;
        }
        catch { return HResults.E_FAIL; }
    }

    public virtual int GetFlags(out uint pFlags) { pFlags = (uint)Flags; return HResults.S_OK; }

    public virtual int EnumSubCommands(out IEnumExplorerCommand ppEnum)
        { ppEnum = null!; return HResults.E_NOTIMPL; }

    // Spawn kis.exe in a console window so users can see the output
    protected static void RunKis(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName  = KisExe,
            UseShellExecute = true,   // launches with a console window
            CreateNoWindow  = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    // Use powershell to wrap an interactive verb (so it can prompt for password)
    protected static void RunVerb(string verbScript, string arg)
    {
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe");
        var script = Path.Combine(AssemblyDir, "verbs", verbScript);
        var psi = new ProcessStartInfo
        {
            FileName = ps,
            UseShellExecute = true,
            CreateNoWindow  = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }
}

// ─── Helpers ─────────────────────────────────────────────────────────────
internal static class PathHelpers
{
    public static bool IsKisArchive(string path) =>
        path.EndsWith(".kis", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".kes", StringComparison.OrdinalIgnoreCase);

    public static bool AllAreKisArchives(string[] paths) =>
        paths.Length > 0 && paths.All(IsKisArchive);

    public static bool AnyArePaths(string[] paths) => paths.Length > 0;
}

// ─── Sub-commands ────────────────────────────────────────────────────────

[ComVisible(true), Guid("3D1B7E11-1234-4001-9C0A-2A0B0E1F0001")]
public sealed class CompressCommand : ExplorerCommandBase
{
    public override string Title => "Compress to .kis";
    public override ExpCmdState GetStateFor(string[] paths) =>
        PathHelpers.AnyArePaths(paths) ? ExpCmdState.Enabled : ExpCmdState.Hidden;

    public override void Run(string[] paths)
    {
        foreach (var p in paths)
        {
            string full = Path.GetFullPath(p);
            string outPath = Directory.Exists(full)
                ? full.TrimEnd('\\') + ".kis"
                : Path.ChangeExtension(full, ".kis");
            RunKis(new[] { "create", outPath, full, "-c", "lzma" });
        }
    }
}

[ComVisible(true), Guid("3D1B7E11-1234-4001-9C0A-2A0B0E1F0002")]
public sealed class CompressEncryptedCommand : ExplorerCommandBase
{
    public override string Title => "Compress to .kis (encrypted)…";
    public override ExpCmdState GetStateFor(string[] paths) =>
        PathHelpers.AnyArePaths(paths) ? ExpCmdState.Enabled : ExpCmdState.Hidden;

    public override void Run(string[] paths)
    {
        // Delegate to the existing PowerShell verb — it handles the password prompt.
        foreach (var p in paths)
            RunVerb("compress-encrypted.ps1", Path.GetFullPath(p));
    }
}

[ComVisible(true), Guid("3D1B7E11-1234-4001-9C0A-2A0B0E1F0003")]
public sealed class ExtractHereCommand : ExplorerCommandBase
{
    public override string Title => "Extract here";
    public override ExpCmdState GetStateFor(string[] paths) =>
        PathHelpers.AllAreKisArchives(paths) ? ExpCmdState.Enabled : ExpCmdState.Hidden;

    public override void Run(string[] paths)
    {
        foreach (var p in paths)
            RunVerb("extract-here.ps1", Path.GetFullPath(p));
    }
}

[ComVisible(true), Guid("3D1B7E11-1234-4001-9C0A-2A0B0E1F0004")]
public sealed class VerifyCommand : ExplorerCommandBase
{
    public override string Title => "Verify integrity";
    public override ExpCmdState GetStateFor(string[] paths) =>
        PathHelpers.AllAreKisArchives(paths) ? ExpCmdState.Enabled : ExpCmdState.Hidden;

    public override void Run(string[] paths)
    {
        foreach (var p in paths)
            RunVerb("verify.ps1", Path.GetFullPath(p));
    }
}

[ComVisible(true), Guid("3D1B7E11-1234-4001-9C0A-2A0B0E1F0005")]
public sealed class InfoCommand : ExplorerCommandBase
{
    public override string Title => "Show info";
    public override ExpCmdState GetStateFor(string[] paths) =>
        PathHelpers.AllAreKisArchives(paths) ? ExpCmdState.Enabled : ExpCmdState.Hidden;

    public override void Run(string[] paths)
    {
        foreach (var p in paths)
            RunVerb("info.ps1", Path.GetFullPath(p));
    }
}

// ─── Top-level "KIS" entry that hosts the submenu ────────────────────────
[ComVisible(true), Guid("3D1B7E11-1234-4000-9C0A-2A0B0E1F0000")]
public sealed class KisRootCommand : ExplorerCommandBase
{
    public override string Title => "KIS";
    public override ExpCmdFlags Flags => ExpCmdFlags.HasSubCommands;

    public override int GetState(IShellItemArray? psiItemArray, bool fOkToBeSlow, out uint pCmdState)
    {
        // Always enabled — visibility of children is governed by their own GetState.
        pCmdState = (uint)ExpCmdState.Enabled;
        return HResults.S_OK;
    }

    public override void Run(string[] paths) { /* root only — never invoked directly */ }

    public override int EnumSubCommands(out IEnumExplorerCommand ppEnum)
    {
        ppEnum = new SubCommandEnum(new IExplorerCommand[]
        {
            new CompressCommand(),
            new CompressEncryptedCommand(),
            new ExtractHereCommand(),
            new VerifyCommand(),
            new InfoCommand(),
        });
        return HResults.S_OK;
    }
}

// ─── IEnumExplorerCommand implementation ─────────────────────────────────
internal sealed class SubCommandEnum : IEnumExplorerCommand
{
    private readonly IExplorerCommand[] _items;
    private int _i;
    public SubCommandEnum(IExplorerCommand[] items) { _items = items; _i = 0; }

    public int Next(uint celt, IExplorerCommand[] pUICommand, out uint pceltFetched)
    {
        pceltFetched = 0;
        for (uint k = 0; k < celt && _i < _items.Length; k++, _i++, pceltFetched++)
            pUICommand[k] = _items[_i];
        return pceltFetched == celt ? HResults.S_OK : HResults.S_FALSE;
    }
    public int Skip(uint celt) { _i += (int)celt; return HResults.S_OK; }
    public int Reset() { _i = 0; return HResults.S_OK; }
    public int Clone(out IEnumExplorerCommand ppenum) { ppenum = new SubCommandEnum(_items); return HResults.S_OK; }
}
