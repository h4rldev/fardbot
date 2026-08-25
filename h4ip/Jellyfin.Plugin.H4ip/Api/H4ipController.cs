using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.H4ip.Data;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.H4ip.Api;

/// <summary>
/// API endpoints for the h4ip integration.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("h4ip")]
[Produces(MediaTypeNames.Application.Json)]
public class H4ipController : ControllerBase
{
    private readonly H4ipRepository _repository;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="H4ipController"/> class.
    /// </summary>
    /// <param name="repository">Playcount/suggestion repository.</param>
    /// <param name="userManager">User manager.</param>
    public H4ipController(H4ipRepository repository, IUserManager userManager)
    {
        _repository = repository;
        _userManager = userManager;
    }

    /// <summary>
    /// Get artist suggestions.
    /// </summary>
    /// <param name="all">Whether to return all suggestions.</param>
    /// <returns>A list of suggestions.</returns>
    [HttpGet("suggestions")]
    public ActionResult<IEnumerable<object>> GetSuggestions([FromQuery] bool all = false)
    {
        var rows = _repository.GetSuggestions(pendingOnly: !all);
        return Ok(rows.Select(r => new { r.Id, r.Artist, r.AddedAt, r.Done }));
    }

    /// <summary>
    /// Adds a suggestion.
    /// </summary>
    /// <param name="request">The request body.</param>
    /// <returns>A successful response.</returns>
    [HttpPost("suggestions")]
    public ActionResult AddSuggestion([FromBody] AddSuggestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Artist))
        {
            return BadRequest("Artist name is required");
        }

        _repository.AddSuggestion(request.Artist);
        return Ok();
    }

    /// <summary>
    /// Marks a suggestion as done.
    /// </summary>
    /// <param name="artist">The artist name.</param>
    /// <returns>A successful response.</returns>
    [HttpDelete("suggestions/{artist}")]
    public ActionResult MarkSuggestionDone([FromRoute] string artist)
    {
        _repository.MarkSuggestionDone(artist);
        return Ok();
    }

    /// <summary>
    /// Gets the users with the most plays for an item.
    /// </summary>
    /// <param name="kind">The item type.</param>
    /// <param name="name">The item name.</param>
    /// <param name="limit">The number of listeners to return.</param>
    /// <returns>A list of top listeners.</returns>
    [HttpGet("crown")]
    public ActionResult<IEnumerable<object>> GetCrown([FromQuery] string kind, [FromQuery] string name, [FromQuery] int limit = 10)
    {
        if (!IsValidKind(kind))
        {
            return BadRequest("Kind must be artist, album, or track");
        }

        return Ok(_repository.GetTopListeners(kind, name, limit).Select(l =>
        {
            var user = Guid.TryParse(l.UserId, out var userId) ? _userManager.GetUserById(userId)?.Username : null;
            return (object)new { user = user ?? l.UserId, count = l.Count };
        }));
    }

    /// <summary>
    /// Gets the top listeners for an item.
    /// </summary>
    /// <param name="user">The user ID.</param>
    /// <param name="kind">The item type.</param>
    /// <param name="limit">The number of listeners to return.</param>
    /// <returns>A list of top listeners.</returns>
    [HttpGet("top")]
    public ActionResult<IEnumerable<object>> GetTop([FromQuery] string user, [FromQuery] string kind = "artist", [FromQuery] int limit = 10)
    {
        if (!IsValidKind(kind))
        {
            return BadRequest("Kind must be artist, album, or track");
        }

        return Ok(_repository.GetTopItems(user, kind, limit).Select(t => new { t.ItemName, t.Count }));
    }

    /// <summary>
    /// Checks if a kind is valid.
    /// </summary>
    /// <param name="kind">The kind to check.</param>
    /// <returns>True if the kind is valid, false otherwise.</returns>
    private static bool IsValidKind(string kind)
        => kind is "artist" or "album" or "track";
}
