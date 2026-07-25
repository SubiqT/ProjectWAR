using System;
using System.IO;
using WarZoneLib;

namespace ClientPatcher
{
    /// <summary>
    /// Points a stock WAR client at a private server, which the Windows only
    /// launcher normally does. Two things are needed:
    ///
    ///   1. mythloginserviceconfig.xml, which carries the login server address,
    ///      lives inside data.myp rather than on disk. Dropping a copy next to
    ///      WAR.exe has no effect.
    ///   2. WAR.exe encrypts its packets unless three sites are patched.
    ///
    /// Mirrors Launcher/NetWork/Client.cs UpdateWarData() and patchExe().
    /// </summary>
    internal static class Program
    {
        /// <summary>Hash of mythloginserviceconfig.xml within data.myp.</summary>
        private const long LoginConfigHash = 0x0B3E7AC0C6762BF7;

        private const int ImageBase = 0x00400000;

        private static readonly (int Address, byte[] Bytes, string Description)[] ExePatches =
        {
            (0x00957FBE + 3, new byte[] { 0x01 }, "send packets unencrypted"),
            (0x009580CB, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x57, 0x8B, 0xF8, 0xEB, 0x32 }, "skip decrypt check 1"),
            (0x0095814B, new byte[] { 0x90, 0x90, 0x90, 0x90, 0xEB, 0x08 }, "skip decrypt check 2")
        };

        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("usage: ClientPatcher <client directory> [login config xml]");
                Console.Error.WriteLine("       Without a config file the client is only inspected.");
                return 2;
            }

            var clientDirectory = new DirectoryInfo(args[0]);
            string configPath = args.Length > 1 ? args[1] : null;

            if (!clientDirectory.Exists)
            {
                Console.Error.WriteLine($"No such directory: {clientDirectory.FullName}");
                return 1;
            }

            try
            {
                ReportOrPatchExe(Path.Combine(clientDirectory.FullName, "WAR.exe"), configPath != null);
                ReportOrPatchLoginConfig(Path.Combine(clientDirectory.FullName, "data.myp"), configPath);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
                return 1;
            }

            return 0;
        }

        private static void ReportOrPatchExe(string exePath, bool apply)
        {
            if (!File.Exists(exePath))
                throw new FileNotFoundException("WAR.exe not found", exePath);

            byte[] data = File.ReadAllBytes(exePath);
            bool changed = false;

            foreach ((int address, byte[] patch, string description) in ExePatches)
            {
                int offset = address - ImageBase;

                if (offset + patch.Length > data.Length)
                    throw new InvalidDataException($"WAR.exe is too small for offset 0x{offset:X}.");

                bool patched = true;

                for (int i = 0; i < patch.Length; i++)
                {
                    if (data[offset + i] != patch[i])
                        patched = false;
                }

                Console.WriteLine($"WAR.exe  {description,-26} 0x{offset:X6}  {(patched ? "already patched" : "needs patch")}");

                if (patched || !apply)
                    continue;

                Buffer.BlockCopy(patch, 0, data, offset, patch.Length);
                changed = true;
            }

            if (!changed)
                return;

            string backup = exePath + ".orig";

            if (!File.Exists(backup))
            {
                File.Copy(exePath, backup);
                Console.WriteLine($"WAR.exe  original saved as {Path.GetFileName(backup)}");
            }

            File.WriteAllBytes(exePath, data);
            Console.WriteLine("WAR.exe  patched");
        }

        /// <summary>
        /// Finds the login config by content. Clients built by different launchers
        /// store it under different hashes, so the launcher's constant is only a hint.
        /// </summary>
        private static long? FindLoginConfigEntry(MYP archive)
        {
            const string Marker = "MythLoginServiceConfig";
            const int MaxSize = 64 * 1024;

            foreach (var pair in archive.Enteries)
            {
                if (pair.Value.UnCompressedSize == 0 || pair.Value.UnCompressedSize > MaxSize)
                    continue;

                byte[] content;

                try
                {
                    content = archive.ReadFile(pair.Value);
                }
                catch (Exception)
                {
                    continue;
                }

                if (content == null || content.Length == 0)
                    continue;

                if (System.Text.Encoding.ASCII.GetString(content).Contains(Marker))
                    return pair.Key;
            }

            return null;
        }

        private static void ReportOrPatchLoginConfig(string archivePath, string configPath)
        {
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("data.myp not found", archivePath);

            FileAccess access = configPath == null ? FileAccess.Read : FileAccess.ReadWrite;

            using (FileStream stream = File.Open(archivePath, FileMode.Open, access))
            using (var archive = new MYP(MythicPackage.ART, stream))
            {
                bool present = archive.Enteries.ContainsKey(LoginConfigHash);
                long hash = LoginConfigHash;

                if (!present)
                {
                    long? found = FindLoginConfigEntry(archive);

                    if (found == null)
                        throw new InvalidDataException(
                            $"data.myp has no login config: neither hash 0x{LoginConfigHash:X} nor any entry containing MythLoginServiceConfig.");

                    hash = found.Value;
                    Console.WriteLine($"data.myp entry 0x{LoginConfigHash:X} absent, found login config under 0x{hash:X} instead");
                }
                else
                {
                    Console.WriteLine($"data.myp entry 0x{hash:X} present, {archive.Enteries.Count} entries total");
                }

                if (configPath == null)
                {
                    byte[] current = archive.ReadFile(archive.Enteries[hash]);
                    Console.WriteLine($"data.myp current login config, {current.Length} bytes:");
                    Console.WriteLine(System.Text.Encoding.UTF8.GetString(current));

                    return;
                }

                byte[] config = File.ReadAllBytes(configPath);
                archive.UpdateFile(hash, config);
                archive.Save();

                Console.WriteLine($"data.myp login config replaced with {configPath} ({config.Length} bytes)");
            }
        }
    }
}
