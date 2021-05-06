// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.NET.Sdk.WorkloadManifestReader;

namespace Microsoft.DotNet.Build.Tasks.Workloads
{
    /// <summary>
    /// MSBuild task to generate a workload pack installer (MSI).
    /// </summary>
    public abstract class GenerateMsiBase : GenerateTaskBase
    {
        /// <summary>
        /// The name of the ComponentGroup generated by Heat.
        /// </summary>
        internal const string PackageContentComponentGroupName = "CG_PackageContent";

        /// <summary>
        /// The DirectoryReference to use as the parent for harvested directories.
        /// </summary>
        internal const string PackageContentDirectoryReference = "VersionDir";

        /// <summary>
        /// The UUID namespace to use for generating a product code.
        /// </summary>
        internal static readonly Guid ProductCodeNamespaceUuid = Guid.Parse("3B04DD8B-41C4-4DA3-9E49-4B69F11533A7");

        /// <summary>
        /// Static RTF text for inserting a EULA into the MSI. The license URL of the NuGet package will be embedded 
        /// as plain text since the text control used to render the MSI UI does not render hyperlinks even though RTF supports it.
        /// </summary>
        internal static readonly string Eula = @"{\rtf1\ansi\ansicpg1252\deff0\nouicompat\deflang1033{\fonttbl{\f0\fnil\fcharset0 Calibri;}}
{\colortbl ;\red0\green0\blue255;}
{\*\generator Riched20 10.0.19041}\viewkind4\uc1 
\pard\sa200\sl276\slmult1\f0\fs22\lang9 This software is licensed separately as set out in its accompanying license. By continuing, you also agree to that license (__LICENSE_URL__).\par
\par
}";

        /// <summary>
        /// An item group containing information to shorten the names of packages.
        /// </summary>
        public ITaskItem[] ShortNames
        {
            get;
            set;
        }

        /// <summary>
        /// The set of supported target platforms for MSIs.
        /// </summary>
        internal static readonly string[] SupportedPlatforms = new string[] { "x86", "x64", "arm64" };

        /// <summary>
        /// The UUID namesapce to use for generating an upgrade code.
        /// </summary>
        internal static readonly Guid UpgradeCodeNamespaceUuid = Guid.Parse("C743F81B-B3B5-4E77-9F6D-474EFF3A722C");

        /// <summary>
        /// Wildcard patterns of files that should be removed prior to harvesting the package contents.
        /// </summary>
        public ITaskItem[] ExcludeFiles
        {
            get;
            set;
        } = Array.Empty<ITaskItem>();

        /// <summary>
        /// Gets or sets whether a corresponding SWIX project should be generated for the MSI.
        /// </summary>
        public bool GenerateSwixAuthoring
        {
            get;
            set;
        }

        /// <summary>
        /// The set of MSIs that were produced.
        /// </summary>
        [Output]
        public ITaskItem[] Msis
        {
            get;
            protected set;
        }

        /// <summary>
        /// The path where the generated MSIs will be placed.
        /// </summary>
        [Required]
        public string OutputPath
        {
            get;
            set;
        }

        /// <summary>
        /// Semicolon sepearate list of ICEs to suppress.
        /// </summary>
        public string SuppressIces
        {
            get;
            set;
        }

        /// <summary>
        /// Generate a set of MSIs for the specified platforms using the specified NuGet package.
        /// </summary>
        /// <param name="sourcePackage">The NuGet package to convert into an MSI.</param>
        /// <param name="outputPath">The output path of the generated MSI.</param>
        /// <param name="platforms"></param>
        protected IEnumerable<ITaskItem> Generate(string sourcePackage, string swixPackageId, string outputPath, string installDir, params string[] platforms)
        {
            NugetPackage nupkg = new(sourcePackage, Log);
            List<TaskItem> msis = new();

            // MSI ProductName defaults to the package title and fallback to the package ID with a warning.
            string productName = nupkg.Title;

            if (string.IsNullOrWhiteSpace(nupkg.Title))
            {
                Log?.LogWarning($"'{sourcePackage}' should have a non-empty title. The MSI ProductName will be set to the package ID instead.");
                productName = nupkg.Id;
            }

            // Extract once, but harvest multiple times because some generated attributes are platform dependent. 
            string packageContentsDirectory = Path.Combine(PackageDirectory, $"{nupkg.Identity}");
            IEnumerable<string> exclusions = GetExlusionPatterns();
            Log.LogMessage(MessageImportance.Low, $"Extracting '{sourcePackage}' to '{packageContentsDirectory}'");
            nupkg.Extract(packageContentsDirectory, exclusions);

            foreach (string platform in platforms)
            {
                // Extract the MSI template and add it to the list of source files.
                List<string> sourceFiles = new();
                string msiSourcePath = Path.Combine(MsiDirectory, $"{nupkg.Id}", $"{nupkg.Version}", platform);
                sourceFiles.Add(EmbeddedTemplates.Extract("DependencyProvider.wxs", msiSourcePath));
                sourceFiles.Add(EmbeddedTemplates.Extract("Directories.wxs", msiSourcePath));
                sourceFiles.Add(EmbeddedTemplates.Extract("Product.wxs", msiSourcePath));
                sourceFiles.Add(EmbeddedTemplates.Extract("Registry.wxs", msiSourcePath));

                string EulaRtfPath = Path.Combine(msiSourcePath, "eula.rtf");
                File.WriteAllText(EulaRtfPath, Eula.Replace("__LICENSE_URL__", nupkg.LicenseUrl));
                EmbeddedTemplates.Extract("Variables.wxi", msiSourcePath);

                // Harvest the package contents and add it to the source files we need to compile.
                string packageContentWxs = Path.Combine(msiSourcePath, "PackageContent.wxs");
                sourceFiles.Add(packageContentWxs);

                HarvestToolTask heat = new(BuildEngine, WixToolsetPath)
                {
                    ComponentGroupName = PackageContentComponentGroupName,
                    DirectoryReference = PackageContentDirectoryReference,
                    OutputFile = packageContentWxs,
                    Platform = platform,
                    SourceDirectory = packageContentsDirectory
                };

                if (!heat.Execute())
                {
                    throw new Exception($"Failed to harvest package contents.");
                }

                // Compile the MSI sources
                string candleIntermediateOutputPath = Path.Combine(IntermediateBaseOutputPath, "wixobj",
                    $"{nupkg.Id}", $"{nupkg.Version}", platform);

                CompileToolTask candle = new(BuildEngine, WixToolsetPath)
                {
                    // Candle expects the output path to end with a single '\'
                    OutputPath = candleIntermediateOutputPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    SourceFiles = sourceFiles,
                    Arch = platform
                };

                // Configure preprocessor definitions. 
                string manufacturer = "Microsoft Corporation";

                if (!string.IsNullOrWhiteSpace(nupkg.Authors) && (nupkg.Authors.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0))
                {
                    manufacturer = nupkg.Authors;
                }
                Log.LogMessage(MessageImportance.Low, $"Setting Manufacturer to '{manufacturer}'");

                candle.PreprocessorDefinitions.AddRange(GetPreprocessorDefinitions(nupkg, platform));
                candle.PreprocessorDefinitions.Add($@"InstallDir={installDir}");
                candle.PreprocessorDefinitions.Add($@"ProductName={productName}");
                candle.PreprocessorDefinitions.Add($@"Platform={platform}");
                candle.PreprocessorDefinitions.Add($@"SourceDir={packageContentsDirectory}");
                candle.PreprocessorDefinitions.Add($@"Manufacturer={manufacturer}");
                candle.PreprocessorDefinitions.Add($@"EulaRtf={EulaRtfPath}");

                // Compiler extension to process dependency provider authoring for package reference counting.
                candle.Extensions.Add("WixDependencyExtension");

                if (!candle.Execute())
                {
                    throw new Exception($"Failed to compile MSI.");
                }

                // Link the MSI. The generated filename contains a the semantic version (excluding build metadata) and platform. 
                // If the source package already contains a platform, e.g. an aliased package that has a RID, then we don't add
                // the platform again.

                string shortPackageName = Path.GetFileNameWithoutExtension(sourcePackage).Replace(ShortNames);

                string outputFile = sourcePackage.Contains(platform) ?
                    Path.Combine(OutputPath, shortPackageName + ".msi") :
                    Path.Combine(OutputPath, shortPackageName + $"-{platform}.msi");

                LinkToolTask light = new(BuildEngine, WixToolsetPath)
                {
                    OutputFile = Path.Combine(OutputPath, outputFile),
                    SourceFiles = Directory.EnumerateFiles(candleIntermediateOutputPath, "*.wixobj"),
                    SuppressIces = this.SuppressIces
                };

                // Add WiX extensions
                light.Extensions.Add("WixDependencyExtension");
                light.Extensions.Add("WixUIExtension");

                if (!light.Execute())
                {
                    throw new Exception($"Failed to link MSI.");
                }

                TaskItem msi = new(light.OutputFile);
                msi.SetMetadata(Metadata.Platform, platform);
                msi.SetMetadata(Metadata.Version, nupkg.ProductVersion);

                if (GenerateSwixAuthoring)
                {
                    string swixProject = GenerateSwixPackageAuthoring(light.OutputFile,
                        !string.IsNullOrWhiteSpace(swixPackageId) ? swixPackageId :
                        $"{nupkg.Id.Replace(ShortNames)}.{nupkg.Version}", platform);

                    if (!string.IsNullOrWhiteSpace(swixProject))
                    {
                        msi.SetMetadata(Metadata.SwixProject, swixProject);
                    }
                }

                msis.Add(msi);
            }

            return msis;
        }

        private string GenerateSwixPackageAuthoring(string msiPath, string packageId, string platform)
        {
            GenerateVisualStudioMsiPackageProject swixTask = new()
            {
                Chip = platform,
                IntermediateBaseOutputPath = this.IntermediateBaseOutputPath,
                PackageName = packageId,
                MsiPath = msiPath,
                BuildEngine = this.BuildEngine,
            };

            if (!swixTask.Execute())
            {
                Log.LogError($"Failed to generate SWIX authoring for '{msiPath}'");
            }

            return swixTask.SwixProject;
        }

        private IEnumerable<string> GetExlusionPatterns()
        {
            IEnumerable<string> patterns = ExcludeFiles.Select(
                e => Utils.ConvertToRegexPattern(e.ItemSpec)) ?? Enumerable.Empty<string>();

            foreach (string pattern in patterns)
            {
                Log.LogMessage(MessageImportance.Low, $"Adding exclusion pattern: {pattern}");
            }

            return patterns;
        }

        /// <summary>
        /// Generate a set of preprocessor variable definitions using the metadata.
        /// </summary>
        /// <returns>An enumerable containing package metadata converted to WiX preprocessor definitions.</returns>
        private IEnumerable<string> GetPreprocessorDefinitions(NugetPackage package, string platform)
        {
            yield return $@"PackageId={package.Id}";
            yield return $@"PackageVersion={package.Version}";
            yield return $@"ProductVersion={package.ProductVersion}";
            yield return $@"ProductCode={Utils.CreateUuid(ProductCodeNamespaceUuid, package.Identity.ToString() + $"{platform}"):B}";
            yield return $@"UpgradeCode={Utils.CreateUuid(UpgradeCodeNamespaceUuid, package.Identity.ToString() + $"{platform}"):B}";
        }

        /// <summary>
        /// Get the installation directory based on the kind of workload pack.
        /// </summary>
        /// <param name="kind">The workload pack kind.</param>
        /// <returns>The name of the root installation directory.</returns>
        internal static string GetInstallDir(WorkloadPackKind kind)
        {
            return kind switch
            {
                WorkloadPackKind.Framework or WorkloadPackKind.Sdk => "packs",
                WorkloadPackKind.Library => "library-packs",
                WorkloadPackKind.Template => "templates",
                WorkloadPackKind.Tool => "tool-packs",
                _ => throw new ArgumentException($"Unknown package kind: {kind}"),
            };
        }
    }
}