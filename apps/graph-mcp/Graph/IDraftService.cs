using GraphMcp.Infrastructure;
using GraphMcp.Models;

namespace GraphMcp.Graph;

public interface IDraftService
{
    Task<DraftDto> CreateAsync(CreateDraftRequest request, DraftWriteState state, CancellationToken cancellationToken);
    Task<DraftDto> CreateReplyAsync(ReplyDraftRequest request, DraftWriteState state, CancellationToken cancellationToken);
    Task<DraftDto> CreateReplyAllAsync(ReplyDraftRequest request, DraftWriteState state, CancellationToken cancellationToken);
    Task<DraftDto> CreateForwardAsync(ForwardDraftRequest request, DraftWriteState state, CancellationToken cancellationToken);
    Task<DraftDto> UpdateAsync(UpdateDraftRequest request, DraftWriteState state, CancellationToken cancellationToken);
}
