﻿using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Configuration;

namespace ApiReviewDotNet.Services
{
    public sealed class RepositoryGroupService
    {
        public RepositoryGroupService(IConfiguration configuration)
        {
            RepositoryGroups = RepositoryGroup.Get(configuration.GetSection("RepositoryGroups"));
            Repositories = RepositoryGroups.SelectMany(rg => rg.Repos.Select(r => r.FullName))
                                           .Distinct(StringComparer.OrdinalIgnoreCase)
                                           .Select(OrgAndRepo.Parse)
                                           .ToArray();
        }

        public IReadOnlyList<RepositoryGroup> RepositoryGroups { get; }
        public IReadOnlyList<OrgAndRepo> Repositories { get; }

    }
}
