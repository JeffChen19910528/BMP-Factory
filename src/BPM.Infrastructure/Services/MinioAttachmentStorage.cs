using BPM.Application.Forms;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace BPM.Infrastructure.Services;

// Object storage backed by MinIO / any S3-compatible endpoint (Skill.md Phase 4 §28). Never
// stores bytes in Postgres — only the Attachment entity's metadata lives there; this is where the
// actual content goes.
public class MinioAttachmentStorage : IAttachmentStorage
{
    private readonly IMinioClient _client;
    private readonly string _bucketName;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private bool _bucketEnsured;

    public MinioAttachmentStorage(IOptions<MinioSettings> settings)
    {
        var config = settings.Value;
        _client = new MinioClient()
            .WithEndpoint(config.Endpoint)
            .WithCredentials(config.AccessKey, config.SecretKey)
            .WithSSL(config.UseSsl)
            .Build();
        _bucketName = config.BucketName;
    }

    public async Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);

        await _client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(storageKey)
            .WithStreamData(content)
            .WithObjectSize(content.Length)
            .WithContentType(contentType), cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var buffer = new MemoryStream();
        await _client.GetObjectAsync(new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(storageKey)
            .WithCallbackStream(stream => stream.CopyTo(buffer)), cancellationToken);

        buffer.Position = 0;
        return buffer;
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        await _client.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(storageKey), cancellationToken);
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (_bucketEnsured)
        {
            return;
        }

        await _bucketLock.WaitAsync(cancellationToken);
        try
        {
            if (_bucketEnsured)
            {
                return;
            }

            var exists = await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucketName), cancellationToken);
            if (!exists)
            {
                await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucketName), cancellationToken);
            }

            _bucketEnsured = true;
        }
        finally
        {
            _bucketLock.Release();
        }
    }
}
