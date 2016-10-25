using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.DotNet.Cli.Build.Framework;
using Microsoft.DotNet.InternalAbstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.DotNet.Cli.Build;
using static Microsoft.DotNet.Cli.Build.Framework.BuildHelpers;
using static Microsoft.DotNet.Cli.Build.FS;

namespace Microsoft.DotNet.Host.Build
{
    public class CompileTargets
    {
        public static readonly bool IsWinx86 = CurrentPlatform.IsWindows && CurrentArchitecture.Isx86;
        public const string SharedFrameworkName = "Microsoft.NETCore.App";

        public static readonly Dictionary<string, string> HostPackageSupportedRids = new Dictionary<string, string>()
        {
            // Key: Current platform RID. Value: The actual publishable (non-dummy) package name produced by the build system for this RID.
            { "win7-x64", "win7-x64" },
            { "win7-x86", "win7-x86" },
            { "win10-arm64", "win10-arm64" },
            { "osx.10.10-x64", "osx.10.10-x64" },
            { "osx.10.11-x64", "osx.10.10-x64" },
            { "ubuntu.14.04-x64", "ubuntu.14.04-x64" },
            { "ubuntu.16.04-x64", "ubuntu.16.04-x64" },
            { "ubuntu.16.10-x64", "ubuntu.16.10-x64" },
            { "centos.7-x64", "rhel.7-x64" },
            { "rhel.7-x64", "rhel.7-x64" },
            { "rhel.7.2-x64", "rhel.7-x64" },
            { "debian.8-x64", "debian.8-x64" },
            { "fedora.23-x64", "fedora.23-x64" },
            { "opensuse.13.2-x64", "opensuse.13.2-x64" },
            { "opensuse.42.1-x64", "opensuse.42.1-x64" }
        };

        [Target(nameof(PrepareTargets.Init),
            nameof(PublishSharedFrameworkAndSharedHost))]
        public static BuildTargetResult Compile(BuildTargetContext c)
        {
            return c.Success();
        }

        private static void GetVersionResourceForAssembly(
            string assemblyName,
            HostVersion.VerInfo hostVer,
            string commitHash,
            string tempRcDirectory)
        {
            var semVer = hostVer.ToString();
            var majorVersion = hostVer.Major;
            var minorVersion = hostVer.Minor;
            var patchVersion = hostVer.Patch;
            var buildNumberMajor = hostVer.VerRsrcBuildMajor;
            var buildNumberMinor = hostVer.VerRsrcBuildMinor;
            var buildDetails = $"{semVer}, {commitHash} built by: {System.Environment.MachineName}, UTC: {DateTime.UtcNow.ToString()}";
            var rcContent = $@"
#include <Windows.h>

#ifndef VER_COMPANYNAME_STR
#define VER_COMPANYNAME_STR         ""Microsoft Corporation""
#endif
#ifndef VER_FILEDESCRIPTION_STR
#define VER_FILEDESCRIPTION_STR     ""{assemblyName}""
#endif
#ifndef VER_INTERNALNAME_STR
#define VER_INTERNALNAME_STR        VER_FILEDESCRIPTION_STR
#endif
#ifndef VER_ORIGINALFILENAME_STR
#define VER_ORIGINALFILENAME_STR    VER_FILEDESCRIPTION_STR
#endif
#ifndef VER_PRODUCTNAME_STR
#define VER_PRODUCTNAME_STR         ""Microsoft\xae .NET Core Framework"";
#endif
#undef VER_PRODUCTVERSION
#define VER_PRODUCTVERSION          {majorVersion},{minorVersion},{patchVersion},{buildNumberMajor}
#undef VER_PRODUCTVERSION_STR
#define VER_PRODUCTVERSION_STR      ""{buildDetails}""
#undef VER_FILEVERSION
#define VER_FILEVERSION             {majorVersion},{minorVersion},{patchVersion},{buildNumberMajor}
#undef VER_FILEVERSION_STR
#define VER_FILEVERSION_STR         ""{majorVersion},{minorVersion},{buildNumberMajor},{buildNumberMinor},{buildDetails}"";
#ifndef VER_LEGALCOPYRIGHT_STR
#define VER_LEGALCOPYRIGHT_STR      ""\xa9 Microsoft Corporation.  All rights reserved."";
#endif
#ifndef VER_DEBUG
#ifdef DEBUG
#define VER_DEBUG                   VS_FF_DEBUG
#else
#define VER_DEBUG                   0
#endif
#endif
";
            var tempRcHdrDir = Path.Combine(tempRcDirectory, assemblyName);
            Mkdirp(tempRcHdrDir);
            var tempRcHdrFile = Path.Combine(tempRcHdrDir, "version_info.h");
            File.WriteAllText(tempRcHdrFile, rcContent);
        }

        public static string GenerateVersionResource(BuildTargetContext c)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return null;
            }

            var tempRcDirectory = Path.Combine(Dirs.Intermediate, "hostResourceFiles");
            Rmdir(tempRcDirectory);
            Mkdirp(tempRcDirectory);

            var hostVersion = c.BuildContext.Get<HostVersion>("HostVersion");
            var commitHash = c.BuildContext.Get<string>("CommitHash");
            foreach (var binary in hostVersion.LatestHostBinaries)
            {
                GetVersionResourceForAssembly(binary.Key, binary.Value, commitHash, tempRcDirectory);
            }

            return tempRcDirectory;
        }
        [Target]
        public static BuildTargetResult GenerateMSbuildPropsFile(BuildTargetContext c)
        {
            var hostVersion = c.BuildContext.Get<HostVersion>("HostVersion");
            string platform = c.BuildContext.Get<string>("Platform");

            var msbuildProps = new StringBuilder();

            msbuildProps.AppendLine(@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">");
            msbuildProps.AppendLine("  <PropertyGroup>");
            msbuildProps.AppendLine($"    <Platform>{platform}</Platform>");
            msbuildProps.AppendLine($"    <DotNetHostBinDir>{Dirs.CorehostLatest}</DotNetHostBinDir>");
            msbuildProps.AppendLine($"    <HostVersion>{hostVersion.LatestHostPolicyVersion.WithoutSuffix}</HostVersion>");
            msbuildProps.AppendLine($"    <HostResolverVersion>{hostVersion.LatestHostFxrVersion.WithoutSuffix}</HostResolverVersion>");
            msbuildProps.AppendLine($"    <HostPolicyVersion>{hostVersion.LatestHostVersion.WithoutSuffix}</HostPolicyVersion>");
            msbuildProps.AppendLine($"    <BuildNumberMajor>{hostVersion.LatestHostBuildMajor}</BuildNumberMajor>");
            msbuildProps.AppendLine($"    <BuildNumberMinor>{hostVersion.LatestHostBuildMinor}</BuildNumberMinor>");
            msbuildProps.AppendLine($"    <PreReleaseLabel>{hostVersion.ReleaseSuffix}</PreReleaseLabel>");
            msbuildProps.AppendLine($"    <EnsureStableVersion>{hostVersion.EnsureStableVersion}</EnsureStableVersion>");
            msbuildProps.AppendLine("  </PropertyGroup>");
            msbuildProps.AppendLine("</Project>");

            File.WriteAllText(Path.Combine(c.BuildContext.BuildDirectory, "pkg", "version.props"), msbuildProps.ToString());

            return c.Success();
        }

        [Target(nameof(GenerateMSbuildPropsFile))]
        public static BuildTargetResult PackagePkgProjects(BuildTargetContext c)
        {
            var hostVersion = c.BuildContext.Get<HostVersion>("HostVersion");
            var hostNugetversion = hostVersion.LatestHostVersion.ToString();
            var content = $@"{c.BuildContext["CommitHash"]}{Environment.NewLine}{hostNugetversion}{Environment.NewLine}";
            var pkgDir = Path.Combine(c.BuildContext.BuildDirectory, "pkg");
            var packCmd = "pack." + (CurrentPlatform.IsWindows ? "cmd" : "sh");
            string rid = HostPackageSupportedRids[c.BuildContext.Get<string>("TargetRID")];
            var buildVersion = c.BuildContext.Get<BuildVersion>("BuildVersion");
            File.WriteAllText(Path.Combine(pkgDir, "version.txt"), content);

            // Pass the Major.Minor.Patch version to be used when generating packages
            Exec(Path.Combine(pkgDir, packCmd), buildVersion.ProductionVersion);

            foreach (var file in Directory.GetFiles(Path.Combine(pkgDir, "bin", "packages"), "*.nupkg"))
            {
                var fileName = Path.GetFileName(file);
                File.Copy(file, Path.Combine(Dirs.CorehostLocalPackages, fileName), true);

                Console.WriteLine($"Copying package {fileName} to artifacts directory {Dirs.CorehostLocalPackages}.");
            }

            bool fValidateHostPackages = c.BuildContext.Get<bool>("ValidateHostPackages");

            // Validate the generated host packages only if we are building them.
            if (fValidateHostPackages)
            {
                foreach (var item in hostVersion.LatestHostPackages)
                {
                    var fileFilter = $"runtime.{rid}.{item.Key}.{item.Value.ToString()}.nupkg";
                    if (Directory.GetFiles(Dirs.CorehostLocalPackages, fileFilter).Length == 0)
                    {
                        throw new BuildFailureException($"Nupkg for {fileFilter} was not created.");
                    }
                }
            }

            return c.Success();
        }

        [Target(nameof(PrepareTargets.Init))]
        public static BuildTargetResult RestoreLockedCoreHost(BuildTargetContext c)
        {
            var hostVersion = c.BuildContext.Get<HostVersion>("HostVersion");
            var lockedHostFxrVersion = hostVersion.LockedHostFxrVersion.ToString();
            string currentRid = HostPackageSupportedRids[c.BuildContext.Get<string>("TargetRID")];
            string framework = c.BuildContext.Get<string>("TargetFramework");

            string projectJson = $@"{{
  ""dependencies"": {{
      ""Microsoft.NETCore.DotNetHostResolver"" : ""{lockedHostFxrVersion}""
  }},
  ""frameworks"": {{
      ""{framework}"": {{}}
  }},
  ""runtimes"": {{
      ""{currentRid}"": {{}}
  }}
}}";
            var tempPjDirectory = Path.Combine(Dirs.Intermediate, "lockedHostTemp");
            FS.Rmdir(tempPjDirectory);
            Directory.CreateDirectory(tempPjDirectory);
            var tempPjFile = Path.Combine(tempPjDirectory, "project.json");
            File.WriteAllText(tempPjFile, projectJson);

            DotNetCli.Stage0.Restore("--verbosity", "verbose")
                .WorkingDirectory(tempPjDirectory)
                .Execute()
                .EnsureSuccessful();

            // Clean out before publishing locked binaries
            FS.Rmdir(Dirs.CorehostLocked);

            // Use specific RIDS for non-backward compatible platforms.
            DotNetCli.Stage0.Publish("--output", Dirs.CorehostLocked, "--no-build", "-r", currentRid)
                .WorkingDirectory(tempPjDirectory)
                .Execute()
                .EnsureSuccessful();

            return c.Success();
        }

        [Target(/*nameof(RestoreLockedCoreHost)*/)]
        public static BuildTargetResult PublishSharedFrameworkAndSharedHost(BuildTargetContext c)
        {
            var outputDir = Dirs.SharedFrameworkPublish;
            Utils.DeleteDirectory(outputDir);
            Directory.CreateDirectory(outputDir);

            var dotnetCli = DotNetCli.Stage0;
            var hostVersion = c.BuildContext.Get<HostVersion>("HostVersion");
            string sharedFrameworkNugetVersion = c.BuildContext.Get<string>("SharedFrameworkNugetVersion");
            string sharedFrameworkRid = c.BuildContext.Get<string>("TargetRID");
            string sharedFrameworkTarget = c.BuildContext.Get<string>("TargetFramework");
            var hostFxrVersion = hostVersion.LockedHostFxrVersion.ToString();
            var commitHash = c.BuildContext.Get<string>("CommitHash");

            var sharedFrameworkPublisher = new SharedFrameworkPublisher(
                Dirs.RepoRoot,
                Dirs.CorehostLocked,
                Dirs.CorehostLatest,
                Dirs.CorehostLocalPackages,
                sharedFrameworkNugetVersion,
                sharedFrameworkRid,
                sharedFrameworkTarget);

            sharedFrameworkPublisher.PublishSharedFramework(outputDir, commitHash, dotnetCli, hostFxrVersion);

            //sharedFrameworkPublisher.CopyMuxer(outputDir);
            //sharedFrameworkPublisher.CopyHostFxrToVersionedDirectory(outputDir, hostFxrVersion);

            return c.Success();
        }
    }
}
