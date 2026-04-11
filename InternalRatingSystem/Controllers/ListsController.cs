using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Jellyfin.Plugin.InternalRating.Models;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.InternalRating.Controllers
{
    /// <summary>
    /// Collaborative user lists — any user on the server can create a list,
    /// and other users can contribute items to it if it's marked collaborative.
    /// The list owner can always add/remove anything; contributors can only
    /// remove items they themselves added.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/StarTrack")]
    [Produces(MediaTypeNames.Application.Json)]
    public class ListsController : ControllerBase
    {
        private readonly ListsRepository _repo;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<ListsController> _logger;

        public ListsController(IAuthorizationContext authContext, ILogger<ListsController> logger)
        {
            _repo        = Plugin.Instance!.Lists;
            _authContext = authContext;
            _logger      = logger;
        }

        [HttpGet("Lists")]
        [ProducesResponseType(typeof(List<UserList>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAllLists()
        {
            var all = await _repo.GetAllAsync().ConfigureAwait(false);
            return Ok(all);
        }

        [HttpGet("Lists/{listId}")]
        [ProducesResponseType(typeof(UserList), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetList([FromRoute] string listId)
        {
            var l = await _repo.GetByIdAsync(listId).ConfigureAwait(false);
            if (l == null) return NotFound();
            return Ok(l);
        }

        [HttpPost("Lists")]
        [ProducesResponseType(typeof(UserList), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CreateList([FromBody] CreateListRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("name is required");

            var l = await _repo.CreateAsync(
                userId.Value.ToString("N"),
                GetCurrentUserName(),
                req.Name!,
                req.Description,
                req.Collaborative ?? true
            ).ConfigureAwait(false);
            _logger.LogInformation("[StarTrack] {User} created list '{Name}'", GetCurrentUserName(), l.Name);
            return Ok(l);
        }

        [HttpDelete("Lists/{listId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> DeleteList([FromRoute] string listId)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var deleted = await _repo.DeleteAsync(listId, userId.Value.ToString("N")).ConfigureAwait(false);
            return deleted ? Ok() : Forbid();
        }

        [HttpPost("Lists/{listId}/Items")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> AddItem([FromRoute] string listId, [FromBody] AddListItemRequest req)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.ItemId)) return BadRequest("itemId is required");
            var added = await _repo.AddItemAsync(
                listId,
                userId.Value.ToString("N"),
                GetCurrentUserName(),
                req.ItemId!
            ).ConfigureAwait(false);
            return Ok(new { added });
        }

        [HttpDelete("Lists/{listId}/Items/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> RemoveItem([FromRoute] string listId, [FromRoute] string itemId)
        {
            var userId = await GetCurrentUserIdAsync().ConfigureAwait(false);
            if (userId == null) return Unauthorized();
            var removed = await _repo.RemoveItemAsync(listId, userId.Value.ToString("N"), itemId).ConfigureAwait(false);
            return removed ? Ok() : Forbid();
        }

        // ============================== Helpers ================================== //

        private async Task<Guid?> GetCurrentUserIdAsync()
        {
            try
            {
                var info = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
                if (info?.UserId != null && info.UserId != Guid.Empty)
                    return info.UserId;
            }
            catch { }
            var value = User.FindFirst("Jellyfin-UserId")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(value, out var id) ? id : null;
        }

        private string GetCurrentUserName()
        {
            return User.FindFirst("Jellyfin-User")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                ?? User.Identity?.Name
                ?? "Unknown";
        }
    }
}
