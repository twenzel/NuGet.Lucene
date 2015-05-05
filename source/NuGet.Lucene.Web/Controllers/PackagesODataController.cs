﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Http;
using System.Web.Http.OData;
using System.Web.Http.OData.Query;
using NuGet.Lucene.Web.Models;
using NuGet.Lucene.Web.Util;

namespace NuGet.Lucene.Web.Controllers
{
    /// <summary>
    /// OData provider for Lucene based NuGet package repository.
    /// This is the primary interface for the NuGet Command Line client,
    /// Visual Studio Package Manager and Package Manager Console.
    /// </summary>
    public class PackagesODataController : ODataController
    {
        private static readonly ODataQuerySettings SearchQuerySettings = new ODataQuerySettings
        {
            HandleNullPropagation = HandleNullPropagationOption.False,
            EnsureStableOrdering = true
        };

        private const int DefaultSearchPageSize = 30;

        public IMirroringPackageRepository Repository { get; set; }

        [EnableQuery(PageSize = 20, HandleNullPropagation = HandleNullPropagationOption.False)]
        public IQueryable<ODataPackage> Get()
        {
            return Repository.GetPackages().Select(p => p.ToODataPackage()).AsQueryable();
        }

        public HttpResponseMessage GetCount(ODataQueryOptions<ODataPackage> options)
        {
            var queryResults = (IQueryable<ODataPackage>)options.ApplyTo(Get());
            var count = queryResults.Count();

            return OkCount(count);
        }

        public IHttpActionResult Get([FromODataUri] string id, [FromODataUri] string version)
        {
            return Get(id, version, p => p.ToODataPackage());
        }

        [HttpGet]
        public IHttpActionResult GetId([FromODataUri] string id, [FromODataUri] string version)
        {
            return Get(id, version, p => p.Id);
        }

        protected IHttpActionResult Get<T>(string id, string version, Func<IPackage, T> transform)
        {
            SemanticVersion semanticVersion;
            if (!SemanticVersion.TryParse(version, out semanticVersion))
            {
                return BadRequest("Invalid version");
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Invalid package id");
            }

            var package = Repository.FindPackage(id, semanticVersion);

            return package == null ? (IHttpActionResult)NotFound() : Ok(transform(package));
        }

        [HttpPost]
        [HttpGet]
        public IEnumerable<ODataPackage> Search(
            [FromODataUri] string searchTerm,
            [FromODataUri] string targetFramework,
            [FromODataUri] bool includePrerelease,
            ODataQueryOptions<ODataPackage> options)
        {
            var pageSize = options.Top != null ? (int?)null : DefaultSearchPageSize;
            var settings = new ODataQuerySettings(SearchQuerySettings) {PageSize = pageSize};
            return Search(searchTerm, targetFramework, includePrerelease, options, settings);
        }

        [HttpGet]
        public HttpResponseMessage CountSearch(
            [FromODataUri] string searchTerm,
            [FromODataUri] string targetFramework,
            [FromODataUri] bool includePrerelease,
            ODataQueryOptions<ODataPackage> options)
        {
            var queryResults = Search(searchTerm, targetFramework, includePrerelease, options, SearchQuerySettings);

            var count = queryResults.Count();

            return OkCount(count);
        }

        private IEnumerable<ODataPackage> Search(
            string searchTerm,
            string targetFramework,
            bool includePrerelease,
            ODataQueryOptions<ODataPackage> options,
            ODataQuerySettings settings)
        {
            var targetFrameworks = Enumerable.Empty<string>();

            options = SimplifyOrderingClause(options);

            if (!string.IsNullOrWhiteSpace(targetFramework))
            {
                targetFrameworks = targetFramework.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Distinct();
                options = RemoveFilter(options);
            }

            var searchQuery = Repository.Search(searchTerm, targetFrameworks, includePrerelease);

            var odataQuery = from package in searchQuery select package.ToODataPackage();

            return (IEnumerable<ODataPackage>)options.ApplyTo(odataQuery, settings);
        }

        private ODataQueryOptions<ODataPackage> SimplifyOrderingClause(ODataQueryOptions<ODataPackage> options)
        {
            if (options.OrderBy == null) return options;

            var uriBuilder = new UriBuilder(options.Request.RequestUri);
            uriBuilder.Query = uriBuilder.Query
                .Substring(1)
                .Replace("$orderby=concat(Title,Id)", "$orderby=DisplayTitle");

            return new ODataQueryOptions<ODataPackage>(options.Context, new HttpRequestMessage(Request.Method, uriBuilder.Uri));
        }

        private ODataQueryOptions<ODataPackage> RemoveFilter(ODataQueryOptions<ODataPackage> options)
        {
            if (options.Filter == null) return options;

            var uriBuilder = new UriBuilder(options.Request.RequestUri);
            uriBuilder.Query = Regex.Replace(uriBuilder.Query.Substring(1), @"\$filter=(IsLatestVersion|IsAbsoluteLatestVersion)", "", RegexOptions.IgnoreCase);
            return new ODataQueryOptions<ODataPackage>(options.Context, new HttpRequestMessage(Request.Method, uriBuilder.Uri));
        }

        [HttpPost]
        [HttpGet]
        [EnableQuery(PageSize = 100, HandleNullPropagation = HandleNullPropagationOption.False)]
        public IHttpActionResult FindPackagesById([FromODataUri] string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Parameter 'id' must not be empty");
            }

            return Ok(Repository.FindPackagesById(id)
                        .Select(p => p.ToODataPackage())
                        .AsQueryable());
        }

        [HttpPost]
        [HttpGet]
        [EnableQuery(PageSize = 100, HandleNullPropagation = HandleNullPropagationOption.False)]
        public IHttpActionResult GetUpdates(
            [FromODataUri] string packageIds,
            [FromODataUri] string versions,
            [FromODataUri] bool includePrerelease,
            [FromODataUri] bool includeAllVersions,
            [FromODataUri] string targetFrameworks,
            [FromODataUri] string versionConstraints)
        {
            if (String.IsNullOrEmpty(packageIds) || String.IsNullOrEmpty(versions))
            {
                return BadRequest("Parameters 'packageIds' and 'versions' must not be empty.");
            }

            var idValues = packageIds.Trim().Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            var versionValues = versions.Trim().Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            var versionConstraintValues = string.IsNullOrEmpty(versionConstraints)
                                            ? new string[idValues.Length]
                                            : versionConstraints.Trim().Split(new[] { '|' });

            if ((idValues.Length == 0) || (idValues.Length != versionValues.Length) || (idValues.Length != versionConstraintValues.Length))
            {
                return BadRequest("Count of items in parameters 'packageIds', 'version' and 'versionContraints' do not match.");
            }

            var packages = idValues
                .Zip(
                    versionValues.Select(v => new SemanticVersion(v)),
                    (id, version) => new PackageSpec { Id = id, Version = version })
                .ToList();

            var targetFrameworkValues = (targetFrameworks ?? "")
                .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(VersionUtility.ParseFrameworkName)
                .ToList();

            var versionSpecs = versionConstraintValues
                .Select((v, i) => CreateVersionSpec(v, packages[i].Version))
                .ToList();

            var updates = Repository.GetUpdates(packages, includePrerelease, includeAllVersions, targetFrameworkValues, versionSpecs);
            return Ok(updates.Select(p => p.ToODataPackage()).AsQueryable());
        }

        protected virtual IVersionSpec CreateVersionSpec(string constraint, SemanticVersion currentVersion)
        {
            if (!string.IsNullOrWhiteSpace(constraint))
            {
                return VersionUtility.ParseVersionSpec(constraint);
            }

            return new VersionSpec { MinVersion = currentVersion, IsMinInclusive = false };
        }

        /// <summary>
        /// Creates a text/plain response that contains a count of items.
        /// </summary>
        protected virtual HttpResponseMessage OkCount(int count)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(count.ToString(CultureInfo.InvariantCulture), Encoding.UTF8, "text/plain")
            };
        }

    }
}
