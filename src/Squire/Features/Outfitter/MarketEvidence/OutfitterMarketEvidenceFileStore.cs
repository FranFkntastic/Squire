using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.Squire.Outfitter.MarketEvidence;

public sealed class OutfitterMarketEvidenceFileStore : IOutfitterMarketEvidenceBookStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string path;

    public OutfitterMarketEvidenceFileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<OutfitterMarketEvidenceBook?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        var book = await JsonSerializer.DeserializeAsync<OutfitterMarketEvidenceBook>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (book is not null && !string.Equals(book.SchemaVersion, OutfitterMarketEvidenceBook.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported market evidence schema '{book.SchemaVersion}'.");
        return book;
    }

    public async Task SaveAsync(OutfitterMarketEvidenceBook book, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (!book.IsPublishable)
            throw new InvalidOperationException("Only complete market evidence generations may be persisted as published books.");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32_768, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, book, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
