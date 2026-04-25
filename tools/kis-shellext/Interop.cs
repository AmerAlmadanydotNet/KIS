// COM interop for Windows 11 modern context menu (IExplorerCommand).
// Reference: shobjidl_core.h
using System.Runtime.InteropServices;

namespace KIS.ShellExt;

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}

public enum SIGDN : uint
{
    NormalDisplay      = 0x00000000,
    ParentRelativeParsing = 0x80018001,
    DesktopAbsoluteParsing= 0x80028000,
    ParentRelativeEditing = 0x80031001,
    DesktopAbsoluteEditing= 0x8004c000,
    FileSysPath        = 0x80058000,
    Url                = 0x80068000,
    ParentRelativeForAddressBar = 0x8007c001,
    ParentRelative     = 0x80080001,
    ParentRelativeForUI= 0x80094001,
}

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid rbhid, ref Guid riid, out IntPtr ppvOut);
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
    [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
}

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IShellItemArray? psiItemArray, out IntPtr ppszName);
    [PreserveSig] int GetIcon(IShellItemArray? psiItemArray, out IntPtr ppszIcon);
    [PreserveSig] int GetToolTip(IShellItemArray? psiItemArray, out IntPtr ppszInfotip);
    [PreserveSig] int GetCanonicalName(out Guid pguidCommandName);
    [PreserveSig] int GetState(IShellItemArray? psiItemArray,
                               [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow,
                               out uint pCmdState);
    [PreserveSig] int Invoke(IShellItemArray? psiItemArray, IntPtr pbc);
    [PreserveSig] int GetFlags(out uint pFlags);
    [PreserveSig] int EnumSubCommands(out IEnumExplorerCommand ppEnum);
}

[ComImport, Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumExplorerCommand
{
    [PreserveSig] int Next(uint celt,
                           [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IExplorerCommand[] pUICommand,
                           out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumExplorerCommand ppenum);
}

public static class HResults
{
    public const int S_OK     = 0;
    public const int S_FALSE  = 1;
    public const int E_NOTIMPL= unchecked((int)0x80004001);
    public const int E_FAIL   = unchecked((int)0x80004005);
}

[Flags]
public enum ExpCmdState : uint
{
    Enabled  = 0,
    Disabled = 1,
    Hidden   = 2,
}

[Flags]
public enum ExpCmdFlags : uint
{
    Default       = 0,
    HasSubCommands= 0x10,
    HasSplitButton= 0x20,
    HideLabel     = 0x40,
    Separator     = 0x80,
}

internal static class ShellHelpers
{
    public static IntPtr Str(string s) => Marshal.StringToCoTaskMemUni(s);

    public static IEnumerable<string> GetPaths(IShellItemArray? items)
    {
        if (items == null) yield break;
        if (items.GetCount(out uint n) != 0 || n == 0) yield break;
        for (uint i = 0; i < n; i++)
        {
            if (items.GetItemAt(i, out var it) != 0 || it == null) continue;
            if (it.GetDisplayName(SIGDN.FileSysPath, out IntPtr p) == 0 && p != IntPtr.Zero)
            {
                var s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(s)) yield return s!;
            }
        }
    }
}
