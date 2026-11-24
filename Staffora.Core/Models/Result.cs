namespace Staffora.Core.Models
{
    public class Result
    {
        public bool IsSuccess { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
        public object? Value { get; set; }
        public static Result Success(object? value = null, string? message = null) => new() { IsSuccess = true, Message = message, Value = value };
        public static Result Failure(string? error = null) => new() { IsSuccess = false, Error = error };
    }

    public class Result<T> : Result
    {
        public new T? Value { get; init; }
        public static Result<T> Success(T? value, string? message = null) => new() { IsSuccess = true, Value = value, Message = message };
        public static new Result<T> Failure(string? error = null) => new() { IsSuccess = false, Error = error };
    }
}
