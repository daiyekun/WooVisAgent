namespace WooVisAgent.SharedLib.Result;

public class Result<T> : IResult
{
    protected internal Result(T value)
    {
        Value = value;
    }

    protected internal Result(ResultStatus status)
    {
        Status = status;
    }

    public T? Value { get; init; }

    public bool IsSuccess => Status == ResultStatus.Ok;

    public IEnumerable<object>? Errors { get; protected set; }

    public ResultStatus Status { get; protected set; } = ResultStatus.Ok;

    public object? GetValue()
    {
        return Value;
    }

    public static implicit operator Result<T>(Result result)
    {
        return new Result<T>(default(T))
        {
            Status = result.Status,
            Errors = result.Errors
        };
    }
}

public class Result : Result<Result>
{
    protected internal Result(Result value) : base(value)
    {
    }

    protected internal Result(ResultStatus status) : base(status)
    {
    }

    public static Result From(IResult result)
    {
        return new Result(result.Status)
        {
            Errors = result.Errors
        };
    }

    public static Result Success()
    {
        return new Result(ResultStatus.Ok);
    }

    public static Result<T> Success<T>(T value)
    {
        return new Result<T>(value);
    }

    public static Result Failure()
    {
        return new Result(ResultStatus.Error);
    }

    public static Result Failure(params object[] errors)
    {
        return new Result(ResultStatus.Error)
        {
            Errors = errors.AsEnumerable()
        };
    }

    public static Result NotFound()
    {
        return new Result(ResultStatus.NotFound);
    }

    public static Result NotFound(params string[] error)
    {
        return new Result(ResultStatus.NotFound)
        {
            Errors = error.AsEnumerable()
        };
    }

    public static Result Forbidden()
    {
        return new Result(ResultStatus.Forbidden);
    }

    public static Result Unauthorized(params string[] errors)
    {
        return new Result(ResultStatus.Unauthorized)
        {
            Errors = errors
        };
    }

    public static Result Invalid()
    {
        return new Result(ResultStatus.Invalid);
    }

    public static Result Invalid(params string[] errors)
    {
        return new Result(ResultStatus.Invalid)
        {
            Errors = errors
        };
    }
}