﻿namespace BlazorRepl.Core.PackageInstallation
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using NuGet.Common;
    using NuGet.Configuration;
    using NuGet.DependencyResolver;
    using NuGet.Frameworks;
    using NuGet.LibraryModel;
    using NuGet.Packaging;
    using NuGet.Packaging.Core;
    using NuGet.Protocol.Core.Types;
    using NuGet.Versioning;

    public class NuGetRemoteDependencyProvider : IRemoteDependencyProvider
    {
        private static readonly ConcurrentDictionary<string, LibraryDependencyInfo> LibraryDependencyCache = new();

        private readonly HttpClient httpClient;

        public NuGetRemoteDependencyProvider(HttpClient httpClient)
        {
            this.httpClient = httpClient;
        }

        public bool IsHttp { get; } = true;

        public PackageSource Source { get; } = new("https://api.nuget.org/v3/index.json");

        internal ICollection<LibraryDependencyInfo> PackagesToInstall { get; } = new List<LibraryDependencyInfo>();

        internal ICollection<PackageLicenseInfo> PackagesToAcceptLicense { get; } = new List<PackageLicenseInfo>();

        public static void AddBaseAssemblyPackageDependenciesToCache(IDictionary<string, string> assemblyPackageVersionMappings)
        {
            if (assemblyPackageVersionMappings == null)
            {
                return;
            }

            foreach (var (packageName, packageVersion) in assemblyPackageVersionMappings)
            {
                var libraryIdentity = new LibraryIdentity(
                    packageName,
                    new NuGetVersion(packageVersion),
                    LibraryType.Package);

                var libraryDependencyInfo = new LibraryDependencyInfo(
                    libraryIdentity,
                    resolved: true,
                    FrameworkConstants.CommonFrameworks.Net50,
                    Array.Empty<LibraryDependency>());

                LibraryDependencyCache.TryAdd(libraryIdentity.Name, libraryDependencyInfo);
            }
        }

        public Task<LibraryIdentity> FindLibraryAsync(
            LibraryRange libraryRange,
            NuGetFramework targetFramework,
            SourceCacheContext cacheContext,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            // We are validating the name, version and target framework upon getting them on UI so we skip second validation here
            return Task.FromResult(new LibraryIdentity(
                libraryRange.Name,
                libraryRange.VersionRange.MinVersion,
                LibraryType.Package));
        }

        public async Task<LibraryDependencyInfo> GetDependenciesAsync(
            LibraryIdentity libraryIdentity,
            NuGetFramework targetFramework,
            SourceCacheContext cacheContext,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (LibraryDependencyCache.TryGetValue(libraryIdentity.Name, out var dependencyInfo))
            {
                // TODO: handle the case when the constraint is not >=
                if (dependencyInfo.Library.Version >= libraryIdentity.Version)
                {
                    return dependencyInfo;
                }

                throw new NotSupportedException(
                    $"Installing package '{dependencyInfo.Library.Name}' v{libraryIdentity.Version} is not currently supported because v{dependencyInfo.Library.Version} is already installed.");

                // Some thoughts:
                // differentiate the deps which comes from the project from those which comes from the current walking

                // we should separate the cache in 3 different collection
                // 1. libraries from project
                // 2. libraries from different nuget installations
                // 3. libraries from current walking

                // if we have downgrade from the versions in type 1 -> throw
                // if we have downgrade from the versions in type 2 -> check if the outside package can work with the current version that the current walking want to install.
                //    if yes -> download new version, change the version of collection type 2 in the cache and flag this library. After the process is finished, we should get the marked libraries and change the sources in the cache
                //    if not -> throw
            }

            const string NuGetNuspecEndpointFormat = "https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.nuspec";

            var nuspecStream = await this.httpClient.GetStreamAsync(
                string.Format(NuGetNuspecEndpointFormat, libraryIdentity.Name, libraryIdentity.Version),
                cancellationToken);

            var nuspecReader = new NuspecReader(nuspecStream);

            var dependencyGroup = NuGetFrameworkUtility.GetNearest(
                nuspecReader.GetDependencyGroups(useStrictVersionCheck: false),
                targetFramework,
                item => item.TargetFramework);

            var dependencies = dependencyGroup?.Packages?.Select(PackagingUtility.GetLibraryDependencyFromNuspec).ToList();

            var libraryDependencyInfo = new LibraryDependencyInfo(
                libraryIdentity,
                resolved: true,
                dependencyGroup?.TargetFramework ?? NuGetFramework.AnyFramework,
                dependencies ?? Enumerable.Empty<LibraryDependency>());

            if (LibraryDependencyCache.TryAdd(libraryIdentity.Name, libraryDependencyInfo))
            {
                this.PackagesToInstall.Add(libraryDependencyInfo);

                if (nuspecReader.GetRequireLicenseAcceptance())
                {
                    var authors = nuspecReader.GetAuthors();
                    var licenseMetadata = nuspecReader.GetLicenseMetadata();
                    var licenseUrl = nuspecReader.GetLicenseUrl() ?? licenseMetadata?.LicenseUrl?.ToString();

                    this.PackagesToAcceptLicense.Add(new PackageLicenseInfo
                    {
                        Package = libraryIdentity.Name,
                        License = licenseMetadata?.License,
                        LicenseUrl = licenseUrl,
                        Authors = authors,
                    });
                }
            }

            return libraryDependencyInfo;
        }

        public Task<IPackageDownloader> GetPackageDownloaderAsync(
            PackageIdentity packageIdentity,
            SourceCacheContext cacheContext,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IEnumerable<NuGetVersion>> GetAllVersionsAsync(
            string id,
            SourceCacheContext cacheContext,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        internal void ClearPackagesToInstall(bool clearFromCache = false)
        {
            if (clearFromCache)
            {
                foreach (var package in this.PackagesToInstall)
                {
                    LibraryDependencyCache.TryRemove(package.Library.Name, out _);
                }
            }

            this.PackagesToInstall.Clear();
            this.PackagesToAcceptLicense.Clear();
        }
    }
}