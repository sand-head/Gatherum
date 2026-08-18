using Gatherum.Core.Services;

namespace Gatherum.Web.Services;

/// <summary>Runs each UI operation in its own DI scope so a Blazor circuit never shares
/// a DbContext between concurrent event handlers.</summary>
public sealed class AppOperations(IServiceScopeFactory scopes)
{
    public Task<T> Nodes<T>(Func<NodeService, Task<T>> action) => Run(action);
    public Task Nodes(Func<NodeService, Task> action) => Run(action);
    public Task<T> Files<T>(Func<FileService, Task<T>> action) => Run(action);
    public Task Files(Func<FileService, Task> action) => Run(action);
    public Task<T> Search<T>(Func<SearchService, Task<T>> action) => Run(action);
    public Task<T> Keys<T>(Func<ApiKeyService, Task<T>> action) => Run(action);
    public Task Keys(Func<ApiKeyService, Task> action) => Run(action);
    public Task<T> Users<T>(Func<UserService, Task<T>> action) => Run(action);

    private async Task<T> Run<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task Run<TService>(Func<TService, Task> action) where TService : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }
}
