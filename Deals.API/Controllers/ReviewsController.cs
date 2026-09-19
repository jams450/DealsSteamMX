using Deals.API.Models.Reviews;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Deals.API.Controllers;

/// <summary>
/// Personal reviews written by their owner, so the policy is <c>UserWithId</c> (never <c>AdminWithId</c>).
/// Validation lives in <see cref="IReviewService"/> and surfaces as 400 through the global handler.
/// </summary>
[ApiController]
[Route("api/reviews")]
[Authorize(Policy = "UserWithId")]
public class ReviewsController : ControllerBase
{
    private readonly IReviewService _reviewService;
    private readonly ICurrentUserService _currentUserService;

    public ReviewsController(IReviewService reviewService, ICurrentUserService currentUserService)
    {
        _reviewService = reviewService;
        _currentUserService = currentUserService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] long gameId, CancellationToken cancellationToken)
    {
        if (gameId <= 0)
        {
            throw new ArgumentException("gameId es obligatorio", nameof(gameId));
        }

        var reviews = await _reviewService.GetForGameAsync(
            _currentUserService.GetRequiredUserId(),
            gameId,
            cancellationToken);

        return Ok(reviews.Select(ReviewResponse.From));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] ReviewCreateRequest request,
        CancellationToken cancellationToken)
    {
        var review = await _reviewService.CreateAsync(
            _currentUserService.GetRequiredUserId(),
            new GameReviewInput(
                request.GameId,
                request.Platform,
                request.StartedMonth,
                request.FinishedMonth,
                request.Score,
                request.IsGoty,
                request.Body,
                request.Status),
            cancellationToken);

        return CreatedAtAction(nameof(Get), new { gameId = review.GameId }, ReviewResponse.From(review));
    }

    [HttpPut("{reviewId:int}")]
    public async Task<IActionResult> Update(
        int reviewId,
        [FromBody] ReviewUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var review = await _reviewService.UpdateAsync(
            _currentUserService.GetRequiredUserId(),
            reviewId,
            new GameReviewUpdate(
                request.StartedMonth,
                request.FinishedMonth,
                request.Score,
                request.IsGoty,
                request.Body,
                request.Status),
            cancellationToken);

        return review is null ? NotFound() : Ok(ReviewResponse.From(review));
    }

    [HttpDelete("{reviewId:int}")]
    public async Task<IActionResult> Delete(int reviewId, CancellationToken cancellationToken)
    {
        var deleted = await _reviewService.DeleteAsync(
            _currentUserService.GetRequiredUserId(),
            reviewId,
            cancellationToken);

        return deleted ? NoContent() : NotFound();
    }
}
