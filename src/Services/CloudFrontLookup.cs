using System.Text.RegularExpressions;
using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.Runtime;
using RegionEndpoint = Amazon.RegionEndpoint;

namespace screengrab.Services;

/// <summary>A CloudFront distribution that serves a bucket.</summary>
/// <param name="Id">The distribution's id, for invalidations.</param>
/// <param name="Domain">The first alternate domain name (CNAME), or the dxxxx.cloudfront.net name.</param>
/// <param name="OriginPath">The distribution's origin path, e.g. "/site", or "".</param>
/// <param name="RequiresSignedUrls">True when plain links won't work (trusted key groups / signers).</param>
public sealed record BucketDistribution(string Id, string Domain, string OriginPath, bool RequiresSignedUrls)
{
    /// <summary>The CloudFront URL for an object key, or null if the key is outside the origin path.</summary>
    public string? UrlFor(string key) => PathFor(key) is { } path ? $"https://{Domain}{path}" : null;

    /// <summary>The URL path ("/x") that serves an object key, or null if the key is outside the origin path.</summary>
    public string? PathFor(string key)
    {
        // Origin path "/site" means https://domain/x serves the key "site/x".
        var prefix = OriginPath.Trim('/');
        if (prefix.Length > 0)
        {
            if (!key.StartsWith(prefix + "/", StringComparison.Ordinal))
                return null;
            key = key[(prefix.Length + 1)..];
        }
        return "/" + S3Uploader.EncodeKey(key);
    }
}

/// <summary>
/// Finds the CloudFront distribution in front of a bucket, by listing the account's distributions
/// and matching the origin of each one's default behavior to the bucket (as S3 File Explorer
/// does). Needs the cloudfront:ListDistributions permission; without it none is found.
/// </summary>
public static class CloudFrontLookup
{
    // bucket.s3.amazonaws.com, bucket.s3.us-east-1.amazonaws.com, bucket.s3-website-us-east-1.amazonaws.com, ...
    private static readonly Regex S3OriginDomain = new(
        @"^(?<bucket>.+?)\.s3([.-][a-z0-9-]+)*\.amazonaws\.com(\.cn)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The best enabled distribution serving <paramref name="bucket"/> with plain links: the
    /// bucket's own domain first, then any custom domain, then dxxxx.cloudfront.net. Null if
    /// there's none, or the distributions can't be listed.
    /// </summary>
    public static async Task<BucketDistribution?> FindAsync(AWSCredentials credentials, string bucket, CancellationToken cancellationToken)
    {
        var found = (await FindAllAsync(credentials, bucket, cancellationToken)).Where(d => !d.RequiresSignedUrls);
        return found.OrderByDescending(d => Rank(d, bucket.ToLowerInvariant())).FirstOrDefault();
    }

    /// <summary>
    /// Every enabled distribution serving <paramref name="bucket"/>, signed or not. Empty if
    /// there's none, or the distributions can't be listed.
    /// </summary>
    public static async Task<List<BucketDistribution>> FindAllAsync(AWSCredentials credentials, string bucket, CancellationToken cancellationToken)
    {
        bucket = bucket.ToLowerInvariant();
        var found = new List<BucketDistribution>();
        try
        {
            // CloudFront is a global service; its API lives in us-east-1.
            using var client = new AmazonCloudFrontClient(credentials, RegionEndpoint.USEast1);
            var request = new ListDistributionsRequest();
            DistributionList? list;
            do
            {
                list = (await client.ListDistributionsAsync(request, cancellationToken)).DistributionList;
                foreach (var distribution in list?.Items ?? [])
                {
                    if (Match(distribution, bucket) is { } match)
                        found.Add(match);
                }
                request.Marker = list?.NextMarker;
            }
            while (list?.IsTruncated == true);
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            // No permission, or CloudFront unreachable: use the S3 link instead.
            return [];
        }
        return found;
    }

    /// <summary>
    /// Asks CloudFront to drop its cached copies of an object key, so a deleted file stops being
    /// served. Needs cloudfront:CreateInvalidation. Does nothing for a key outside the origin path.
    /// CloudFront finishes the invalidation by itself, usually within a few minutes.
    /// </summary>
    public static async Task InvalidateAsync(AWSCredentials credentials, BucketDistribution distribution, string key,
        CancellationToken cancellationToken)
    {
        if (distribution.PathFor(key) is not { } path)
            return;
        using var client = new AmazonCloudFrontClient(credentials, RegionEndpoint.USEast1);
        await client.CreateInvalidationAsync(new CreateInvalidationRequest
        {
            DistributionId = distribution.Id,
            InvalidationBatch = new InvalidationBatch
            {
                CallerReference = Guid.NewGuid().ToString("N"),
                Paths = new Paths { Quantity = 1, Items = [path] },
            },
        }, cancellationToken);
    }

    private static BucketDistribution? Match(DistributionSummary distribution, string bucket)
    {
        if (distribution.Enabled != true)
            return null;

        var behavior = distribution.DefaultCacheBehavior;
        var origin = distribution.Origins?.Items?.FirstOrDefault(o => o.Id == behavior?.TargetOriginId);
        var match = S3OriginDomain.Match(origin?.DomainName ?? "");
        if (origin is null || !match.Success || !match.Groups["bucket"].Value.Equals(bucket, StringComparison.OrdinalIgnoreCase))
            return null;

        var aliases = distribution.Aliases?.Items?.Where(a => !a.StartsWith('*')).ToList() ?? [];
        // A bucket named after a domain (cache.example.com) is served on that domain, even when the
        // distribution lists another one (example.com) first.
        var alias = aliases.FirstOrDefault(a => a.Equals(bucket, StringComparison.OrdinalIgnoreCase))
            ?? aliases.FirstOrDefault();
        return new BucketDistribution(
            distribution.Id,
            alias ?? distribution.DomainName,
            origin.OriginPath ?? "",
            behavior?.TrustedKeyGroups?.Enabled == true || behavior?.TrustedSigners?.Enabled == true);
    }

    /// <summary>2 for the bucket's own domain, 1 for another custom domain, 0 for dxxxx.cloudfront.net.</summary>
    private static int Rank(BucketDistribution distribution, string bucket) =>
        distribution.Domain.Equals(bucket, StringComparison.OrdinalIgnoreCase) ? 2
        : distribution.Domain.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase) ? 0
        : 1;
}
