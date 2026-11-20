using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WindowsDriverBackup
{
    internal sealed class DriverBackupService
    {
        internal delegate void Progress(string message);

        private const string PackageDirectoryName = "DriverBackup";
        private static readonly string[] DriverDirectories = {
            @"System32\drivers", @"System32\DriverStore", @"SysWOW64\drivers", @"SysWOW64\DriverStore"
        };

        private readonly string windows = WindowsTools.GetWindowsDirectory();

        internal OperationResult Backup(string destination, Progress progress)
        {
            destination = Path.GetFullPath(destination);
            string windowsPrefix = windows.TrimEnd('\\') + "\\";
            if ((destination.TrimEnd('\\') + "\\").StartsWith(windowsPrefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Select a backup destination outside the Windows directory.");

            string snapshots = Path.Combine(destination, "Windows");
            string packages = Path.Combine(destination, PackageDirectoryName);
            if (Directory.Exists(snapshots) || File.Exists(snapshots) ||
                Directory.Exists(packages) || File.Exists(packages))
                throw new IOException("This destination already contains a Windows or DriverBackup item. Select a new folder.");

            // Reserve this destination before copying anything, including across app instances.
            WindowsTools.CreateNewDirectory(snapshots);
            string system = WindowsTools.GetNativeSystemDirectory(windows);
            string robocopy = Path.Combine(system, "robocopy.exe");
            OperationResult result = new OperationResult();
            int copied = 0;
            int available = 0;

            foreach (string relative in DriverDirectories)
            {
                string source = relative.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(system, Path.GetFileName(relative))
                    : Path.Combine(windows, relative);

                try
                {
                    if (!GetDirectoryIfPresent(source))
                        continue;

                    available++;
                    progress("Copying Windows\\" + relative + "...");
                    string target = Path.Combine(snapshots, relative);
                    if (File.Exists(robocopy))
                    {
                        string arguments = WindowsTools.QuotePath(Path.Combine(windows, relative)) + " " +
                            WindowsTools.QuotePath(target) + " /E /COPY:DAT /XJ /R:1 /W:1";
                        int code = WindowsTools.RunInConsole(robocopy, arguments);
                        if (code >= 8)
                            code = WindowsTools.RunInConsole(robocopy, arguments + " /B");
                        if (code < 0 || code >= 8)
                            throw new IOException("Robocopy returned exit code " + code + ".");
                    }
                    else
                    {
                        CopyDirectory(source, target);
                    }
                    copied++;
                }
                catch (Exception ex)
                {
                    result.Warn(relative + " — " + ex.Message);
                }
            }

            result.Lines.Add("Copied " + copied + " of " + available + " available Windows driver folders.");
            DriverPackages driverPackages = new DriverPackages(windows, system);
            try
            {
                driverPackages.Export(packages, result, progress);
            }
            catch (Exception ex)
            {
                result.Warn("Package export failed — " + ex.Message);
            }
            result.Lines.Add(destination);
            return result;
        }

        internal OperationResult Restore(string source, Progress progress)
        {
            source = Path.GetFullPath(source);
            if (source.Length > Path.GetPathRoot(source).Length)
                source = source.TrimEnd('\\');
            string nested = Path.Combine(source, PackageDirectoryName);
            if (Directory.Exists(nested))
                source = nested;
            else if (!String.Equals(Path.GetFileName(source), PackageDirectoryName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Select the DriverBackup folder or the folder that contains it.");

            List<string> infFiles = new List<string>();
            FindInfFiles(source, infFiles);
            if (infFiles.Count == 0)
                throw new IOException("No driver package INF files were found in " + source + ".");

            infFiles.Sort(StringComparer.OrdinalIgnoreCase);
            OperationResult result = new OperationResult();
            DriverPackages driverPackages = new DriverPackages(windows, WindowsTools.GetNativeSystemDirectory(windows));
            driverPackages.Install(infFiles, result, progress);
            return result;
        }

        internal static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                if (!IsReparsePoint(file))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
            foreach (string directory in Directory.GetDirectories(source))
            {
                if (!IsReparsePoint(directory))
                    CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
            }
        }

        private static void FindInfFiles(string directory, List<string> files)
        {
            if (IsReparsePoint(directory))
                return;

            foreach (string path in Directory.GetFiles(directory, "*.inf"))
            {
                // .NET 2.0 wildcard matching can also return longer extensions.
                if (String.Equals(Path.GetExtension(path), ".inf", StringComparison.OrdinalIgnoreCase) &&
                    !IsReparsePoint(path))
                    files.Add(path);
            }
            foreach (string child in Directory.GetDirectories(directory))
                FindInfFiles(child, files);
        }

        internal static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private static bool GetDirectoryIfPresent(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
    }

    internal sealed class OperationResult
    {
        internal readonly List<string> Lines = new List<string>();
        private readonly List<string> warnings = new List<string>();

        internal bool HasWarnings { get { return warnings.Count != 0; } }

        internal void Warn(string message)
        {
            warnings.Add(message);
        }

        internal string GetMessage()
        {
            StringBuilder message = new StringBuilder(String.Join(Environment.NewLine, Lines.ToArray()));
            if (HasWarnings)
            {
                message.AppendLine().AppendLine().AppendLine("Warnings");
                int shown = Math.Min(warnings.Count, 8);
                for (int index = 0; index < shown; index++)
                    message.AppendLine(warnings[index]);
                if (shown < warnings.Count)
                    message.Append(warnings.Count - shown).Append(" additional warnings.");
            }
            return message.ToString().TrimEnd();
        }
    }
}
