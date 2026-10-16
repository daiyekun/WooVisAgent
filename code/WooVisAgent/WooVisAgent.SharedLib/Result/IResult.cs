namespace WooVisAgent.SharedLib.Result;

public interface IResult
{
    IEnumerable<object>? Errors { get; }

    bool IsSuccess { get; }

    ResultStatus Status { get; }

    object? GetValue();
}