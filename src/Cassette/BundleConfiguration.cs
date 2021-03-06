﻿#region License
/*
Copyright 2011 Andrew Davey

This file is part of Cassette.

Cassette is free software: you can redistribute it and/or modify it under the 
terms of the GNU General Public License as published by the Free Software 
Foundation, either version 3 of the License, or (at your option) any later 
version.

Cassette is distributed in the hope that it will be useful, but WITHOUT ANY 
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS 
FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

You should have received a copy of the GNU General Public License along with 
Cassette. If not, see http://www.gnu.org/licenses/.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using Cassette.IO;
using Cassette.Persistence;
using Cassette.Utilities;
using CreateBundleContainer = System.Func<bool, Cassette.IBundleContainer>;
// CreateBundleContainer    = useCache => BundleContainer<BundleType>

namespace Cassette
{
    public class BundleConfiguration
    {
        public BundleConfiguration(ICassetteApplication application, IDirectory cacheDirectory, IDirectory sourceDirectory, Dictionary<Type, object> bundleFactories, string version)
        {
            this.application = application;
            this.cacheDirectory = cacheDirectory;
            this.sourceDirectory = sourceDirectory;
            this.bundleFactories = bundleFactories;
            this.version = version;
        }

        readonly ICassetteApplication application;
        readonly Dictionary<Type, Tuple<object, CreateBundleContainer>> bundleSourceResultsByType = new Dictionary<Type, Tuple<object, CreateBundleContainer>>();
        readonly IDirectory cacheDirectory;
        readonly IDirectory sourceDirectory;
        readonly Dictionary<Type, object> bundleFactories;
        readonly string version;
        readonly Dictionary<Type, List<Action<object>>> customizations = new Dictionary<Type, List<Action<object>>>();

        public void Add<T>(params IBundleSource<T>[] bundleSources)
            where T : Bundle
        {
            foreach (var bundleSource in bundleSources)
            {
                Add(bundleSource);
            }
        }

        public bool ContainsBundleSources(Type bundleType)
        {
            return bundleSourceResultsByType.ContainsKey(bundleType);
        }

        void Add<T>(IBundleSource<T> bundleSource)
            where T : Bundle
        {
            var result = bundleSource.GetBundles(GetBundleFactory<T>(), application);

            Tuple<object, CreateBundleContainer> existingTuple;
            if (bundleSourceResultsByType.TryGetValue(typeof(T), out existingTuple))
            {
                var existingResult = (IEnumerable<T>)existingTuple.Item1;
                var existingAction = existingTuple.Item2;
                // Concat the two bundle collections.
                // Keep the existing initialization action.
                bundleSourceResultsByType[typeof(T)] = Tuple.Create(
                    (object)existingResult.Concat(result),
                    existingAction
                );
            }
            else
            {
                bundleSourceResultsByType[typeof(T)] = Tuple.Create<object, CreateBundleContainer>(
                    result,
                    CreateBundleContainer<T>
                );
            }
        }

        public Dictionary<Type, IBundleContainer> CreateBundleContainers(bool useCache)
        {
            return bundleSourceResultsByType.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Item2(useCache)
            );
        }

        IBundleContainer CreateBundleContainer<T>(bool useCache)
            where T : Bundle
        {
            var bundles = ((IEnumerable<Bundle>)bundleSourceResultsByType[typeof(T)].Item1).ToArray();
            if (useCache)
            {
                return GetOrCreateCachedBundleContainer(bundles);
            }
            else
            {
                return CreateBundleContainer(bundles);
            }
        }

        IBundleContainer GetOrCreateCachedBundleContainer(Bundle[] bundles)
        {
            var cache = GetBundleCache();
            if (cache.InitializeBundlesFromCacheIfUpToDate(bundles))
            {
                return new BundleContainer(ConvertUrlReferencesToBundles(bundles));
            }
            else
            {
                var container = CreateBundleContainer(bundles);
                cache.SaveBundleContainer(container);
                cache.InitializeBundlesFromCacheIfUpToDate(bundles);
                return container;
            }
        }

        IBundleContainer CreateBundleContainer(IEnumerable<Bundle> bundles)
        {
            var bundlesArray = bundles.ToArray();
            List<Action<object>> customizeActions;
            if (customizations.TryGetValue(typeof(Bundle), out customizeActions))
            {
                foreach (var customize in customizeActions)
                {
                    foreach (var bundle in bundlesArray)
                    {
                        customize(bundle);
                    }
                }
            }
            ProcessAll(bundlesArray);
            return new BundleContainer(ConvertUrlReferencesToBundles(bundlesArray));
        }

        IEnumerable<Bundle> ConvertUrlReferencesToBundles<T>(T[] bundles) where T : Bundle
        {
            var bundlePaths = new HashSet<string>(bundles.Select(m => m.Path), StringComparer.OrdinalIgnoreCase);

            foreach (var bundle in bundles)
            {
                yield return bundle;

                foreach (var reference in bundle.References)
                {
                    if (reference.IsUrl() == false) continue;
                    if (bundlePaths.Contains(reference)) continue;
                    
                    bundlePaths.Add(reference);
                    yield return GetBundleFactory<T>().CreateExternalBundle(reference);
                }

                var urlReferences = bundle.Assets
                    .SelectMany(asset => asset.References)
                    .Where(r => r.Type == AssetReferenceType.Url);

                foreach (var reference in urlReferences)
                {
                    if (bundlePaths.Contains(reference.Path)) continue;

                    var urlBundle = GetBundleFactory<T>().CreateExternalBundle(reference.Path);
                    bundlePaths.Add(urlBundle.Path);
                    yield return urlBundle;
                }
            }
        }

        void ProcessAll(IEnumerable<Bundle> bundles)
        {
            foreach (var bundle in bundles)
            {
                bundle.Process(application);
            }
        }

        IBundleCache GetBundleCache()
        {
            return new BundleCache(
                version,
                cacheDirectory,
                sourceDirectory
            );
        }

        IBundleFactory<T> GetBundleFactory<T>()
            where T : Bundle
        {
            return (IBundleFactory<T>)bundleFactories[typeof(T)];
        }
    }
}