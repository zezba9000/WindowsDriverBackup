using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsDriverBackup
{
    // Pre-Vista Windows keeps the installed files and published INF but has no full package store.
    internal sealed class LegacyDriverPackages
    {
        private const uint InfSpecIsHandle = 1;
        private const uint WindowsInfStyle = 2;
        private const uint SourceMediaPath = 1;
        private const uint SourceInfoPath = 1;
        private const int ErrorNoSuchDevice = unchecked((int)0xE000020B);
        private const string CatalogDirectory = @"CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}";

        private readonly string[] payloadDirectories;
        private readonly string[] catalogDirectories;
        private static readonly string Architecture = IntPtr.Size == 4 ? "x86" :
            (String.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "IA64",
                StringComparison.OrdinalIgnoreCase) ? "ia64" : "amd64");

        internal LegacyDriverPackages(string windows, string system)
        {
            payloadDirectories = new string[] {
                Path.Combine(system, "drivers"), system, Path.Combine(system, "dllcache"),
                Path.Combine(windows, "INF"), windows
            };
            catalogDirectories = new string[] { Path.Combine(system, CatalogDirectory), Path.Combine(windows, "INF") };
        }

        internal void Export(string publishedInf, string target)
        {
            IntPtr inf = OpenInf(publishedInf);
            try
            {
                OriginalFileInfo original = GetOriginalFileInfo(inf);
                Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                files.Add(GetPackagePath(target, original.InfName), publishedInf);

                if (!String.IsNullOrEmpty(original.CatalogName))
                {
                    string publishedCatalog = Path.ChangeExtension(Path.GetFileName(publishedInf), ".cat");
                    string catalog = FindFile(catalogDirectories, publishedCatalog, original.CatalogName);
                    if (catalog == null)
                        throw new IOException("The installed catalog " + original.CatalogName + " is missing.");
                    files.Add(GetPackagePath(target, original.CatalogName), catalog);
                }

                AddPayloadFiles(inf, "SourceDisksFiles." + Architecture, target, files);
                AddPayloadFiles(inf, "SourceDisksFiles", target, files);
                foreach (KeyValuePair<string, string> file in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Key));
                    File.Copy(file.Value, file.Key);
                }
            }
            finally
            {
                SetupCloseInfFile(inf);
            }
        }

        private void AddPayloadFiles(IntPtr inf, string section, string target, Dictionary<string, string> files)
        {
            InfContext line;
            if (!SetupFindFirstLine(inf, section, null, out line))
                return;

            do
            {
                string name = GetField(ref line, 0);
                StringBuilder subdirectory = new StringBuilder(260);
                StringBuilder mediaDirectory = new StringBuilder(260);
                uint disk;
                if (!SetupGetSourceFileLocation(inf, IntPtr.Zero, name, out disk, subdirectory,
                        subdirectory.Capacity, IntPtr.Zero) ||
                    !SetupGetSourceInfo(inf, disk, SourceInfoPath, mediaDirectory, mediaDirectory.Capacity, IntPtr.Zero))
                    throw new Win32Exception();

                string relative = Path.Combine(mediaDirectory.ToString().Trim('\\'),
                    Path.Combine(subdirectory.ToString().Trim('\\'), name));
                string targetFile = GetPackagePath(target, relative);
                if (files.ContainsKey(targetFile))
                    continue;
                string installed = FindFile(payloadDirectories, relative, Path.GetFileName(name));
                if (installed == null)
                    throw new IOException("The installed source file " + name + " is missing.");
                files.Add(targetFile, installed);
            }
            while (NextLine(ref line));
        }

        private static string FindFile(string[] directories, string name, string alternateName)
        {
            foreach (string directory in directories)
            {
                string file = GetPackagePath(directory, name);
                if (File.Exists(file))
                    return file;
            }
            foreach (string directory in directories)
            {
                string file = GetPackagePath(directory, alternateName);
                if (File.Exists(file))
                    return file;
            }
            return null;
        }

        private static string GetPackagePath(string root, string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (Path.IsPathRooted(relative) ||
                !path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The INF contains an invalid package path.");
            return path;
        }

        private static OriginalFileInfo GetOriginalFileInfo(IntPtr inf)
        {
            int length;
            if (!SetupGetInfInformation(inf, InfSpecIsHandle, IntPtr.Zero, 0, out length))
                throw new Win32Exception();

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                OriginalFileInfo original = new OriginalFileInfo();
                original.Size = Marshal.SizeOf(typeof(OriginalFileInfo));
                if (!SetupGetInfInformation(inf, InfSpecIsHandle, buffer, length, out length) ||
                    !SetupQueryInfOriginalFileInformation(buffer, 0, IntPtr.Zero, ref original))
                    throw new Win32Exception();
                if (String.IsNullOrEmpty(original.InfName))
                    throw new IOException("Windows could not identify the original INF name.");
                return original;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static bool Install(string path)
        {
            if (!SetupCopyOEMInf(path, null, SourceMediaPath, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            IntPtr inf = OpenInf(path);
            try
            {
                InfContext manufacturer;
                if (!SetupFindFirstLine(inf, "Manufacturer", null, out manufacturer))
                    return false;

                Dictionary<string, bool> attempted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                bool needsReboot = false;
                do
                {
                    string section = GetModelsSection(inf, ref manufacturer);
                    InfContext model;
                    if (section == null || !SetupFindFirstLine(inf, section, null, out model))
                        continue;
                    do
                    {
                        string id = GetField(ref model, 2);
                        if (String.IsNullOrEmpty(id) || attempted.ContainsKey(id))
                            continue;
                        attempted.Add(id, true);

                        bool reboot;
                        if (UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, id, path, 0, out reboot))
                        {
                            needsReboot |= reboot;
                        }
                        else
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error != ErrorNoSuchDevice && error != WindowsTools.NoMatchingDevice)
                                throw new Win32Exception(error);
                        }
                    }
                    while (NextLine(ref model));
                }
                while (NextLine(ref manufacturer));
                return needsReboot;
            }
            finally
            {
                SetupCloseInfFile(inf);
            }
        }

        private static string GetModelsSection(IntPtr inf, ref InfContext manufacturer)
        {
            try
            {
                StringBuilder section = new StringBuilder(256);
                return SetupDiGetActualModelsSection(ref manufacturer, IntPtr.Zero, section, section.Capacity,
                    IntPtr.Zero, IntPtr.Zero) ? section.ToString() : null;
            }
            catch (EntryPointNotFoundException)
            {
                string name = GetField(ref manufacturer, 1);
                if (SetupGetLineCount(inf, name + ".NT" + Architecture) > 0)
                    return name + ".NT" + Architecture;
                if (Architecture == "x86" && SetupGetLineCount(inf, name + ".NT") > 0)
                    return name + ".NT";
                return name;
            }
        }

        private static IntPtr OpenInf(string path)
        {
            IntPtr inf = SetupOpenInfFile(path, null, WindowsInfStyle, IntPtr.Zero);
            if (inf == new IntPtr(-1))
                throw new Win32Exception();
            return inf;
        }

        private static bool NextLine(ref InfContext line)
        {
            InfContext next;
            if (!SetupFindNextLine(ref line, out next))
                return false;
            line = next;
            return true;
        }

        private static string GetField(ref InfContext line, int number)
        {
            StringBuilder value = new StringBuilder(260);
            return SetupGetStringField(ref line, number, value, value.Capacity, IntPtr.Zero) ? value.ToString() : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InfContext
        {
            public IntPtr Inf, CurrentInf;
            public uint Section, Line;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OriginalFileInfo
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string InfName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string CatalogName;
        }

        [DllImport("setupapi.dll", EntryPoint = "SetupOpenInfFileW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr SetupOpenInfFile(string path, string infClass, uint style, IntPtr errorLine);

        [DllImport("setupapi.dll")]
        private static extern void SetupCloseInfFile(IntPtr inf);

        [DllImport("setupapi.dll", EntryPoint = "SetupGetInfInformationW", ExactSpelling = true, SetLastError = true)]
        private static extern bool SetupGetInfInformation(IntPtr inf, uint searchControl, IntPtr buffer,
            int bufferSize, out int requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupQueryInfOriginalFileInformationW", ExactSpelling = true,
            SetLastError = true)]
        private static extern bool SetupQueryInfOriginalFileInformation(IntPtr info, uint index,
            IntPtr alternatePlatform, ref OriginalFileInfo original);

        [DllImport("setupapi.dll", EntryPoint = "SetupFindFirstLineW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool SetupFindFirstLine(IntPtr inf, string section, string key, out InfContext line);

        [DllImport("setupapi.dll")]
        private static extern bool SetupFindNextLine(ref InfContext line, out InfContext next);

        [DllImport("setupapi.dll", EntryPoint = "SetupGetStringFieldW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool SetupGetStringField(ref InfContext line, int index, StringBuilder buffer,
            int length, IntPtr requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupGetSourceFileLocationW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern bool SetupGetSourceFileLocation(IntPtr inf, IntPtr line, string filename,
            out uint sourceId, StringBuilder sourcePath, int sourcePathSize, IntPtr requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupGetSourceInfoW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern bool SetupGetSourceInfo(IntPtr inf, uint sourceId, uint infoType,
            StringBuilder buffer, int bufferSize, IntPtr requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupGetLineCountW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetupGetLineCount(IntPtr inf, string section);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetActualModelsSectionW", CharSet = CharSet.Unicode,
            ExactSpelling = true)]
        private static extern bool SetupDiGetActualModelsSection(ref InfContext line, IntPtr platform,
            StringBuilder section, int sectionLength, IntPtr requiredSize, IntPtr reserved);

        [DllImport("setupapi.dll", EntryPoint = "SetupCopyOEMInfW", CharSet = CharSet.Unicode, ExactSpelling = true,
            SetLastError = true)]
        private static extern bool SetupCopyOEMInf(string inf, string sourceMedia, uint mediaType, uint copyStyle,
            IntPtr destination, int destinationSize, IntPtr requiredSize, IntPtr fileNameComponent);

        [DllImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr parent, string hardwareId, string inf,
            uint flags, out bool reboot);
    }
}
