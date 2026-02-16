namespace Nexus.Core.Models;

public class ApiResult {
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string? Message { get; set; }
    public string? ErrorDetail { get; set; }

    public bool IsAuthError => StatusCode == 401;
    public bool IsValidationError => StatusCode == 422;
    public bool IsServerError => StatusCode >= 500;

    public static ApiResult Ok( string? message = null ) => new() {
        Success = true,
        StatusCode = 200,
        Message = message
    };

    public static ApiResult Fail( int statusCode, string message, string? detail = null ) => new() {
        Success = false,
        StatusCode = statusCode,
        Message = message,
        ErrorDetail = detail
    };
}
