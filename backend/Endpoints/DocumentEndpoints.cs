using Harness.Data;
using Harness.Services;
using Microsoft.EntityFrameworkCore;

namespace Harness.Endpoints;

// How far the search index has come, for Settings › Documents.
public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/documents", async (ChatDb db, Embeddings embeddings, DocumentIndexer indexer, CancellationToken ct) =>
            Results.Ok(new
            {
                model = embeddings.Model,
                installed = await embeddings.AvailableAsync(ct),
                working = indexer.Working,
                documents = await db.Documents.CountAsync(d => d.Error == null, ct),
                passages = await db.DocumentChunks.CountAsync(ct),
                updated = await db.Documents.MaxAsync(d => (DateTime?)d.IndexedAt, ct),
                failed = await db.Documents.Where(d => d.Error != null).OrderBy(d => d.Name).Take(20)
                    .Select(d => new { d.Name, d.Error }).ToListAsync(ct)
            }));
        app.MapPost("/api/documents/reindex", (DocumentIndexer indexer) =>
        {
            indexer.Trigger();
            return Results.Accepted();
        });
    }
}
