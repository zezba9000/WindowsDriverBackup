using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsDriverBackup
{
    internal sealed class DriverPackages
    {
        private readonly string windows;
        private readonly string system;
        private readonly string pnpUtil;
        private readonly string dism;
        private readonly bool hasDriverStore = Environment.OSVersion.Version.Major >= 6;

        internal DriverPackages(string windows, string system)
        {
            this.windows = windows;
            this.system = system;
            pnpUtil = Path.Combine(system, "pnputil.exe");
            dism = Path.Combine(system, "dism.exe");
        }

        internal void Export(string destination, OperationResult result, DriverBackupService.Progress progress)
        {
            if (!File.Exists(pnpUtil))
            {
                result.Lines.Add("PnPUtil was unavailable at " + pnpUtil + ".");
            }
            else if (!WindowsTools.HasModernPnPUtil(pnpUtil))
            {
                result.Lines.Add("The installed PnPUtil predates the driver export command.");
            }
            else if (TryExport(pnpUtil, "/export-driver * ", destination, result, progress))
            {
                result.Lines.Add("Driver packages exported with PnPUtil.");
                return;
            }

            if (File.Exists(dism) &&
                TryExport(dism, "/Online /Export-Driver /Destination:", destination, result, progress))
            {
                result.Lines.Add("Driver packages exported with DISM.");
                return;
            }

            string scratch = CreateExportDirectory(destination);
            try
            {
                progress("Exporting packages with Windows SetupAPI...");
                string target = Path.Combine(scratch, "DriverBackup");
                Directory.CreateDirectory(target);
                int exported = ExportWithSetupApi(target, scratch, result);
                Directory.Move(target, destination);
                result.Lines.Add("Exported " + exported + " driver packages using Windows SetupAPI.");
                if (!hasDriverStore)
                    result.Lines.Add("Legacy export uses files still available on this Windows installation.");
            }
            finally
            {
                RemoveExportDirectory(scratch, result);
            }
        }

        private static bool TryExport(string executable, string arguments, string destination,
            OperationResult result, DriverBackupService.Progress progress)
        {
            string name = Path.GetFileNameWithoutExtension(executable);
            string target = CreateExportDirectory(destination);
            try
            {
                progress("Exporting driver packages with " + name + "...");
                int code = WindowsTools.RunInConsole(executable, arguments + WindowsTools.QuotePath(target));
                if (code != 0)
                {
                    string exitCode = code < 0 ? "0x" + code.ToString("X8") : code.ToString();
                    result.Warn(name + " export failed (exit code " + exitCode + "). " + new Win32Exception(code).Message);
                    return false;
                }

                Directory.Move(target, destination);
                target = null;
                return true;
            }
            catch (Win32Exception ex)
            {
                result.Warn(name + " could not run (Windows error " + ex.NativeErrorCode + "). " + ex.Message);
                return false;
            }
            finally
            {
                if (target != null)
                    RemoveExportDirectory(target, result);
            }
        }

        private static string CreateExportDirectory(string destination)
        {
            // Keep the temporary path as short as DriverBackup for tools that are sensitive to path length.
            string path = Path.Combine(Path.GetDirectoryName(destination), ".drv" + Guid.NewGuid().ToString("N").Substring(0, 8));
            WindowsTools.CreateNewDirectory(path);
            return path;
        }

        private static void RemoveExportDirectory(string path, OperationResult result)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                result.Warn("Could not clean up " + path + " — " + ex.Message);
            }
        }

        private int ExportWithSetupApi(string destination, string scratch, OperationResult result)
        {
            LegacyDriverPackages legacy = hasDriverStore ? null : new LegacyDriverPackages(windows, system);
            int exported = 0;
            foreach (string publishedInf in Directory.GetFiles(Path.Combine(windows, "INF"), "oem*.inf"))
            {
                string name = Path.GetFileName(publishedInf);
                if (!Regex.IsMatch(name, @"^oem[0-9]+\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    continue;
                string packageName = Path.GetFileNameWithoutExtension(name);
                string target = Path.Combine(scratch, packageName);
                try
                {
                    if (legacy == null)
                        ExportFromDriverStore(publishedInf, target);
                    else
                        legacy.Export(publishedInf, target);
                    Directory.Move(target, Path.Combine(destination, packageName));
                    exported++;
                }
                catch (Exception ex)
                {
                    result.Warn(name + " — " + ex.Message);
                }
            }
            return exported;
        }

        private void ExportFromDriverStore(string publishedInf, string target)
        {
            StringBuilder originalInf = new StringBuilder(260);
            if (!SetupGetInfDriverStoreLocation(publishedInf, IntPtr.Zero, null, originalInf,
                originalInf.Capacity, IntPtr.Zero))
                throw new Win32Exception();

            string directory = Path.GetDirectoryName(originalInf.ToString());
            string nativePrefix = Path.Combine(windows, "System32") + "\\";
            if (directory.StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase))
                directory = Path.Combine(system, directory.Substring(nativePrefix.Length));
            DriverBackupService.CopyDirectory(directory, target);
        }

        internal void Install(List<string> infFiles, OperationResult result, DriverBackupService.Progress progress)
        {
            bool hasPnPUtil = File.Exists(pnpUtil);
            bool modernPnPUtil = hasPnPUtil && WindowsTools.HasModernPnPUtil(pnpUtil);
            int completed = 0;
            int alreadyOrUnmatched = 0;
            bool needsReboot = false;

            // DISM's Add-Driver applies to offline Windows images. Use the native APIs for a running system.
            for (int index = 0; index < infFiles.Count; index++)
            {
                string inf = infFiles[index];
                progress("Restoring " + (index + 1) + " of " + infFiles.Count + " — " + Path.GetFileName(inf));
                try
                {
                    if (hasPnPUtil)
                    {
                        string arguments = modernPnPUtil
                            ? "/add-driver " + WindowsTools.QuotePath(inf) + " /install"
                            : "-i -a " + WindowsTools.QuotePath(inf);
                        int code = WindowsTools.RunInConsole(pnpUtil, arguments);
                        if (code == WindowsTools.NoMatchingDevice)
                        {
                            alreadyOrUnmatched++;
                            continue;
                        }
                        if (code != 0 && code != WindowsTools.RebootRequired)
                            throw new IOException("PnPUtil returned exit code " + code + ".");
                        needsReboot |= code == WindowsTools.RebootRequired;
                    }
                    else if (hasDriverStore)
                    {
                        bool reboot;
                        if (!DiInstallDriver(IntPtr.Zero, inf, 0, out reboot))
                            throw new Win32Exception();
                        needsReboot |= reboot;
                    }
                    else
                    {
                        needsReboot |= LegacyDriverPackages.Install(inf);
                    }
                    completed++;
                }
                catch (Exception ex)
                {
                    result.Warn(inf + " — " + ex.Message);
                }
            }

            result.Lines.Add("Processed " + infFiles.Count + " driver packages using " +
                (hasPnPUtil ? "PnPUtil." : "Windows driver-installation APIs."));
            result.Lines.Add("Staged or installed " + completed + " driver packages.");
            if (alreadyOrUnmatched != 0)
                result.Lines.Add("Windows found no matching device or preferred an installed driver for " +
                    alreadyOrUnmatched + " packages.");
            if (!hasDriverStore)
                result.Lines.Add("Keep this backup accessible so Windows can use it as the source for staged drivers.");
            if (needsReboot)
                result.Lines.Add("Windows needs a restart to finish installing drivers.");
        }

        [DllImport("setupapi.dll", EntryPoint = "SetupGetInfDriverStoreLocationW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern bool SetupGetInfDriverStoreLocation(string fileName, IntPtr alternatePlatform,
            string locale, StringBuilder returnBuffer, int bufferSize, IntPtr requiredSize);

        [DllImport("newdev.dll", EntryPoint = "DiInstallDriverW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern bool DiInstallDriver(IntPtr parent, string infPath, uint flags, out bool needReboot);
    }
}
