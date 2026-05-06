namespace YMMProjectManager.Presentation.Timeline;

public static class YmmTimelineInstanceDisposer
{
    public static async Task<(bool Succeeded, string? FailureReason)> DisposeAsync(object? instance)
    {
        if (instance is null)
        {
            return (true, null);
        }

        try
        {
            if (instance is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(true);
                return (true, null);
            }

            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
                return (true, null);
            }

            var method = instance.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method is not null)
            {
                method.Invoke(instance, null);
                return (true, null);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
