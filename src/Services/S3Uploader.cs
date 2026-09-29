using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using screengrab.Models;
using RegionEndpoint = Amazon.RegionEndpoint;

namespace screengrab.Services;

/// <summary>Uploads screenshots to the bucket and folder in <see cref="S3Settings"/>.</summary>
public static class S3Uploader
{
    /// <summary>The object key for a file name: the folder, a slash, then the name.</summary>
    public static string Key(S3Settings settings, string fileName)
    {
        var folder = settings.Folder.Trim().Trim('/');
        return folder.Length == 0 ? fileName : $"{folder}/{fileName}";
    }

    /// <summary>The bucket names the keys can see, for choosing one.</summary>
    public static async Task<IReadOnlyList<string>> ListBucketsAsync(S3Settings settings, CancellationToken cancellationToken = default)
    {
        using var client = Client(settings, settings.Region);
        var response = await client.ListBucketsAsync(cancellationToken);
        return (response.Buckets ?? []).Select(b => b.BucketName).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether something is already stored under <paramref name="key"/>.</summary>
    public static async Task<bool> ExistsAsync(S3Settings settings, string key, CancellationToken cancellationToken = default)
    {
        using var client = Client(settings, await BucketRegionAsync(settings, settings.Bucket, cancellationToken));
        try
        {
            await client.GetObjectMetadataAsync(settings.Bucket, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// Stores the bytes under <paramref name="key"/> as a public file (canned ACL public-read),
    /// replacing anything already there, and returns the file's link: on the bucket's CloudFront
    /// domain when a distribution serves it with plain links, otherwise the S3 URL.
    /// </summary>
    public static async Task<string> UploadAsync(S3Settings settings, string key, byte[] data, string contentType,
        CancellationToken cancellationToken = default)
    {
        var region = await BucketRegionAsync(settings, settings.Bucket, cancellationToken);
        using var client = Client(settings, region);
        using var stream = new MemoryStream(data);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            // Public, so the link works for anyone: the S3 URL, or CloudFront reading the bucket as a public origin.
            CannedACL = S3CannedACL.PublicRead,
        }, cancellationToken);

        var distribution = await CloudFrontLookup.FindAsync(Credentials(settings), settings.Bucket, cancellationToken);
        return distribution?.UrlFor(key) ?? PublicObjectUrl(settings.Bucket, region, key);
    }

    /// <summary>
    /// Deletes an uploaded file, with the keys in <paramref name="settings"/> (S3 reports success
    /// when it's already gone), then clears it from the cache of every CloudFront distribution
    /// serving the bucket. Throws if S3 refuses; returns null, or why the cache couldn't be
    /// cleared (the file is deleted all the same).
    /// </summary>
    public static async Task<string?> DeleteAsync(S3Settings settings, S3Upload upload, CancellationToken cancellationToken = default)
    {
        using (var client = Client(settings, await BucketRegionAsync(settings, upload.Bucket, cancellationToken)))
            await client.DeleteObjectAsync(upload.Bucket, upload.Key, cancellationToken);

        var credentials = Credentials(settings);
        try
        {
            foreach (var distribution in await CloudFrontLookup.FindAllAsync(credentials, upload.Bucket, cancellationToken))
                await CloudFrontLookup.InvalidateAsync(credentials, distribution, upload.Key, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            return $"CloudFront may keep showing {upload.Url} until its cache expires, as the invalidation " +
                $"failed (the keys need cloudfront:CreateInvalidation): {ex.Message}";
        }
    }

    /// <summary>
    /// A readable message for a failed upload, with advice for the two ways S3 refuses a public
    /// ACL (as S3 File Explorer explains them).
    /// </summary>
    public static string DescribeUploadError(AmazonServiceException ex) => ex switch
    {
        AmazonS3Exception { ErrorCode: "AccessControlListNotSupported" } =>
            "This bucket has ACLs turned off (Object Ownership: bucket owner enforced), so files can't be made " +
            "public one at a time. Turn ACLs on in the bucket's Permissions tab, or make it public with a bucket policy.",
        // S3 answers "access denied", even to the account's root user, when Block Public Access forbids the ACL.
        AmazonS3Exception { ErrorCode: "AccessDenied" } =>
            "S3 refused the public upload. This is almost always Block Public Access, on this bucket or for the " +
            "whole account (S3 console > Block Public Access settings for this account), or keys without " +
            $"s3:PutObjectAcl. S3 said: {ex.Message}",
        _ => $"S3 refused the upload: {ex.Message}",
    };

    /// <summary>
    /// The plain S3 URL of an object. Bucket names with dots use the path style, since the
    /// virtual-hosted style's HTTPS certificate doesn't cover them.
    /// </summary>
    public static string PublicObjectUrl(string bucket, string region, string key) =>
        bucket.Contains('.')
            ? $"https://s3.{region}.amazonaws.com/{bucket}/{EncodeKey(key)}"
            : $"https://{bucket}.s3.{region}.amazonaws.com/{EncodeKey(key)}";

    /// <summary>URL-encodes each part of an object key, keeping the "/" separators.</summary>
    public static string EncodeKey(string key) =>
        string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

    /// <summary>Buckets live in a specific region, and requests must go to that region.</summary>
    private static async Task<string> BucketRegionAsync(S3Settings settings, string bucket, CancellationToken cancellationToken)
    {
        try
        {
            using var client = Client(settings, settings.Region);
            var response = await client.GetBucketLocationAsync(bucket, cancellationToken);
            return response.Location?.Value switch
            {
                null or "" => "us-east-1",  // S3 reports us-east-1 as an empty location
                "EU" => "eu-west-1",        // legacy name
                var value => value,
            };
        }
        catch (AmazonS3Exception)
        {
            // No permission for GetBucketLocation: try the chosen region.
            return settings.Region;
        }
    }

    private static AmazonS3Client Client(S3Settings settings, string region) =>
        new(Credentials(settings), RegionEndpoint.GetBySystemName(region));

    private static AWSCredentials Credentials(S3Settings settings) =>
        new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey);
}
