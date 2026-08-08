using JoRideBackend.Models.Payments;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Security-critical: the only HTTP surface that can actuate a vehicle. See
/// DeviceCommandService for the state machine and the non-bypassable audit guarantee.
/// Vehicles have no renter/owner linkage in the current codebase, so authorization is
/// Admin-only for now rather than inventing a "renter" concept that doesn't exist yet.
/// </summary>
[ApiController]
[Route("api/vehicles/{vehicleId:int}/commands")]
[Authorize]
public class DeviceCommandsController : ControllerBase
{
    private readonly DeviceCommandService _commands;

    public DeviceCommandsController(DeviceCommandService commands)
    {
        _commands = commands;
    }

    [HttpPost("{type}")]
    public async Task<IActionResult> RequestCommand(int vehicleId, string type, CancellationToken ct)
    {
        if (!Enum.TryParse<DeviceCommandType>(type, ignoreCase: true, out var commandType))
        {
            return BadRequest(new { error = $"Unknown command type '{type}'. Valid: unlock, lock, immobilize, mobilize." });
        }

        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var isAdmin = User.HasClaim("role", "admin");

        var command = await _commands.RequestCommandAsync(vehicleId, commandType, userId.Value, isAdmin, ct);

        var response = ToResponse(command);

        return command.State switch
        {
            DeviceCommandState.Unauthorized => StatusCode(StatusCodes.Status403Forbidden, response),
            DeviceCommandState.SafetyBlocked => Conflict(response),
            _ => Ok(response),
        };
    }

    [HttpGet("{commandId:guid}")]
    public async Task<IActionResult> GetCommand(int vehicleId, Guid commandId, CancellationToken ct)
    {
        var command = await _commands.GetCommandAsync(commandId, ct);
        if (command is null || command.VehicleId != vehicleId)
        {
            return NotFound();
        }

        return Ok(ToResponse(command));
    }

    private int? CurrentUserId()
    {
        var sub = User.FindFirst("sub")?.Value;
        return int.TryParse(sub, out var id) ? id : null;
    }

    private static object ToResponse(DeviceCommand command) => new
    {
        id = command.Id,
        vehicleId = command.VehicleId,
        type = command.Type.ToString(),
        state = command.State.ToString(),
        requestedByUserId = command.RequestedByUserId,
        requestedAt = command.RequestedAt,
        resolvedAt = command.ResolvedAt,
    };
}
