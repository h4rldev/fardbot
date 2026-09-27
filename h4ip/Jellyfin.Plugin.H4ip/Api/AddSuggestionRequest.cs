namespace Jellyfin.Plugin.H4ip.Api;

/// <summary>
/// Request body for adding a suggestion.
/// </summary>
public class AddSuggestionRequest
{
    /// <summary>
    /// Gets or sets the artist name for the suggestion.
    /// </summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional notes for the suggestion.
    /// </summary>
    public string? Notes { get; set; }
}
