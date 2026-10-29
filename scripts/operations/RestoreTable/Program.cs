namespace RepoAgent.RestoreTable;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            var result = await RestoreCommand.RunAsync(args, deadline.Token);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, System.Text.Json.JsonSerializerOptions.Web));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Provider errors and row/artifact contents can contain private material.
            Console.Error.WriteLine("Restore Table operation refused or unconfirmed; inspect private operator state. No automatic retry or repair was attempted.");
            return 1;
        }
    }
}
