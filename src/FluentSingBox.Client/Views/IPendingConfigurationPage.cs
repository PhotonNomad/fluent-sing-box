namespace FluentSingBox.Client.Views;

// Navigation waits for the current editor so the next page loads the committed source.
internal interface IPendingConfigurationPage
{
    Task<bool> FlushPendingChangesAsync();
}
