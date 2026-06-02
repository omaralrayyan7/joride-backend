using JoRideBackend.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/otp")]
public class OtpController : ControllerBase
{
    private readonly IOtpService _otp;
    private readonly ISmsService _sms;
    private readonly IEmailService _email;

    public OtpController(IOtpService otp, ISmsService sms, IEmailService email)
    {
        _otp = otp;
        _sms = sms;
        _email = email;
    }

    public record SendSmsOtpRequest(string PhoneNumber);
    public record SendEmailOtpRequest(string Email);
    public record VerifySmsOtpRequest(string PhoneNumber, string Code);
    public record VerifyEmailOtpRequest(string Email, string Code);

    // POST /api/otp/send
    [HttpPost("send")]
    public async Task<IActionResult> SendSms([FromBody] SendSmsOtpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber))
            return BadRequest("Phone number is required.");

        var code = _otp.GenerateAndStoreCode(request.PhoneNumber);
        var message = $"Your JoRide security code is: {code}. It expires in 5 minutes.";

        try
        {
            await _sms.SendSmsAsync(request.PhoneNumber, message);
            return Ok(new { message = "OTP sent via SMS." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to send SMS OTP.", error = ex.Message });
        }
    }

    // POST /api/otp/send-email
    [HttpPost("send-email")]
    public async Task<IActionResult> SendEmail([FromBody] SendEmailOtpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Email is required.");

        var code = _otp.GenerateAndStoreCode(request.Email);

        try
        {
            await _email.SendAsync(
                request.Email,
                "Your JoRide verification code",
                $"Your JoRide security code is: {code}\nIt expires in 5 minutes.");

            return Ok(new { message = "OTP sent via email." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to send email OTP.", error = ex.Message });
        }
    }

    // POST /api/otp/verify
    [HttpPost("verify")]
    public IActionResult VerifySms([FromBody] VerifySmsOtpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber) || string.IsNullOrWhiteSpace(request.Code))
            return BadRequest("Phone number and code are required.");

        var ok = _otp.VerifyCode(request.PhoneNumber, request.Code);
        if (!ok)
            return BadRequest(new { verified = false, message = "Code is wrong or expired." });

        var user = UsersController.GetUserByPhone(request.PhoneNumber);
        if (user is not null)
        {
            user.IsPhoneVerified = true;
            _ = UsersController.SaveUser(user);
        }

        return Ok(new { verified = true, message = "Phone verified successfully." });
    }

    // POST /api/otp/verify-email
    [HttpPost("verify-email")]
    public IActionResult VerifyEmail([FromBody] VerifyEmailOtpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code))
            return BadRequest("Email and code are required.");

        var ok = _otp.VerifyCode(request.Email, request.Code);
        if (!ok)
            return BadRequest(new { verified = false, message = "Code is wrong or expired." });

        // Mark the user's email as verified
        var user = UsersController.GetUser(request.Email);
        if (user is not null)
        {
            user.IsEmailVerified = true;
            _ = UsersController.SaveUser(user);
        }

        return Ok(new { verified = true, message = "Email verified successfully." });
    }
}