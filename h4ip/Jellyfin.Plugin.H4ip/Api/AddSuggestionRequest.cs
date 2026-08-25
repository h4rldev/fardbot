namespace Jellyfin.Plugin.H4ip.Api;

/// <summary>
/// Request body for adding a suggestion.
/// </summary>
public class AddSuggestionRequest
{
    /// <summary>
    /// Gets or sets the artist name.
    /// </summary>
    public string Artist { get; set; } = string.Empty;
}
