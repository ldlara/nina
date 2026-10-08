namespace Nina.Bff;

public interface INinaApiClient
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}

public sealed class NinaApiClient(HttpClient http) : INinaApiClient
{
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri("/ready", UriKind.Relative), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
