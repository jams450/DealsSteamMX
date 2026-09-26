using Deals.API.Models.Jobs;
using Deals.BusinessLogic.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Deals.API.Controllers;

[ApiController]
[Route("api/jobs")]
[Authorize(Policy = "AdminWithId")]
public sealed class JobsController(JobRunQueryService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<JobRunHistoryResponse>> Get([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var result = await service.QueryAsync(page, pageSize, cancellationToken);
        return Ok(new JobRunHistoryResponse(
            new(result.Summary.Total, result.Summary.Running, result.Summary.Ok, result.Summary.Failed, result.Summary.LastStartedAt),
            result.Items.Select(item => new JobRunListItemResponse(item.JobRunId, item.Job, item.Trigger, item.Status, item.StartedAt, item.FinishedAt, item.DurationMilliseconds, item.Details)).ToList(),
            result.Page, result.PageSize, result.TotalPages));
    }
}
