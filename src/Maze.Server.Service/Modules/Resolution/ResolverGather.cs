using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using Maze.ModuleManagement.PackageManagement;
using Maze.Server.Service.Modules.PackageManagement;

namespace Maze.Server.Service.Modules.Resolution
{
    public class ResolverGather
    {
        private readonly GatherContext _context;
        private readonly List<Task<GatherResult>> _workerTasks;
        private int _maxDegreeOfParallelism;
        private readonly GatherCache _cache;
        private readonly List<SourceResource> _primaryResources = new List<SourceResource>();
        private readonly List<SourceResource> _allResources = new List<SourceResource>();
        private DependencyInfoResource _packagesFolderResource;
        private readonly HashSet<string> _idsSearched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _lastRequestId = -1;
        private readonly Queue<GatherRequest> _gatherRequests = new Queue<GatherRequest>();
        private readonly ConcurrentDictionary<string, TimeSpan> _timeTaken =
            new ConcurrentDictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        private readonly List<GatherResult> _results = new List<GatherResult>();

        private ResolverGather(GatherContext context)
        {
            _context = context;

            _maxDegreeOfParallelism = PackageManagementConstants.DefaultMaxDegreeOfParallelism;
            RequestTimeout = PackageManagementConstants.DefaultRequestTimeout;

            _workerTasks = new List<Task<GatherResult>>(_maxDegreeOfParallelism);

            _cache = _context.ResolutionContext?.GatherCache;
        }

        /// <summary>
        ///     Maximum number of threads to use when gathering packages.
        /// </summary>
        /// <remarks>The value must be >= 1.</remarks>
        public int MaxDegreeOfParallelism
        {
            get => _maxDegreeOfParallelism;
            set => _maxDegreeOfParallelism = Math.Max(1, value);
        }

        /// <summary>
        ///     Timeout when waiting for source requests
        /// </summary>
        public TimeSpan RequestTimeout { get; set; }

        /// <summary>
        /// Gather packages
        /// </summary>
        //Use static definition because instance can only be used once
        public static async Task<HashSet<SourcePackageDependencyInfo>> GatherAsync(GatherContext context,
            CancellationToken token)
        {
            var engine = new ResolverGather(context);
            return await engine.GatherAsync(token);
        }

        public async Task<HashSet<SourcePackageDependencyInfo>> GatherAsync(CancellationToken token)
        {
            // preserve start time of gather api
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            token.ThrowIfCancellationRequested();

            // get a distinct set of packages from all repos
            var combinedResults = new HashSet<SourcePackageDependencyInfo>(PackageIdentity.Comparer);

            // Initialize dependency info resources in parallel
            await InitializeResourcesAsync(token);

            // resolve primary targets only from primary sources
            foreach (var primaryTarget in _context.PrimaryTargets)
            {
                // Add the id to the search list to block searching for all versions
                //_idsSearched.Add(primaryTarget.Id);

                QueueWork(_primaryResources, primaryTarget, ignoreExceptions: false, isInstalledPackage: false);
            }

            // walk the dependency graph both upwards and downwards for the new package
            // this is done in multiple passes to find the complete closure when
            // new dependecies are found
            while (true)
            {
                token.ThrowIfCancellationRequested();

                // Start tasks for queued requests and process finished results.
                await StartTasksAndProcessWork(token);

                // Completed results
                var currentItems = _results.ToList();

                // Get a unique list of packages
                // Results are ordered by their request order. If the same version of package
                // exists in multiple sources the hashset will contain the package from the 
                // source where it was requested from first.
                var currentResults = new HashSet<SourcePackageDependencyInfo>(
                    currentItems.OrderBy(item => item.Request.Order).SelectMany(item => item.Packages),
                    PackageIdentity.Comparer);

                // Remove downgrades if the flag is not set, this will skip unneeded dependencies from older versions
                if (!_context.AllowDowngrades)
                {
                    foreach (var primaryTarget in _context.PrimaryTargets)
                    {
                        if (!primaryTarget.HasVersion)
                            continue;

                        // Clear out all versions of the installed package which are less than the installed version
                        currentResults.RemoveWhere(package => string.Equals(primaryTarget.Id, package.Id, StringComparison.OrdinalIgnoreCase)
                            && package.Version < primaryTarget.Version);
                    }
                }

                // Find all installed packages, these may have come from a remote source
                // if they were not found on disk, so it is not possible to compute this up front.
                var installedInfo = new HashSet<SourcePackageDependencyInfo>(
                    currentItems.Where(item => item.Request.IsInstalledPackage)
                        .OrderBy(item => item.Request.Order)
                        .SelectMany(item => item.Packages),
                        PackageIdentity.Comparer);

                // Find the closure of all parent and child packages around the targets
                // Skip walking dependencies when the behavior is set to ignore
                if (_context.ResolutionContext?.DependencyBehavior != DependencyBehavior.Ignore)
                {
                    var closureIds = GetClosure(currentResults, installedInfo, _idsSearched);

                    // Find all ids in the closure that have not been gathered
                    var missingIds = closureIds.Except(_idsSearched, StringComparer.OrdinalIgnoreCase);

                    // Gather packages for all missing ids
                    foreach (var missingId in missingIds)
                    {
                        QueueWork(_allResources, missingId, ignoreExceptions: true);
                    }
                }

                // We are done when the queue is empty, and the number of finished requests matches the total request count
                if (_gatherRequests.Count < 1 && _workerTasks.Count < 1)
                {
                    _context.Log.LogDebug($"Total number of results gathered : {_results.Count}");
                    break;
                }
            }

            token.ThrowIfCancellationRequested();

            // Order sources by their request order
            foreach (var result in _results.OrderBy(result => result.Request.Order))
            {
                // Merge the results, taking on the first instance of each package
                combinedResults.UnionWith(result.Packages);
            }

            _context.Log.LogMinimal(
                $"Gathering information about {combinedResults.Count} packages took {stopWatch.ElapsedMilliseconds} ms");

            return combinedResults;
        }

        /// <summary>
        /// Find the closure of required package ids
        /// </summary>
        private static HashSet<string> GetClosure(HashSet<SourcePackageDependencyInfo> combinedResults,
            HashSet<SourcePackageDependencyInfo> installedPackages, HashSet<string> idsSearched)
        {
            var closureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // find all dependencies of packages that we have expanded, and search those also
            closureIds.UnionWith(combinedResults.Where(package => idsSearched.Contains(package.Id))
                .SelectMany(package => package.Dependencies)
                .Select(dependency => dependency.Id));

            // expand all parents of expanded packages
            closureIds.UnionWith(combinedResults.Where(
                    package => package.Dependencies.Any(dependency => idsSearched.Contains(dependency.Id)))
                .Select(package => package.Id));

            // all unique ids gathered so far
            var currentResultIds = new HashSet<string>(combinedResults.Select(package => package.Id),
                StringComparer.OrdinalIgnoreCase);

            // installed packages must be gathered to find a complete solution
            closureIds.UnionWith(installedPackages.Select(package => package.Id)
                .Where(id => !currentResultIds.Contains(id)));

            // if any dependencies are completely missing they must be retrieved
            closureIds.UnionWith(combinedResults.SelectMany(package => package.Dependencies)
                .Select(dependency => dependency.Id).Where(id => !currentResultIds.Contains(id)));

            return closureIds;
        }

        /// <summary>
        /// Start tasks for queued requests and process finished tasks.
        /// This method will continue until at least 1 task has finished,
        /// and keep going until all queued requests have been started.
        /// </summary>
        private async Task StartTasksAndProcessWork(CancellationToken token)
        {
            // Start new tasks and process the work at least once
            // Continuing looping under the number of tasks has gone
            // below the limit. While we are at the limit there is no 
            // need to queue up additional work.
            do
            {
                token.ThrowIfCancellationRequested();

                // Run queued work
                StartWorkerTasks(token);

                // Wait for at least one of the tasks to finish before processing results
                if (_workerTasks.Count > 0)
                {
                    await Task.WhenAny(_workerTasks);
                }

                // Retrieve results from finished tasks
                await ProcessResultsAsync();
            }
            while (_workerTasks.Count >= MaxDegreeOfParallelism);

            // Start more tasks after processing
            StartWorkerTasks(token);
        }

        /// <summary>
        /// Retrieve results from completed tasks
        /// </summary>
        private async Task ProcessResultsAsync()
        {
            var currentTasks = _workerTasks.ToArray();

            foreach (var task in currentTasks)
            {
                if (task.IsCompleted || task.IsFaulted || task.IsCanceled)
                {
                    _workerTasks.Remove(task);

                    // Await the task to throw any exceptions that may have occurred
                    var gatherResult = await task;

                    _results.Add(gatherResult);
                }
            }
        }

        /// <summary>
        /// Load up to the MaxThread count
        /// </summary>
        private void StartWorkerTasks(CancellationToken token)
        {
            while (_workerTasks.Count < MaxDegreeOfParallelism && _gatherRequests.Count > 0)
            {
                var request = _gatherRequests.Dequeue();
                var task = Task.Run(async () => await GatherPackageAsync(request, token));
                _workerTasks.Add(task);
            }
        }

        /// <summary>
        /// Retrieve the packages from the cache or source
        /// </summary>
        private async Task<GatherResult> GatherPackageAsync(GatherRequest request, CancellationToken token)
        {
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();
            token.ThrowIfCancellationRequested();

            var packages = new List<SourcePackageDependencyInfo>();
            GatherCacheResult cacheResult = null;
            var packageSource = request.Source.Source.PackageSource;

            // Gather packages from cache
            if (_cache != null)
            {
                if (request.Package.HasVersion)
                    cacheResult = _cache.GetPackage(packageSource, request.Package, request.Framework);
                else
                    cacheResult = _cache.GetPackages(packageSource, request.Package.Id, request.Framework);
            }

            // ReSharper disable once PossibleNullReferenceException
            if (_cache != null && cacheResult.HasEntry)
            {
                // Use cached packages
                _context.Log.LogDebug(
                    $"Package {request.Package.Id} from source {request.Source.Source.PackageSource.Name} gathered from cache.");
                packages.AddRange(cacheResult.Packages);
            }
            else
            {
                // No cache entry exists, request it from the source
                try
                {
                    using (var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        // stop requests after a timeout
                        linkedTokenSource.CancelAfter(RequestTimeout);

                        // Gather packages from source if it was not in the cache

                        packages = await GatherPackageFromSourceAsync(
                            request.Package.Id,
                            request.Package.Version,
                            request.Source.Resource,
                            request.Framework,
                            request.IgnoreExceptions,
                            linkedTokenSource.Token);
                        
                        // add packages to the cache
                        if (_cache != null)
                        {
                            if (request.Package.HasVersion)
                            {
                                _cache.AddPackageFromSingleVersionLookup(
                                    packageSource,
                                    request.Package,
                                    request.Framework,
                                    packages.FirstOrDefault());
                            }
                            else
                            {
                                _cache.AddAllPackagesForId(
                                    packageSource,
                                    request.Package.Id,
                                    request.Framework,
                                    packages);
                            }
                        }
                    }
                }
                catch (TaskCanceledException ex)
                {
                    if (!ex.CancellationToken.IsCancellationRequested)
                    {
                        string message =
                            $"Unable to gather package '{request.Package.Id}' from source '{request.Source.Source.PackageSource.Source}'. Please verify all your online package sources are available.";
                        throw new InvalidOperationException(message, ex);
                    }
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is OperationCanceledException || ex is TaskCanceledException)
                {
                    string message =
                        $"Unable to gather package '{request.Package.Id}' from source '{request.Source.Source.PackageSource.Source}'. Please verify all your online package sources are available.";
                    throw new InvalidOperationException(message, ex);
                }

                // it maintain each source total time taken so far
                stopWatch.Stop();
                _timeTaken.AddOrUpdate(request.Source.Source.PackageSource.Source, stopWatch.Elapsed, (k, v) => stopWatch.Elapsed + v);
            }

            return new GatherResult(request, packages);
        }

        /// <summary>
        /// Call the DependencyInfoResource safely
        /// </summary>
        private async Task<List<SourcePackageDependencyInfo>> GatherPackageFromSourceAsync(string packageId,
            NuGetVersion version, DependencyInfoResource resource, NuGetFramework targetFramework,
            bool ignoreExceptions, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var results = new List<SourcePackageDependencyInfo>();

            try
            {
                // Call the dependecy info resource
                if (version == null)
                {
                    // find all versions of a package
                    var packages = await resource.ResolvePackages(packageId, targetFramework,
                        _context.ResolutionContext.SourceCacheContext, _context.Log, token);

                    results.AddRange(packages);
                }
                else
                {
                    // find a single package id and version
                    var identity = new PackageIdentity(packageId, version);
                    var package = await resource.ResolvePackage(identity, targetFramework,
                        _context.ResolutionContext.SourceCacheContext, _context.Log, token);

                    if (package != null)
                    {
                        results.Add(package);
                    }
                }
            }
            catch
            {
                // Secondary sources should not stop the gather process. They are often invalid
                // such as scenarios where a UNC is offline.
                if (!ignoreExceptions)
                {
                    throw;
                }
            }

            return results;
        }

        private void QueueWork(IReadOnlyList<SourceResource> sources, string packageId, bool ignoreExceptions)
        {
            var identity = new PackageIdentity(packageId, version: null);
            QueueWork(sources, identity, ignoreExceptions, isInstalledPackage: false);
        }

        private void QueueWork(IReadOnlyList<SourceResource> sources, PackageIdentity package, bool ignoreExceptions, bool isInstalledPackage)
        {
            // No-op if the id has already been searched for
            // Exact versions are not added to the list since we may need to search for the full 
            // set of packages for that id later if it becomes part of the closure later.
            if (package.HasVersion || _idsSearched.Add(package.Id))
            {
                foreach (var source in sources)
                {
                    // Keep track of the order in which these were made
                    var requestId = GetNextRequestId();
                    var isPrimarySource = IsPrimarySource(source);
                    var framework = isPrimarySource ? _context.PrimaryFramework : _context.DependencyFramework;

                    var request = new GatherRequest(source, package, ignoreExceptions, requestId, isInstalledPackage,
                        framework);

                    // Order is important here
                    _gatherRequests.Enqueue(request);
                }
            }
        }

        private bool IsPrimarySource(SourceResource sourceResource) => _primaryResources.Contains(sourceResource);

        /// <summary>
        /// Get the current request id number, and increment it for the next count
        /// </summary>
        private int GetNextRequestId()
        {
            return ++_lastRequestId;
        }

        /// <summary>
        /// Initialize the PackageSource resources to gather the dependencies
        /// </summary>
        private async Task InitializeResourcesAsync(CancellationToken token)
        {
            var currentSource = string.Empty;
            try
            {
                // get the dependency info resources for each repo
                // primary and all may share the same resources
                var getResourceTasks = new List<Task>();

                var allSources = new List<SourceRepository>();

                allSources.AddRange(_context.PrimarySources);
                allSources.Add(_context.PackagesFolderSource);
                allSources.AddRange(_context.DependencySources);

                var depResources = new Dictionary<SourceRepository, Task<DependencyInfoResource>>();
                foreach (var source in allSources)
                {
                    if (!depResources.ContainsKey(source))
                    {
                        var task = Task.Run(async () => await source.GetResourceAsync<DependencyInfoResource>(token));

                        getResourceTasks.Add(task);
                        depResources.Add(source, task);

                        // Limit the number of tasks to MaxThreads by awaiting each time we hit the limit
                        while (getResourceTasks.Count >= MaxDegreeOfParallelism)
                        {
                            var finishedTask = await Task.WhenAny(getResourceTasks);

                            getResourceTasks.Remove(finishedTask);
                        }
                    }
                }

                var uniquePrimarySources = new HashSet<PackageSource>();
                foreach (var source in _context.PrimarySources)
                {
                    if (uniquePrimarySources.Add(source.PackageSource))
                    {
                        currentSource = source.PackageSource.Source;

                        var resource = await depResources[source];
                        _primaryResources.Add(new SourceResource(source, resource));
                    }
                }

                // All sources - for fallback
                var uniqueAllSources = new HashSet<PackageSource>();
                foreach (var source in allSources)
                {
                    if (uniqueAllSources.Add(source.PackageSource))
                    {
                        currentSource = source.PackageSource.Source;

                        var resource = await depResources[source];
                        _allResources.Add(new SourceResource(source, resource));
                    }
                }

                // Installed packages resource
                _packagesFolderResource = await depResources[_context.PackagesFolderSource];
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is OperationCanceledException ||
                                       ex is InvalidOperationException || ex is AggregateException)
            {
                var message =
                    $"Exception '{ex.GetType()}' thrown when trying to add source '{currentSource}'. Please verify all your online package sources are available.";
                throw new InvalidOperationException(message, ex);
            }
        }

        /// <summary>
        /// Holds a Source and DependencyInfoResource
        /// </summary>
        private class SourceResource
        {
            public SourceResource(SourceRepository source, DependencyInfoResource resource)
            {
                Source = source;
                Resource = resource;
            }

            public SourceRepository Source { get; }
            public DependencyInfoResource Resource { get; }
        }

        /// <summary>
        ///     Request info
        /// </summary>
        private class GatherRequest
        {
            public GatherRequest(SourceResource source, PackageIdentity package, bool ignoreExceptions, int order,
                bool isInstalledPackage, NuGetFramework framework)
            {
                Source = source;
                Package = package;
                IgnoreExceptions = ignoreExceptions;
                Order = order;
                IsInstalledPackage = isInstalledPackage;
                Framework = framework;
            }

            public NuGetFramework Framework { get; }
            public SourceResource Source { get; }
            public PackageIdentity Package { get; }
            public bool IgnoreExceptions { get; }
            public int Order { get; }
            public bool IsInstalledPackage { get; }
        }

        /// <summary>
        ///     Contains the original request along with the resulting packages.
        /// </summary>
        private class GatherResult
        {
            public GatherResult(GatherRequest request, IReadOnlyList<SourcePackageDependencyInfo> packages)
            {
                Request = request;
                Packages = packages;
            }

            public GatherRequest Request { get; }
            public IReadOnlyList<SourcePackageDependencyInfo> Packages { get; }
        }
    }
}