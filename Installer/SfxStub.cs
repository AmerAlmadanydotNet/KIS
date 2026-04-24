// KIS file Setup - Self-Extracting Installer Stub
// Compiled by CreateSetupPackage.ps1 into KISfile-Setup.exe
//
// File layout of the final EXE:
//   [SfxStub.exe bytes]
//   [Payload ZIP bytes]
//   [8 bytes  - int64 zipStart offset]
//   [11 bytes - "KISFILE_SFX"  magic]
//                 ^^^ 19-byte trailer total

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

class SfxStub
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int MessageBox(IntPtr hWnd, string text, string caption, uint uType);

    const int TRAILER = 19;   // 8 (int64) + 11 (magic)
    const string MAGIC = "KISFILE_SFX";

    static bool IsAdmin()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    [STAThread]
    static void Main()
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;

            // --------------------------------------------------
            // 1. Self-elevate if not already running as admin
            // --------------------------------------------------
            if (!IsAdmin())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = exePath,
                        Verb            = "runas",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // User cancelled the UAC prompt
                    MessageBox(IntPtr.Zero,
                        "Administrator rights are required to install KIS file.\n\nSetup will now exit.",
                        "KIS file Setup", 0x40 /* MB_ICONINFORMATION */);
                }
                return;
            }

            // --------------------------------------------------
            // 2. Read trailer to find the embedded ZIP
            // --------------------------------------------------
            long fileSize = new FileInfo(exePath).Length;

            if (fileSize < TRAILER + 22)
            {
                MessageBox(IntPtr.Zero,
                    "The installer file is corrupted (too small).\nPlease re-download.",
                    "KIS file Setup", 0x10);
                return;
            }

            byte[] trailerBuf = new byte[TRAILER];
            using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(-TRAILER, SeekOrigin.End);
                fs.Read(trailerBuf, 0, TRAILER);
            }

            long   zipStart = BitConverter.ToInt64(trailerBuf, 0);
            string magic    = Encoding.ASCII.GetString(trailerBuf, 8, 11);

            if (magic != MAGIC)
            {
                MessageBox(IntPtr.Zero,
                    "The installer file is corrupted (bad signature).\nPlease re-download.",
                    "KIS file Setup", 0x10);
                return;
            }

            int    zipLength = (int)(fileSize - TRAILER - zipStart);
            byte[] zipData   = new byte[zipLength];

            using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(zipStart, SeekOrigin.Begin);
                int done = 0;
                while (done < zipLength)
                {
                    int n = fs.Read(zipData, done, zipLength - done);
                    if (n == 0) break;
                    done += n;
                }
            }

            // --------------------------------------------------
            // 3. Extract ZIP to a unique temp folder
            // --------------------------------------------------
            string tempDir = Path.Combine(
                Path.GetTempPath(),
                "KISfile_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            Directory.CreateDirectory(tempDir);

            using (var ms  = new MemoryStream(zipData))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string dest   = Path.Combine(tempDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    string subDir = Path.GetDirectoryName(dest);

                    if (!string.IsNullOrEmpty(subDir) && !Directory.Exists(subDir))
                        Directory.CreateDirectory(subDir);

                    using (var src = entry.Open())
                    using (var dst = File.Create(dest))
                        src.CopyTo(dst);
                }
            }

            // --------------------------------------------------
            // 4. Run Install.ps1 in its own PowerShell window.
            //    We are already admin so Install.ps1's own UAC
            //    check will pass immediately.  Wait for it to
            //    finish so the temp folder is not deleted early.
            // --------------------------------------------------
            string ps1 = Path.Combine(tempDir, "Install.ps1");

            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = "powershell.exe",
                Arguments        = "-ExecutionPolicy Bypass -NoProfile -File \"" + ps1 + "\"",
                UseShellExecute  = true,
                WorkingDirectory = tempDir
            });

            if (proc != null) proc.WaitForExit();

            // Cleanup temp files (best-effort)
            try { Directory.Delete(tempDir, true); } catch { }
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero,
                "KIS file Setup error:\n\n" + ex.Message,
                "KIS file Setup", 0x10);
        }
    }
}
