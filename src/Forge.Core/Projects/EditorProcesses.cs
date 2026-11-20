using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Forge.Core.Projects;

/// <summary>One running Unreal Editor: its program file and its command line, either null when Windows would not say.</summary>
public sealed record EditorProcess(int Id, string? Program, string? CommandLine);

/// <summary>Whether a project is open in Unreal Editor, by the editors' command lines.</summary>
public enum EditorOnProject
{
    /// <summary>Every running editor's command line was read, and none names the project.</summary>
    No,
    /// <summary>An editor's command line names the project.</summary>
    Yes,
    /// <summary>An editor runs whose command line could not be read: it may have the project open.</summary>
    Unknown,
}

/// <summary>
/// The running UnrealEditor processes and what they opened. A project editor is started with the .uproject on its
/// command line (by the launcher, by a double click through UnrealVersionSelector, by "Open project"), so a
/// command line that names the file is the editor that holds it. Reading another process's command line needs
/// only PROCESS_QUERY_LIMITED_INFORMATION (NtQueryInformationProcess, class 60, Windows 8.1 and later); an
/// editor running elevated or as another user cannot be read, and that is said as Unknown, never as No.
/// Nothing here stops or changes a process.
/// </summary>
public static class EditorProcesses
{
    private static readonly string[] Names = ["UnrealEditor", "UnrealEditor-Cmd", "UE4Editor"];

    public static IReadOnlyList<EditorProcess> Running()
    {
        var list = new List<EditorProcess>();
        foreach (var name in Names)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch (InvalidOperationException) { continue; }
            foreach (var p in procs)
            {
                using (p)
                {
                    string? program = null;
                    try { program = p.MainModule?.FileName; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                    list.Add(new EditorProcess(p.Id, program, CommandLineOf(p.Id)));
                }
            }
        }
        return list;
    }

    /// <summary>Whether any of <paramref name="editors"/> has the project open. Pure, for the tests.</summary>
    public static EditorOnProject Check(string uproject, IReadOnlyList<EditorProcess> editors)
    {
        var unknown = false;
        var target = Normalize(Path.GetFullPath(uproject));
        foreach (var e in editors)
        {
            if (e.CommandLine is null) { unknown = true; continue; }
            if (Normalize(e.CommandLine).Contains(target, StringComparison.OrdinalIgnoreCase)) return EditorOnProject.Yes;
        }
        return unknown ? EditorOnProject.Unknown : EditorOnProject.No;
    }

    public static EditorOnProject Check(string uproject) => Check(uproject, Running());

    /// <summary>Whether an editor runs from this engine folder (it then has that engine's plugins loaded).</summary>
    public static bool RunsFrom(string engineFolder, IReadOnlyList<EditorProcess> editors)
    {
        var root = engineFolder.TrimEnd('\\', '/');
        return editors.Any(e => e.Program?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string Normalize(string s) => s.Replace('/', '\\').Replace("\\\\", "\\");

    // ── the command line ─────────────────────────────────────────────────

    private const int ProcessCommandLineInformation = 60;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The process's command line, or null when it cannot be read.</summary>
    public static string? CommandLineOf(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 1 << 20) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;
                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }, the text inside the same block.
                var length = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return length == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or AccessViolationException) { return null; }
        finally { CloseHandle(handle); }
    }
}
